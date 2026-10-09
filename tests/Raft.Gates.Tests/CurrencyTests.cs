using System;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P13-01: the one comparison every hand-kept input's observer reports through, and the Budget
/// seeds' observer, the soak's ranking. Sabotages S-input-2 (the comparison never reports stale)
/// and S-soak-12 (the seeds taken as the soak's ranking).
/// </summary>
public sealed class CurrencyTests
{
    /// <summary>Sabotage S-input-2.</summary>
    [Fact]
    public void AClaimBeyondItsFactorIsStale()
    {
        var f = new Findings();

        var stale = Currency.Factor(Currency.GitHubRun, [("the run's 3 entries", 300, 900)], factor: 2, floor: 30, f, (_, observed) => $"refresh to {observed}");

        Assert.Equal(1, stale);
        var failure = Assert.Single(f.Failures);
        Assert.Equal("ci/sabotage-costs.txt: the run's 3 entries claims 300; GitHub's head shards, their entry-time notices: 900 (3.00 times) (within a factor of 2 (and 30 s)): stale. refresh to 900", failure);
    }

    [Theory]
    [InlineData(300, 590)]
    [InlineData(300, 151)]
    [InlineData(10, 35)]
    public void AClaimWithinItsFactorOrItsFloorIsCurrent(double claimed, double observed)
    {
        var f = new Findings();

        Assert.Equal(0, Currency.Factor(Currency.GitHubRun, [("k", claimed, observed)], factor: 2, floor: 30, f, (_, _) => ""));
        Assert.Empty(f.Failures);
    }

    [Fact]
    public void AClaimTheObservationDoesNotContainIsStale()
    {
        var f = new Findings();

        Currency.Member(Currency.RecordCommit, [("p12-01", "abc1234"), ("p12-02", "def5678")], c => c == "abc1234", "the commits the head descends from", f, k => $"relabel {k}");

        var failure = Assert.Single(f.Failures);
        Assert.StartsWith("measurements: p12-02 claims def5678;", failure, StringComparison.Ordinal);
        Assert.EndsWith("relabel p12-02", failure, StringComparison.Ordinal);
    }

    private static string Report(int executions, int most, params (int States, int Seed)[] ranking) =>
        $"{executions} executions (seeds 0..{executions - 1}), faults generated until 1\n" +
        $"linearizability (WGL, budget 1000000 states per key): ... most states explored {most}\n" +
        $"ten hardest decided histories (states explored, seed): {string.Join(", ", ranking.Select(r => $"{r.States} seed {r.Seed}"))}\n";

    private static readonly (int, int)[] Ten = [(3702, 9532), (2371, 6673), (890, 9824), (700, 1), (600, 2), (500, 3), (400, 4), (300, 5), (200, 6), (100, 7)];

    [Fact]
    public void ABudgetSeedAmongTheSoaksTenHardestIsCurrent()
    {
        var f = new Findings();

        Currency.CheckSeeds([9532, 6673, 9824], Report(10_000, 3702, Ten), f);

        Assert.Empty(f.Failures);
    }

    /// <summary>The case phase 11 met: a change made a named seed easy, and only a surviving sabotage said so. Sabotage S-soak-12.</summary>
    [Fact]
    public void ABudgetSeedOutsideTheSoaksTenHardestIsStale()
    {
        var f = new Findings();

        Currency.CheckSeeds([9532, 5908], Report(10_000, 3702, Ten), f);

        var failure = Assert.Single(f.Failures);
        Assert.Contains("ci/budget-seeds.txt: seed 2 claims 5908", failure, StringComparison.Ordinal);
        Assert.Contains("The file should hold the soak's hardest: 9532 (3702 states), 6673 (2371 states), 9824 (890 states)", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSuitesSampleIsNotTheSoaksRanking()
    {
        var f = new Findings();

        Currency.CheckSeeds([9532], Report(300, 3702, Ten), f);

        Assert.Contains(f.Failures, x => x.Contains("not the full soak's", StringComparison.Ordinal));
    }

    [Fact]
    public void ARankingThatCountsSomethingOtherThanStatesIsRefused()
    {
        var f = new Findings();

        Currency.CheckSeeds([9532], Report(10_000, 900, Ten), f);

        Assert.Contains(f.Failures, x => x.Contains("explored more states than the report's most", StringComparison.Ordinal));
    }
}
