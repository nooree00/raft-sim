using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P13-01: build-collect reads every head shard's entry-time notice and compares the run's total with
/// the cost file's sum over the same entries (the observer github-run). Sabotage S-shard-12 (the
/// file's sum compared with itself).
/// </summary>
public sealed class BuildCollectCostTests
{
    private static readonly string[] TwoShards = ["1/2", "2/2"];

    private const string Jobs = """{"jobs":[{"id":11,"name":"sabotage 1/2"},{"id":22,"name":"sabotage 2/2"},{"id":33,"name":"soak"}]}""";

    private static readonly ShardCosts Costs = new(Build: 60, StaleFactor: 5, Units: new Dictionary<string, double>(StringComparer.Ordinal), Entries: new Dictionary<string, double>(StringComparer.Ordinal) { ["S-a-1"] = 100, ["S-b-1"] = 100, ["S-c-1"] = 100 }, StaleFloor: 30);

    /// <summary>The annotations response for a job, as GitHub returns the harness's notice (its %0A decoded to newlines).</summary>
    private static string Annotations(string times) =>
        $$"""[{"annotation_level":"notice","title":"","message":"sabotage entry times\n{{times.Replace("\n", "\\n", StringComparison.Ordinal)}}"}]""";

    private static Findings Check(Func<long, string> annotations)
    {
        var f = new Findings();
        BuildCollect.CheckCosts(TwoShards, Jobs, annotations, Costs, f);
        return f;
    }

    [Fact]
    public void ARunWithinTheFactorOfTheFilesSumIsCurrent() =>
        Assert.Empty(Check(id => id == 11 ? Annotations("S-a-1 120.0\nS-b-1 90.5") : Annotations("S-c-1 140.0")).Failures);

    /// <summary>Sabotage S-shard-12.</summary>
    [Fact]
    public void ARunWhoseTotalIsBeyondTheFactorOfTheFilesSumIsStale()
    {
        var f = Check(id => id == 11 ? Annotations("S-a-1 300.0\nS-b-1 300.0") : Annotations("S-c-1 300.0"));

        var failure = Assert.Single(f.Failures);
        Assert.StartsWith("ci/sabotage-costs.txt: the run's 3 entries claims 300; GitHub's head shards, their entry-time notices: 900 (3.00 times)", failure, StringComparison.Ordinal);
    }

    /// <summary>The vacuity guard: a total over the shards that reported compares less work than the run did.</summary>
    [Fact]
    public void AShardWithNoNoticeFails() =>
        Assert.Contains(Check(id => id == 11 ? Annotations("S-a-1 100.0") : "[]").Failures, x => x.Contains("sabotage 2/2: no 'sabotage entry times' notice", StringComparison.Ordinal));

    [Fact]
    public void TheNoticeIsReadAsTheHarnessWritesIt() =>
        Assert.Equal([("S-a-1", 12.5), ("S-b-10", 300.0)], BuildCollect.Notice(Annotations("S-a-1 12.5\nS-b-10 300.0"))!.ToArray());
}
