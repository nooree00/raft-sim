using System;
using System.Linq;
using Raft.Checker;
using Xunit;

namespace Raft.Checker.Tests;

/// <summary>
/// The brute-force oracle against histories whose verdicts are known in advance.
/// Vacuity risks: an oracle that accepts everything passes every accepting case, so rejecting
/// cases come first and outnumber them; hand-written histories share my misreading of real-time
/// order, which S-hist-2 targets specifically; an oracle that treats timeouts as never having
/// happened is targeted by S-hist-3. A history that is itself malformed would make its verdict
/// meaningless, so well-formedness is asserted first.
/// </summary>
public sealed class KnownHistoryTests
{
    public static TheoryData<string> Names() => new(KnownHistories.All.Select(h => h.Name));

    private static KnownHistory Named(string name) => KnownHistories.All.Single(h => h.Name == name);

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryKnownHistoryIsWellFormed(string name) =>
        Assert.Empty(History.Problems(Named(name).Ops));

    [Theory]
    [MemberData(nameof(Names))]
    public void OracleClassifiesEveryKnownHistory(string name)
    {
        var h = Named(name);

        Assert.True(BruteForceOracle.IsLinearizable(h.Ops) == h.Linearizable,
            $"{h.Name} ({h.Source}) should be {(h.Linearizable ? "accepted" : "rejected")}:\n  " + string.Join("\n  ", h.Ops));
    }

    [Fact]
    public void RejectingCasesOutnumberAcceptingOnes()
    {
        var rejecting = KnownHistories.All.Count(h => !h.Linearizable);

        Assert.True(rejecting >= KnownHistories.All.Count - rejecting, $"{rejecting} rejecting of {KnownHistories.All.Count}");
    }

    /// <summary>
    /// Sensitivity: in every accepted history, making one completed read return a value nobody
    /// wrote must turn the verdict to rejected. An oracle that ignores outputs fails here.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void CorruptingAReadInAnAcceptedHistoryMakesItRejected(string name)
    {
        var h = Named(name);
        var readIndex = h.Ops.ToList().FindIndex(o => o.Kind == OpKind.Get && !o.IsIndeterminate);
        if (!h.Linearizable || readIndex < 0)
        {
            return;
        }

        var corrupted = h.Ops.Select((o, i) => i == readIndex ? o with { Output = "never-written" } : o).ToList();

        Assert.False(BruteForceOracle.IsLinearizable(corrupted), $"{h.Name} with read #{readIndex} corrupted was still accepted");
    }

    [Fact]
    public void TheOracleRefusesHistoriesTooLongForBruteForce()
    {
        var ops = Enumerable.Range(0, BruteForceOracle.MaxOperations + 1)
            .Select(i => new Operation(i, OpKind.Put, "x", i * 10, i * 10 + 5, Value: "v")).ToList();

        Assert.Throws<ArgumentException>(() => BruteForceOracle.IsLinearizable(ops));
    }
}
