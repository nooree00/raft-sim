using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raft.Checker;
using Xunit;

namespace Raft.Checker.Tests;

/// <summary>
/// P2-05: the WGL checker against the catalogue (every rejected history rejected, every twin and
/// accepted history accepted), Herlihy &amp; Wing's published verdicts, and locality (decomposed and
/// whole-history search agree). Vacuity risk: a checker that rejects everything passes the
/// rejecting half alone — guarded here by the twins, and by P2-06's oracle agreement. Sabotages
/// S-wgl-1..3.
/// </summary>
public sealed class WglCheckerTests
{
    public static TheoryData<string> Names() => new(KnownHistories.All.Select(h => h.Name));

    [Theory]
    [MemberData(nameof(Names))]
    public void TheCheckerClassifiesEveryKnownHistory(string name)
    {
        var h = KnownHistories.All.Single(x => x.Name == name);

        var r = WglChecker.Check(h.Ops);

        Assert.True(r.Verdict == (h.Linearizable ? Verdict.Linearizable : Verdict.NotLinearizable), $"{name}: {r.Verdict}\n  " + string.Join("\n  ", h.Ops));
    }

    public static TheoryData<string> Figure1() => new(HerlihyWingTests.Figure1.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Figure1))]
    public void TheCheckerAgreesWithHerlihyAndWing(string name)
    {
        var (linearizable, events) = HerlihyWingTests.Figure1[name];

        var r = WglChecker.Check(HerlihyWingTests.Parse(events), QueueModel.Instance);

        Assert.Equal(linearizable ? Verdict.Linearizable : Verdict.NotLinearizable, r.Verdict);
    }

    [Fact]
    public void ARejectionNamesItsKeyAndHowFarTheSearchGot()
    {
        var stale = KnownHistories.All.Single(h => h.Name == "stale-read").Ops;
        var noise = new Operation(9, OpKind.Put, "y", 0, 5, Value: "7");

        var r = WglChecker.Check([.. stale, noise]);

        Assert.Equal(Verdict.NotLinearizable, r.Verdict);
        Assert.Equal("x", r.Key);
        Assert.Equal(stale, r.SubHistory);
        Assert.Equal(2, r.LongestPrefix.Count); // both puts fit; no order then admits the read of "1"
        Assert.True(r.StatesExplored > 0);
    }

    [Fact]
    public void DecomposedAndWholeHistorySearchAgree()
    {
        var rng = new Random(20260928);
        int agreed = 0, linearizable = 0;
        for (var i = 0; i < 400; i++)
        {
            var h = LocalityTests.RandomHistory(rng);
            var split = WglChecker.Check(h).Verdict;
            var whole = WglChecker.Check(h, decompose: false).Verdict;
            Assert.True(split == whole, $"decomposed {split}, whole {whole}\n  " + string.Join("\n  ", h));
            agreed++;
            linearizable += split == Verdict.Linearizable ? 1 : 0;
        }

        Assert.InRange(linearizable, 40, 360); // both verdicts represented, or agreement means little
        Assert.Equal(400, agreed);
    }

    /// <summary>n concurrent appends to one key, then a read that sees them in index order: linearizable, at a cost that grows with n.</summary>
    private static List<Operation> Appends(string key, int n) =>
        Enumerable.Range(0, n).Select(i => new Operation(i, OpKind.Append, key, 0, 100, Value: key + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Append(new Operation(99, OpKind.Get, key, 200, 210, Output: string.Concat(Enumerable.Range(0, n).Select(i => key + i.ToString(System.Globalization.CultureInfo.InvariantCulture))))).ToList();

    [Fact]
    public void EachKeyHasTheWholeBudgetSoAKeyIsNotUndecidedForWhatAnotherSpent()
    {
        var a = Appends("a", 7);
        var b = Appends("b", 5);
        var sa = WglChecker.Check(a).StatesExplored;
        var sb = WglChecker.Check(b).StatesExplored;
        var budget = Math.Max(sa, sb) + 1;
        Assert.True(sa + sb > budget, $"a {sa} and b {sb} must not fit one budget of {budget} together");

        var r = WglChecker.Check([.. a, .. b], budget);

        Assert.True(r.IsLinearizable, $"{r.Verdict} at key {r.Key}: each key fits the budget alone ({sa}, {sb} of {budget})");
        Assert.Equal(sa + sb, r.StatesExplored);
    }

    [Fact]
    public void AKeyThatCannotBeLinearizedIsReportedAlthoughAnEarlierKeyWasUndecided()
    {
        var undecidable = Enumerable.Range(0, 10).Select(i => new Operation(i, OpKind.Append, "a", 0, 100, Value: i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Append(new Operation(99, OpKind.Get, "a", 200, 210, Output: "no order gives this"));
        var stale = new[] { new Operation(20, OpKind.Put, "b", 0, 10, Value: "1"), new Operation(21, OpKind.Get, "b", 20, 30, Output: "never written") };

        var r = WglChecker.Check([.. undecidable, .. stale], budget: 1_000);

        Assert.Equal((Verdict.NotLinearizable, "b"), (r.Verdict, r.Key));
    }

    [Fact]
    public void AnExhaustedBudgetIsUndecidedNeverAVerdict()
    {
        var ops = Enumerable.Range(0, 10).Select(i => new Operation(i, OpKind.Append, "x", 0, 100, Value: i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Append(new Operation(99, OpKind.Get, "x", 200, 210, Output: "no order gives this")).ToList();

        var r = WglChecker.Check(ops, budget: 1_000);

        Assert.Equal(Verdict.Undecided, r.Verdict);
        Assert.False(r.IsLinearizable);
    }

    /// <summary>
    /// P2-05's prediction, measured: k concurrent operations on one key, then a read no order
    /// explains, so the search must exhaust every reachable state. Put's state is the last value;
    /// Append's is the whole string, which differs per order.
    /// </summary>
    [Fact]
    public void StatesExploredGrowFactoriallyForAppendsAndSlowlyForPuts()
    {
        long States(OpKind kind, int k)
        {
            var ops = Enumerable.Range(0, k).Select(i => new Operation(i, kind, "x", 0, 100, Value: ((char)('a' + i)).ToString()))
                .Append(new Operation(99, OpKind.Get, "x", 200, 210, Output: "none")).ToList();
            var r = WglChecker.Check(ops);
            Assert.Equal(Verdict.NotLinearizable, r.Verdict);
            return r.StatesExplored;
        }

        var lines = new List<string>();
        for (var k = 4; k <= 8; k++)
        {
            lines.Add($"k={k} append={States(OpKind.Append, k)} put={States(OpKind.Put, k)}");
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "wgl-growth.txt"), string.Join("\n", lines) + "\n");
        Assert.True(States(OpKind.Append, 8) > States(OpKind.Put, 8));
    }
}
