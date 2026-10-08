using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Host;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P12-03, P12-04: the decomposition's join, on hand-built traces whose every segment is known. Three writes
/// (numbers 1 to 3, at indices 2 to 4, so a number taken for an index shows), each segment j of the
/// committing follower's path lasting (j + 1) * 10 us; the other follower answers later, so it must
/// not be the one decomposed. Sabotage S-lat-2 (each write joined to the next index, whose stamps are
/// the next write's).
/// </summary>
public sealed class DecompositionTests
{
    private static readonly NodeId Leader = new(1), Fast = new(2), Slow = new(3);
    private static readonly Term T1 = new(1);

    private static long Ticks(double micros) => (long)(micros * Stopwatch.Frequency / 1_000_000);

    private static byte[] Command(int n) => Encoding.ASCII.GetBytes($"Put|k{n}|v{n}");

    /// <summary>The traces and the generator's stamps; <paramref name="tamper"/> may change a stamp before it is added.</summary>
    private static (Dictionary<NodeId, IReadOnlyList<HandOffTrace.Stamp>> Traces, List<WriteStamp> Writes) Build(Func<NodeId, HandOffTrace.Stamp, int, HandOffTrace.Stamp>? tamper = null)
    {
        var traces = new Dictionary<NodeId, List<HandOffTrace.Stamp>> { [Leader] = [], [Fast] = [], [Slow] = [] };
        var writes = new List<WriteStamp>();
        for (var n = 1; n <= 3; n++)
        {
            var index = n + 1;
            var request = 100 + n;
            var at = new double[15];
            at[0] = n * 10_000;
            for (var j = 1; j < 15; j++)
            {
                at[j] = at[j - 1] + (j * 10);
            }

            void Add(NodeId node, double micros, HandOffTrace.Kind kind, long id, object? payload)
            {
                var s = new HandOffTrace.Stamp(Ticks(micros), kind, id, payload);
                traces[node].Add(tamper is null ? s : tamper(node, s, n));
            }

            var append = new AppendEntries(T1, Leader, index - 1, T1, [new LogEntry(T1, Command(n))], index - 1);
            var answer = new AppendEntriesResponse(T1, true, index);
            Add(Leader, at[3], HandOffTrace.Kind.ClientRead, request, Command(n));
            Add(Leader, at[4], HandOffTrace.Kind.ClientTaken, request, null);
            Add(Leader, at[5], HandOffTrace.Kind.Durable, 0, new ReadOnlyMemory<byte>(EntryLog.Record(index, T1, T1, Command(n))));
            foreach (var (f, late) in new[] { (Fast, 0.0), (Slow, 500.0) })
            {
                var appendRead = MessageCodec.Encode(append);
                var answerRead = MessageCodec.Encode(answer);
                Add(Leader, at[6], HandOffTrace.Kind.Sent, f.Value, Frames.Encode(MessageCodec.Encode(append)));
                Add(f, at[7], HandOffTrace.Kind.PeerRead, Leader.Value, appendRead);
                Add(f, at[8], HandOffTrace.Kind.PeerTaken, Leader.Value, appendRead);
                Add(f, at[9] + late, HandOffTrace.Kind.Durable, 0, new ReadOnlyMemory<byte>(EntryLog.Record(index, T1, T1, Command(n))));
                Add(f, at[10] + late, HandOffTrace.Kind.Sent, Leader.Value, Frames.Encode(answerRead));
                Add(Leader, at[11] + late, HandOffTrace.Kind.PeerRead, f.Value, answerRead);
                Add(Leader, at[12] + late, HandOffTrace.Kind.PeerTaken, f.Value, answerRead);
            }

            Add(Leader, at[13], HandOffTrace.Kind.Responded, request, null);
            writes.Add(new WriteStamp(n, Ticks(at[0]), Ticks(at[1]), Ticks(at[2]), Ticks(at[14]), at[14] - at[0]));
        }

        return (traces.ToDictionary(t => t.Key, t => (IReadOnlyList<HandOffTrace.Stamp>)t.Value), writes);
    }

    /// <summary>Every segment of every write is the planted time between its two hand-offs, taken from the follower that committed it. Sabotage S-lat-2.</summary>
    [Fact]
    public void EachSegmentIsTheTimeBetweenItsTwoHandOffs()
    {
        var (traces, writes) = Build();

        var d = Decomposition.Compute(traces, Leader, writes);

        Assert.Equal((3, 3, 0, 0), (d.Writes, d.Joined, d.Violations, d.Mismatched));
        for (var j = 0; j < Decomposition.Segments.Length; j++)
        {
            Assert.All(d.Micros[Decomposition.Segments[j]], m => Assert.Equal((j + 1) * 10, m, 1));
        }

        Assert.True(d.SumGap < 1e-6, $"sum gap {d.SumGap}");
    }

    /// <summary>A stamp joined by index whose entry is another write's: the write is counted as mismatched and not decomposed.</summary>
    [Fact]
    public void AWriteWhoseIndexLeadsToAnotherWritesEntryIsLeftOut()
    {
        var (traces, writes) = Build((node, s, n) => node == Fast && n == 2 && s.Kind == HandOffTrace.Kind.Durable
            ? s with { Payload = new ReadOnlyMemory<byte>(EntryLog.Record(3, T1, T1, Command(9))) }
            : s);

        var d = Decomposition.Compute(traces, Leader, writes);

        Assert.Equal((2, 1), (d.Joined, d.Mismatched));
    }

    /// <summary>A follower's take stamped before its read: a negative segment after the send, so the write is a violation, not a sample.</summary>
    [Fact]
    public void ANegativeSegmentAfterTheSendIsAViolation()
    {
        var (traces, writes) = Build((node, s, n) => node == Fast && n == 1 && s.Kind == HandOffTrace.Kind.PeerTaken ? s with { At = s.At - Ticks(100) } : s);

        var d = Decomposition.Compute(traces, Leader, writes);

        Assert.Equal((2, 1, 1), (d.Joined, d.Violations, d.NegativeBy["follower-queue"]));
    }

    /// <summary>A write dispatched early (P12-03's generator sent 14 to 20% of its writes before their time): its lateness is negative, and that is the instrument's, not a violation.</summary>
    [Fact]
    public void AnEarlyDispatchIsTheGeneratorsLatenessNotAViolation()
    {
        var (traces, writes) = Build();
        writes[0] = writes[0] with { Dispatched = writes[0].Due - Ticks(50) };

        var d = Decomposition.Compute(traces, Leader, writes);

        Assert.Equal((3, 0, 1), (d.Joined, d.Violations, d.NegativeBy["generator-late"]));
        Assert.Contains(d.Micros["generator-late"], m => Math.Abs(m + 50) < 0.1);
    }

    /// <summary>
    /// The trace through its bytes, as a Compose node's comes: the same decomposition. A peer message's
    /// read and take share one array, which is how they are joined, and a durable append's payload stays
    /// what it was (a first version returned every array as memory, and joined nothing).
    /// </summary>
    [Fact]
    public void ATraceDecomposesTheSameAfterItsBytes()
    {
        var (traces, writes) = Build();

        var read = traces.ToDictionary(t => t.Key, t => HandOffTrace.Read(HandOffTrace.Write(t.Value)));
        var d = Decomposition.Compute(read, Leader, writes);

        Assert.Equal((3, 0, 0), (d.Joined, d.Violations, d.Mismatched));
        Assert.Equal(Decomposition.Compute(traces, Leader, writes).Micros.SelectMany(m => m.Value), d.Micros.SelectMany(m => m.Value));
    }
}
