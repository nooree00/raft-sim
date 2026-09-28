using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Checker;
using Xunit;

namespace Raft.Checker.Tests;

/// <summary>
/// P2-07: the checker's cost, in states explored (deterministic; never wall time — P1 findings).
/// Vacuity risk: a budget that is never reached tests nothing, and one reached on every history
/// hides the checker behind "undecided". Guarded on both sides: a long history that must decide
/// within the budget, and one built to exhaust it, which must report undecided. The table it
/// writes sets phase 3's client count and operation mix. Sabotage S-wgl-4.
/// </summary>
public sealed class CheckerCostTests
{
    /// <summary>
    /// A sequential execution of <paramref name="count"/> operations over <paramref name="keys"/>
    /// keys, each interval spanning its neighbours' linearization points so that about
    /// <paramref name="concurrency"/> operations overlap on a key; optionally ending in a read on
    /// key 0 that no order explains, which forces the search to exhaust that key.
    /// </summary>
    internal static List<Operation> Long(int count, int keys, int concurrency, string mix, bool unexplainable, int seed = 1)
    {
        var rng = new Random(seed);
        var state = KvModel.Instance.NewState();
        var ops = new List<Operation>();
        for (var i = 0; i < count; i++)
        {
            var key = "k" + (i % keys).ToString(CultureInfo.InvariantCulture);
            var point = 10L * (i / keys + 1);
            var half = 5L * concurrency;
            var kind = mix switch
            {
                "put" => OpKind.Put,
                "append" => OpKind.Append,
                _ => rng.Next(3) switch { 0 => OpKind.Put, 1 => OpKind.Append, _ => OpKind.Get },
            };
            var op = new Operation(i, kind, key, point - half, point + half, Value: kind == OpKind.Get ? null : ((char)('a' + (i % 26))).ToString());
            var output = KvModel.Instance.Apply(state, op);
            ops.Add(kind == OpKind.Get ? op with { Output = output } : op);
        }

        if (unexplainable)
        {
            var end = ops.Max(o => o.Response!.Value) + 10;
            ops.Add(new Operation(count, OpKind.Get, "k0", end, end + 1, Output: "no order explains this"));
        }

        return ops;
    }

    [Fact]
    public void ALongHistoryOverManyKeysDecidesWellWithinTheBudget()
    {
        var r = WglChecker.Check(Long(200, 20, 3, "mixed", unexplainable: false));

        Assert.Equal(Verdict.Linearizable, r.Verdict);
        Assert.True(r.StatesExplored < WglChecker.DefaultBudget / 100, $"{r.StatesExplored} states");
    }

    [Fact]
    public void AHistoryBuiltToExhaustTheBudgetIsUndecided()
    {
        var r = WglChecker.Check(Long(200, 1, 8, "append", unexplainable: true), budget: 10_000);

        Assert.Equal(Verdict.Undecided, r.Verdict);
        Assert.InRange(r.StatesExplored, 10_000, 10_001);
        Assert.Equal("k0", r.Key);
    }

    /// <summary>
    /// The cost table (P2-07), written beside the test assembly and copied into the phase report.
    /// At a 10^5 budget, each row stopping at its first undecided, to keep the suite fast (it runs in
    /// every Checker sabotage); the phase report also records one run at 10^6.
    /// </summary>
    [Fact]
    public void TheCostTable()
    {
        const long budget = 100_000;
        var lines = new List<string> { "200 operations, one key, ending in an unexplainable read; states explored (U = undecided at 10^5):" };
        foreach (var mix in new[] { "put", "append", "mixed" })
        {
            var row = new List<string>();
            for (var c = 1; c <= 8; c++)
            {
                var r = WglChecker.Check(Long(200, 1, c, mix, unexplainable: true), budget);
                Assert.NotEqual(Verdict.Linearizable, r.Verdict);
                row.Add($"c={c}:{(r.Verdict == Verdict.Undecided ? "U" : r.StatesExplored.ToString(CultureInfo.InvariantCulture))}");
                if (r.Verdict == Verdict.Undecided)
                {
                    break;
                }
            }

            lines.Add($"  {mix,-7} " + string.Join(" ", row));
        }

        long States(int keys, bool bad) => WglChecker.Check(Long(200, keys, 3, "mixed", bad), budget).StatesExplored;
        lines.Add($"mixed, concurrency 3, linearizable: 20 keys {States(20, false)} states, 1 key {States(1, false)}");
        lines.Add($"mixed, concurrency 3, one unexplainable read: 20 keys {States(20, true)} states, 1 key {States(1, true)}");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "checker-cost.txt"), string.Join("\n", lines) + "\n");
    }
}
