using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P2-01: the harness shard plan and the `build` collect decision. Every rule has a case that must
/// fail it. Sabotages S-shard-1..3 disable one rule each.
/// </summary>
public sealed class ShardPlanTests
{
    private static readonly string[] Ids = Enumerable.Range(1, 83).Select(i => $"S-x-{i:D3}").ToArray();

    [Theory]
    [InlineData(83, 28, 3)]
    [InlineData(84, 28, 3)]
    [InlineData(85, 28, 4)]
    [InlineData(1, 28, 1)]
    [InlineData(0, 28, 1)]
    public void TheShardCountIsDerivedFromTheManifest(int entries, int size, int expected) =>
        Assert.Equal(expected, ShardPlan.Count(entries, size));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void EveryEntryIsDealtToExactlyOneShardAndNoShardIsEmpty(int n)
    {
        var shards = ShardPlan.Deal(Ids, n);

        Assert.Equal(n, shards.Count);
        Assert.Empty(ShardPlan.Problems(Ids, shards));
        Assert.Equal(Ids.Order(StringComparer.Ordinal), shards.SelectMany(s => s).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheDealDoesNotDependOnInputOrder() =>
        Assert.Equal(ShardPlan.Deal(Ids, 3).Select(s => string.Join(",", s)), ShardPlan.Deal(Ids.Reverse(), 3).Select(s => string.Join(",", s)));

    [Fact]
    public void AMissingOrDuplicatedEntryIsAProblem()
    {
        IReadOnlyList<IReadOnlyList<string>> bad = [["a", "b"], ["b"], []];

        var problems = ShardPlan.Problems(["a", "b", "c"], bad);

        Assert.Contains(problems, p => p.Contains("c is in no shard", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("b is in 2 shards", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("shard 3/3 is empty", StringComparison.Ordinal));
    }

    [Fact]
    public void AShardCountThatDisagreesWithTheManifestIsRefused()
    {
        var f = new Findings();

        var share = ShardPlan.Select(Ids, "2/2", 28, f);

        Assert.Null(share);
        Assert.Contains(f.Failures, m => m.Contains("which is 3 shard(s) of 28, not 2", StringComparison.Ordinal));
    }

    [Fact]
    public void AMatchingShardSelectsItsShare()
    {
        var f = new Findings();

        var share = ShardPlan.Select(Ids, "2/3", 28, f);

        Assert.Empty(f.Failures);
        Assert.Equal(28, share!.Count);
        Assert.Equal("S-x-002", share[0]);
    }

    [Theory]
    [InlineData("0/3")]
    [InlineData("4/3")]
    [InlineData("3")]
    [InlineData("a/b")]
    public void AMalformedShardIsRejected(string text) =>
        Assert.Throws<ArgumentException>(() => ShardPlan.Parse(text));

    private static string Jobs(params (string Name, string Conclusion)[] jobs) =>
        "{\"jobs\":[" + string.Join(",", jobs.Select(j => $"{{\"name\":\"{j.Name}\",\"conclusion\":\"{j.Conclusion}\"}}")) + "]}";

    private static readonly string[] ThreeShards = ["1/3", "2/3", "3/3"];

    private static Findings Collect(string core, string jobs)
    {
        var f = new Findings();
        BuildCollect.Evaluate(core, ThreeShards, jobs, f);
        return f;
    }

    [Fact]
    public void BuildPassesWithCoreAndEveryShard() =>
        Assert.Empty(Collect("success", Jobs(("sabotage 1/3", "success"), ("sabotage 2/3", "success"), ("sabotage 3/3", "success"), ("soak", "success"), ("soak-membership", "success"), ("compose", "success"))).Failures);

    [Fact]
    public void AShardWithNoJobFailsTheBuild() =>
        Assert.Contains(Collect("success", Jobs(("sabotage 1/3", "success"), ("sabotage 2/3", "success"), ("soak", "success"), ("soak-membership", "success"))).Failures,
            m => m.Contains("sabotage 3/3: 0 jobs", StringComparison.Ordinal));

    [Fact]
    public void ARedShardFailsTheBuild() =>
        Assert.Contains(Collect("success", Jobs(("sabotage 1/3", "success"), ("sabotage 2/3", "failure"), ("sabotage 3/3", "success"), ("soak", "success"), ("soak-membership", "success"))).Failures,
            m => m.Contains("sabotage 2/3: concluded failure", StringComparison.Ordinal));

    [Fact]
    public void AShardOutsideThePlanFailsTheBuild() =>
        Assert.Contains(Collect("success", Jobs(("sabotage 1/3", "success"), ("sabotage 2/3", "success"), ("sabotage 3/3", "success"), ("sabotage 1/2", "success"), ("soak", "success"), ("soak-membership", "success"))).Failures,
            m => m.Contains("does not contain", StringComparison.Ordinal));

    [Fact]
    public void ARedBuildCoreFailsTheBuild() =>
        Assert.Contains(Collect("failure", Jobs(("sabotage 1/3", "success"), ("sabotage 2/3", "success"), ("sabotage 3/3", "success"), ("soak", "success"), ("soak-membership", "success"))).Failures,
            m => m.Contains("build-core concluded 'failure'", StringComparison.Ordinal));

    /// <summary>P3-08: the soak is required; a run without it, or with it red, fails the build.</summary>
    [Fact]
    public void ASoakThatDidNotRunFailsTheBuild() =>
        Assert.Contains(Collect("success", Jobs(("sabotage 1/3", "success"), ("sabotage 2/3", "success"), ("sabotage 3/3", "success"), ("soak-membership", "success"))).Failures,
            m => m.Contains("soak: 0 jobs", StringComparison.Ordinal));

    [Fact]
    public void ARedSoakFailsTheBuild() =>
        Assert.Contains(Collect("success", Jobs(("sabotage 1/3", "success"), ("sabotage 2/3", "success"), ("sabotage 3/3", "success"), ("soak", "failure"), ("soak-membership", "success"))).Failures,
            m => m.Contains("soak: concluded failure", StringComparison.Ordinal));

    /// <summary>P6-12: the membership soak is a soak of its own, required like the baseline one. Sabotage S-soak-8.</summary>
    [Fact]
    public void AMembershipSoakThatDidNotRunOrWentRedFailsTheBuild()
    {
        Assert.Contains(Collect("success", Jobs(("sabotage 1/3", "success"), ("sabotage 2/3", "success"), ("sabotage 3/3", "success"), ("soak", "success"))).Failures,
            m => m.Contains("soak-membership: 0 jobs", StringComparison.Ordinal));
        Assert.Contains(Collect("success", Jobs(("sabotage 1/3", "success"), ("sabotage 2/3", "success"), ("sabotage 3/3", "success"), ("soak", "success"), ("soak-membership", "failure"))).Failures,
            m => m.Contains("soak-membership: concluded failure", StringComparison.Ordinal));
    }

    /// <summary>P9-08: the Compose run is required like the soaks; a run without it, or with it red, fails the build. Sabotage S-compose-2.</summary>
    [Fact]
    public void AComposeRunThatDidNotRunOrWentRedFailsTheBuild()
    {
        Assert.Contains(Collect("success", Jobs(("sabotage 1/3", "success"), ("sabotage 2/3", "success"), ("sabotage 3/3", "success"), ("soak", "success"), ("soak-membership", "success"))).Failures,
            m => m.Contains("compose: 0 jobs", StringComparison.Ordinal));
        Assert.Contains(Collect("success", Jobs(("sabotage 1/3", "success"), ("sabotage 2/3", "success"), ("sabotage 3/3", "success"), ("soak", "success"), ("soak-membership", "success"), ("compose", "failure"))).Failures,
            m => m.Contains("compose: concluded failure", StringComparison.Ordinal));
    }
}
