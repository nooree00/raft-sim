using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Checker;
using Xunit;

namespace Raft.Checker.Tests;

/// <summary>
/// Verdicts that do not come from me: the FIFO queue histories of Herlihy &amp; Wing, "Linearizability:
/// A Correctness Condition for Concurrent Objects", TOPLAS 12:3 (1990), Fig. 1, as event sequences
/// on pp. 468 and 470 (docs/references/herlihy-wing-1990.pdf). Each is transcribed as the paper's
/// literal event list, so review is a line-by-line comparison; the event index is the time.
/// Vacuity risk: a transcription error that happens to agree with a wrong oracle. The events are
/// verbatim, and the two rejecting histories fail for different reasons in the paper (order, and
/// a duplicate dequeue), so one error cannot flip both.
/// </summary>
public sealed class HerlihyWingTests
{
    /// <summary>Paper's verdicts: H1 and H3 linearizable; H2 and H4 not (pp. 469–470).</summary>
    public static readonly IReadOnlyDictionary<string, (bool Linearizable, string[] Events)> Figure1 =
        new Dictionary<string, (bool, string[])>(StringComparer.Ordinal)
        {
            // p. 468; linearizable by the extension H1·(q Ok() A), p. 469.
            ["H1"] = (true, ["q Enq(x) A", "q Enq(y) B", "q Ok() B", "q Ok() A", "q Deq() B", "q Ok(x) B", "q Deq() A", "q Ok(y) A", "q Enq(z) A"]),
            // p. 470: "the complete Enq operation of x precedes the Enq of y, but y is dequeued before x."
            ["H2"] = (false, ["q Enq(x) A", "q Ok() A", "q Enq(y) B", "q Deq() A", "q Ok() B", "q Ok(y) A"]),
            // p. 470: the enqueue takes effect before its return event.
            ["H3"] = (true, ["q Enq(x) A", "q Deq() B", "q Ok(x) B"]),
            // p. 470: "y is enqueued once but dequeued twice."
            ["H4"] = (false, ["q Enq(x) A", "q Enq(y) B", "q Ok() A", "q Ok() B", "q Deq() A", "q Deq() C", "q Ok(y) A", "q Ok(y) C"]),
        };

    public static TheoryData<string> Names() => new(Figure1.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public void OracleAgreesWithThePublishedVerdict(string name)
    {
        var (linearizable, events) = Figure1[name];
        var ops = Parse(events);

        Assert.Empty(History.Problems(ops));
        Assert.True(BruteForceOracle.IsLinearizable(ops, QueueModel.Instance) == linearizable,
            $"{name}: Herlihy & Wing say {(linearizable ? "linearizable" : "not linearizable")}\n  " + string.Join("\n  ", ops));
    }

    [Fact]
    public void TheParserReadsH1AsThePaperDescribesIt()
    {
        // p. 468: "The [q Enq(y)/Ok() B] operation lies within the [q Enq(x)/Ok() A] operation",
        // and the last invocation of Enq is pending.
        var ops = Parse(Figure1["H1"].Events);
        var enqX = ops.Single(o => o.Kind == OpKind.Enqueue && o.Value == "x");
        var enqY = ops.Single(o => o.Kind == OpKind.Enqueue && o.Value == "y");
        var enqZ = ops.Single(o => o.Kind == OpKind.Enqueue && o.Value == "z");

        Assert.True(enqX.Invoke < enqY.Invoke && enqY.Response < enqX.Response);
        Assert.True(enqZ.IsIndeterminate);
        Assert.Equal(5, ops.Count);
    }

    /// <summary>
    /// "q Op(arg) P" events; an invocation (Enq/Deq) is matched with the process's next response
    /// (Ok). Event index is time; an invocation with no response is pending (indeterminate).
    /// </summary>
    internal static List<Operation> Parse(IReadOnlyList<string> events)
    {
        var open = new Dictionary<string, (OpKind Kind, string Key, string? Value, long Invoke)>(StringComparer.Ordinal);
        var done = new List<Operation>();
        var client = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var t = 0; t < events.Count; t++)
        {
            var parts = events[t].Split(' ');
            var (obj, call, process) = (parts[0], parts[1], parts[2]);
            var name = call[..call.IndexOf('(', StringComparison.Ordinal)];
            var arg = call[(call.IndexOf('(', StringComparison.Ordinal) + 1)..^1];
            client.TryAdd(process, client.Count);
            if (name == "Ok")
            {
                var (kind, key, value, invoke) = open[process];
                open.Remove(process);
                done.Add(new Operation(client[process], kind, key, invoke, t, Value: value, Output: arg.Length == 0 ? null : arg));
            }
            else
            {
                var kind = name switch { "Enq" => OpKind.Enqueue, "Deq" => OpKind.Dequeue, _ => throw new FormatException(events[t]) };
                open.Add(process, (kind, obj, arg.Length == 0 ? null : arg, t));
            }
        }

        done.AddRange(open.Select(kv => new Operation(client[kv.Key], kv.Value.Kind, kv.Value.Key, kv.Value.Invoke, null, Value: kv.Value.Value)));
        return done.OrderBy(o => o.Invoke).ToList();
    }
}
