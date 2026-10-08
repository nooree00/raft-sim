using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Raft.Core;

namespace Raft.Host;

/// <summary>A write as the load generator saw it (P12-03): its number, when it was due, sent and answered, and the latency the generator measured.</summary>
public sealed record WriteStamp(int N, long Due, long Sent, long Answered, double LatencyMicros);

/// <summary>
/// P12-03, phase 12 decision 2: where a commit's latency goes. After a run, the hosts' hand-off
/// stamps (<see cref="HandOffTrace"/>) and the generator's are joined write by write: a write's
/// command (`Put|k..|v{n}`) names it in the leader's client read and in the leader's entry log, which
/// gives its index; its index names it in every append, answer and persist after that. Each write's
/// latency is then fourteen consecutive stamps, so its thirteen segments sum to it by construction.
/// That identity checks nothing about the join, so the guards are others: the share of writes
/// joined; content (every stamp joined by index is of an entry carrying the write's own command, so
/// a write joined to another's index is counted and left out, S-lat-2); causality (no segment from
/// the send on negative: the generator's own lateness is signed, since it sends early when less than
/// a millisecond remains); and the stamps' span against the generator's own latency for the same
/// writes. With two followers, the one whose answer the leader took first is the one that committed
/// the write.
/// </summary>
public static class Decomposition
{
    /// <summary>The segments, in order along a write's path.</summary>
    public static readonly string[] Segments =
    [
        "generator-late",
        "client-to-leader",
        "leader-queue",
        "leader-persist",
        "leader-to-send",
        "network-out",
        "follower-queue",
        "follower-persist",
        "follower-to-send",
        "network-back",
        "leader-queue-answer",
        "commit-respond",
        "response-to-client",
    ];

    /// <summary>
    /// The outcome: how many writes there were; how many were joined; how many were left out because a
    /// segment after the send was negative (Violations); the gap between the stamps' span and the
    /// generator's latency; and each segment's microseconds over the joined writes.
    /// </summary>
    public sealed record Result(int Writes, int Joined, int Violations, double SumGap, IReadOnlyDictionary<string, IReadOnlyList<double>> Micros)
    {
        /// <summary>For each segment, how many writes it was negative in: a stamp out of order, or, for the generator's, an early send.</summary>
        public IReadOnlyDictionary<string, int> NegativeBy { get; init; } = new Dictionary<string, int>();

        /// <summary>Writes left out because their index led to a stamp of an entry carrying another command: a wrong join.</summary>
        public int Mismatched { get; init; }
    }

    /// <summary>A stamp joined by index: when, the array it read (for a peer message's take), and the write number its entry carries (null for an answer, which carries none).</summary>
    private readonly record struct AtIndex(long At, byte[]? Array, int? Number);

    public static Result Compute(IReadOnlyDictionary<NodeId, IReadOnlyList<HandOffTrace.Stamp>> traces, NodeId leader, IReadOnlyList<WriteStamp> writes)
    {
        ArgumentNullException.ThrowIfNull(traces);
        ArgumentNullException.ThrowIfNull(writes);
        var lead = traces[leader];
        var followers = traces.Keys.Where(k => k != leader).ToList();

        // The leader's side by request: its client reads (named by the write's number), takes and responses.
        var reads = new Dictionary<int, (long Id, long At)>();
        var taken = new Dictionary<long, long>();
        var responded = new Dictionary<long, long>();
        foreach (var s in lead)
        {
            switch (s.Kind)
            {
                case HandOffTrace.Kind.ClientRead when s.Payload is byte[] line && WriteNumber(line) is { } n:
                    reads.TryAdd(n, (s.Id, s.At));
                    break;
                case HandOffTrace.Kind.ClientTaken:
                    taken.TryAdd(s.Id, s.At);
                    break;
                case HandOffTrace.Kind.Responded:
                    responded.TryAdd(s.Id, s.At);
                    break;
            }
        }

        // Each node's persists by index, with the write each record's command carries; the leader's give
        // each write its index.
        var durable = traces.ToDictionary(t => t.Key, t => Durable(t.Value));
        var byNumber = new Dictionary<int, long>();
        foreach (var (index, d) in durable[leader].OrderBy(d => d.Key))
        {
            if (d.Number is { } n)
            {
                byNumber.TryAdd(n, index);
            }
        }

        // Appends, answers and their reads by index, per direction; and each node's takes of peer messages by array.
        var appendsSent = followers.ToDictionary(f => f, f => Cover(lead.Where(s => s.Kind == HandOffTrace.Kind.Sent && s.Id == f.Value).Select(s => (s, Message(Frame(s.Payload)))), appends: true));
        var appendsRead = followers.ToDictionary(f => f, f => Cover(traces[f].Where(s => s.Kind == HandOffTrace.Kind.PeerRead && s.Id == leader.Value).Select(s => (s, Message(s.Payload as byte[]))), appends: true));
        var answersSent = followers.ToDictionary(f => f, f => Cover(traces[f].Where(s => s.Kind == HandOffTrace.Kind.Sent && s.Id == leader.Value).Select(s => (s, Message(Frame(s.Payload)))), appends: false));
        var answersRead = followers.ToDictionary(f => f, f => Cover(lead.Where(s => s.Kind == HandOffTrace.Kind.PeerRead && s.Id == f.Value).Select(s => (s, Message(s.Payload as byte[]))), appends: false));
        var peerTaken = traces.ToDictionary(t => t.Key, t => PeerTaken(t.Value));

        var micros = Segments.ToDictionary(s => s, _ => (IReadOnlyList<double>)new List<double>(), StringComparer.Ordinal);
        int joined = 0, violations = 0, mismatched = 0;
        var negative = Segments.ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
        double stampLatency = 0, generatorLatency = 0;
        foreach (var w in writes)
        {
            if (!reads.TryGetValue(w.N, out var read) || !taken.TryGetValue(read.Id, out var leaderTaken) || !responded.TryGetValue(read.Id, out var leaderResponded)
                || !byNumber.TryGetValue(w.N, out var index) || !durable[leader].TryGetValue(index, out var leaderDurable))
            {
                continue;
            }

            long[]? best = null;
            var wrong = leaderDurable.Number != w.N;
            foreach (var f in followers)
            {
                if (appendsSent[f].TryGetValue(index, out var sent) && appendsRead[f].TryGetValue(index, out var fRead) && peerTaken[f].TryGetValue(fRead.Array!, out var fTaken)
                    && durable[f].TryGetValue(index, out var fDurable) && answersSent[f].TryGetValue(index, out var answered)
                    && answersRead[f].TryGetValue(index, out var lRead) && peerTaken[leader].TryGetValue(lRead.Array!, out var lTaken))
                {
                    wrong |= sent.Number != w.N || fRead.Number != w.N || fDurable.Number != w.N;
                    if (best is null || lTaken < best[11])
                    {
                        best = [w.Due, w.Sent, read.At, leaderTaken, leaderDurable.At, sent.At, fRead.At, fTaken, fDurable.At, answered.At, lRead.At, lTaken, leaderResponded, w.Answered];
                    }
                }
            }

            if (best is null)
            {
                continue;
            }

            if (wrong)
            {
                mismatched++;
                continue;
            }

            var segments = new double[Segments.Length];
            var causal = true;
            for (var k = 0; k < segments.Length; k++)
            {
                segments[k] = Bench.Micros(best[k + 1] - best[k]);
                if (segments[k] < 0)
                {
                    // The generator's scheduling error is signed: it sends a write early when less than its
                    // wait threshold remains. Causality is the system's, from the send on.
                    causal &= k == 0;
                    negative[Segments[k]]++;
                }
            }

            if (!causal)
            {
                violations++;
                continue;
            }

            joined++;
            for (var k = 0; k < segments.Length; k++)
            {
                ((List<double>)micros[Segments[k]]).Add(segments[k]);
            }

            stampLatency += Bench.Micros(w.Answered - w.Due);
            generatorLatency += w.LatencyMicros;
        }

        var gap = joined == 0 || generatorLatency == 0 ? 1 : Math.Abs(stampLatency - generatorLatency) / generatorLatency;
        return new Result(writes.Count, joined, violations, gap, micros) { NegativeBy = negative, Mismatched = mismatched };
    }

    /// <summary>The number in a write's command, `Put|k..|v{n}`, or null for anything else.</summary>
    internal static int? WriteNumber(ReadOnlySpan<byte> command)
    {
        var text = Encoding.ASCII.GetString(command).TrimEnd('\n', '\r');
        var at = text.LastIndexOf("|v", StringComparison.Ordinal);
        return text.StartsWith("Put|", StringComparison.Ordinal) && at > 0 && int.TryParse(text.AsSpan(at + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>A node's first durable time per index and the write its record carries, read from the entry-log records each persist appended (EntryLog's framing: length, index, term, previous term, command, checksum).</summary>
    private static Dictionary<long, AtIndex> Durable(IReadOnlyList<HandOffTrace.Stamp> stamps)
    {
        var first = new Dictionary<long, AtIndex>();
        foreach (var s in stamps.Where(s => s.Kind == HandOffTrace.Kind.Durable && s.Payload is ReadOnlyMemory<byte>))
        {
            var data = ((ReadOnlyMemory<byte>)s.Payload!).Span;
            for (var off = 0; off + 4 <= data.Length;)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(data[off..]);
                if (length < 24 || off + 4 + length + 4 > data.Length)
                {
                    break;
                }

                var payload = data.Slice(off + 4, length);
                var index = BinaryPrimitives.ReadInt64BigEndian(payload);
                if (index > 0)
                {
                    first.TryAdd(index, new AtIndex(s.At, null, WriteNumber(payload[24..])));
                }

                off += 4 + length + 4;
            }
        }

        return first;
    }

    /// <summary>The message inside a frame (length, message, checksum).</summary>
    private static byte[]? Frame(object? payload) => payload is byte[] { Length: >= 8 } f ? f[4..^4] : null;

    private static Message? Message(byte[]? bytes) => bytes is null ? null : MessageCodec.Decode(bytes);

    /// <summary>
    /// The first stamp, per index, of a message that covers it: an append carrying entries covers
    /// their indices (and names each entry's write); a successful answer covers every index up to its
    /// match index.
    /// </summary>
    private static Dictionary<long, AtIndex> Cover(IEnumerable<(HandOffTrace.Stamp Stamp, Message? Message)> messages, bool appends)
    {
        var first = new Dictionary<long, AtIndex>();
        var high = -1L;
        foreach (var (s, m) in messages.OrderBy(x => x.Stamp.At))
        {
            var array = s.Payload as byte[];
            if (appends && m is AppendEntries ae)
            {
                for (var i = 0; i < ae.Entries.Count; i++)
                {
                    first.TryAdd(ae.PrevLogIndex + 1 + i, new AtIndex(s.At, array, WriteNumber(ae.Entries[i].Command)));
                }
            }
            else if (!appends && m is AppendEntriesResponse { Success: true } r && r.MatchIndex > high)
            {
                for (var i = Math.Max(high + 1, 1); i <= r.MatchIndex; i++)
                {
                    first.TryAdd(i, new AtIndex(s.At, array, null));
                }

                high = r.MatchIndex;
            }
        }

        return first;
    }

    /// <summary>When the loop took each peer message, by the array its read produced.</summary>
    private static Dictionary<byte[], long> PeerTaken(IReadOnlyList<HandOffTrace.Stamp> stamps)
    {
        var taken = new Dictionary<byte[], long>(ReferenceEqualityComparer.Instance);
        foreach (var s in stamps.Where(s => s.Kind == HandOffTrace.Kind.PeerTaken && s.Payload is byte[]))
        {
            taken.TryAdd((byte[])s.Payload!, s.At);
        }

        return taken;
    }

    /// <summary>A segment's mean, median and 99th percentile, rounded to a tenth of a microsecond.</summary>
    public static (double Mean, double P50, double P99) Stats(IReadOnlyList<double> micros)
    {
        ArgumentNullException.ThrowIfNull(micros);
        if (micros.Count == 0)
        {
            return (0, 0, 0);
        }

        var sorted = micros.Order().ToList();
        return (Math.Round(micros.Average(), 1), Math.Round(Measurement.Percentile(sorted, 50), 1), Math.Round(Measurement.Percentile(sorted, 99), 1));
    }

    /// <summary>The array behind a payload, for joining a take to its read.</summary>
    internal static byte[]? ArrayOf(ReadOnlyMemory<byte> payload) => MemoryMarshal.TryGetArray(payload, out var segment) ? segment.Array : null;
}
