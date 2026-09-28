using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Checker;
using Xunit;

namespace Raft.Checker.Tests;

/// <summary>
/// P2-06: the WGL checker against the brute-force oracle on random small histories. Vacuity risk:
/// agreement on a distribution that is 99% one verdict says almost nothing — a checker with the
/// verdict hard-wired agrees 99% of the time. Guarded: histories are built linearizable by
/// construction and then two in three are mutated, and the test fails unless each verdict is at least 25%.
/// Sabotages S-diff-1, S-diff-2.
/// </summary>
public sealed class DifferentialTests
{
    private const int Histories = 1_500;
    private static readonly string[] Values = ["1", "2", "3"];

    /// <summary>
    /// A random sequential execution (outputs from the model), each operation's interval stretched
    /// around its linearization point without crossing the order, some responses removed
    /// (indeterminate): linearizable by construction. Then, for half, one mutation: usually an
    /// output altered, sometimes an interval moved wholly after a later operation's. A mutation can
    /// leave the history linearizable (the altered value may be explainable); the oracle decides.
    /// </summary>
    internal static List<Operation> Constructed(Random rng, bool mutate)
    {
        var count = rng.Next(1, 9);
        var keys = rng.Next(2) == 0 ? new[] { "x" } : new[] { "x", "y" };
        var state = KvModel.Instance.NewState();
        var ops = new List<Operation>();
        for (var i = 0; i < count; i++)
        {
            var key = keys[rng.Next(keys.Length)];
            var point = 10L * (i + 1);
            var invoke = point - rng.Next(0, 16);
            long? response = rng.Next(7) == 0 ? null : point + rng.Next(0, 16);
            var op = rng.Next(5) switch
            {
                0 => new Operation(i, OpKind.Put, key, invoke, response, Value: Values[rng.Next(3)]),
                1 => new Operation(i, OpKind.Append, key, invoke, response, Value: Values[rng.Next(3)]),
                2 => new Operation(i, OpKind.CompareAndSwap, key, invoke, response, Value: Values[rng.Next(3)], Expected: rng.Next(3) == 0 ? null : Values[rng.Next(3)]),
                3 => new Operation(i, OpKind.Delete, key, invoke, response),
                _ => new Operation(i, OpKind.Get, key, invoke, response),
            };
            var output = KvModel.Instance.Apply(state, op);
            ops.Add(op.IsIndeterminate || op.Kind is OpKind.Put or OpKind.Append or OpKind.Delete ? op : op with { Output = output });
        }

        if (mutate && ops.Count > 0)
        {
            var readers = ops.Select((o, i) => (o, i)).Where(x => !x.o.IsIndeterminate && x.o.Kind is OpKind.Get or OpKind.CompareAndSwap).ToList();
            if (readers.Count > 0 && rng.Next(4) != 0)
            {
                // An output altered: to a value nobody wrote (a third of the time), or to another
                // plausible one; a CompareAndSwap's result flipped.
                var (o, i) = readers[rng.Next(readers.Count)];
                var others = Values.Where(v => v != o.Output).ToArray();
                ops[i] = o.Kind == OpKind.Get
                    ? o with { Output = rng.Next(3) == 0 ? "9" : o.Output is not null && rng.Next(3) == 0 ? null : others[rng.Next(others.Length)] }
                    : o with { Output = o.Output == "true" ? "false" : "true" };
            }
            else if (ops.Count >= 2)
            {
                // Move an earlier completed operation wholly after a later one: a real-time change.
                var i = rng.Next(ops.Count - 1);
                var j = rng.Next(i + 1, ops.Count);
                if (ops[i].Response is { } r && ops[j].Response is { } later)
                {
                    var length = r - ops[i].Invoke;
                    ops[i] = ops[i] with { Invoke = later + 1, Response = later + 1 + length };
                }
            }
        }

        return ops.OrderBy(o => o.Invoke).ToList();
    }

    /// <summary>Random operations, random outputs, random intervals: kept for the record (P2-06's prediction).</summary>
    internal static List<Operation> Naive(Random rng)
    {
        var count = rng.Next(1, 9);
        return Enumerable.Range(0, count).Select(i =>
        {
            var key = rng.Next(2) == 0 ? "x" : "y";
            long invoke = rng.Next(0, 80);
            long? response = rng.Next(7) == 0 ? null : invoke + rng.Next(1, 20);
            return rng.Next(3) switch
            {
                0 => new Operation(i, OpKind.Put, key, invoke, response, Value: Values[rng.Next(3)]),
                1 => new Operation(i, OpKind.Get, key, invoke, response, Output: response is null ? null : rng.Next(4) == 0 ? null : Values[rng.Next(3)]),
                _ => new Operation(i, OpKind.CompareAndSwap, key, invoke, response, Value: Values[rng.Next(3)], Expected: Values[rng.Next(3)],
                    Output: response is null ? null : rng.Next(2) == 0 ? "true" : "false"),
            };
        }).OrderBy(o => o.Invoke).ToList();
    }

    /// <summary>A history in the hand-written test syntax, to commit a disagreement as a regression case.</summary>
    private static string AsCode(IReadOnlyList<Operation> ops) => string.Join(",\n", ops.Select(o =>
        $"new Operation({o.Client}, OpKind.{o.Kind}, \"{o.Key}\", {o.Invoke}, {(o.Response is { } r ? r.ToString(CultureInfo.InvariantCulture) : "null")}"
        + (o.Value is null ? "" : $", Value: \"{o.Value}\"") + (o.Expected is null ? "" : $", Expected: \"{o.Expected}\"")
        + (o.Output is null ? "" : $", Output: \"{o.Output}\"") + ")"));

    [Fact]
    public void TheCheckerAgreesWithTheOracleOnRandomSmallHistories()
    {
        var rng = new Random(20260928);
        var linearizable = 0;
        for (var n = 0; n < Histories; n++)
        {
            var h = Constructed(rng, mutate: n % 3 != 0); // two in three mutated: about half of those stay linearizable
            Assert.Empty(History.Problems(h));
            var oracle = BruteForceOracle.IsLinearizable(h);
            var wgl = WglChecker.Check(h);

            Assert.True(wgl.Verdict != Verdict.Undecided, "undecided on a history of " + h.Count.ToString(CultureInfo.InvariantCulture) + " operations");
            Assert.True(wgl.IsLinearizable == oracle, $"history {n}: oracle says {oracle}, WGL says {wgl.Verdict}. As a test case:\n" + AsCode(h));
            linearizable += oracle ? 1 : 0;
        }

        var share = (double)linearizable / Histories;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "differential-split.txt"),
            $"constructed: {linearizable} of {Histories} linearizable ({share:P0})\nnaive: {NaiveShare()}\n");
        Assert.InRange(share, 0.25, 0.75);
    }

    private static string NaiveShare()
    {
        var rng = new Random(7);
        var yes = Enumerable.Range(0, 1_000).Count(_ => BruteForceOracle.IsLinearizable(Naive(rng)));
        return yes.ToString(CultureInfo.InvariantCulture) + " of 1000 linearizable";
    }
}
