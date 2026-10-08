using System;
using System.Collections.Generic;
using System.IO;
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

        var share = ShardPlan.Select(Entries(Ids), "2/2", 28, ShardCosts.Uniform(Ids), f);

        Assert.Null(share);
        Assert.Contains(f.Failures, m => m.Contains("which is 3 shard(s) of 28, not 2", StringComparison.Ordinal));
    }

    [Fact]
    public void AMatchingShardSelectsItsShare()
    {
        var f = new Findings();

        var share = ShardPlan.Select(Entries(Ids), "2/3", 28, ShardCosts.Uniform(Ids), f);

        Assert.Empty(f.Failures);
        Assert.Equal(ShardPlan.Balance(Entries(Ids), 3, ShardCosts.Uniform(Ids))[1], share);
    }

    private static List<PlanEntry> Entries(IEnumerable<string> ids, string unit = "test:tests/X") => ids.Select(id => new PlanEntry(id, unit)).ToList();

    private static ShardCosts Costs(params (string Id, double Seconds)[] entries) =>
        new(60, 3, new Dictionary<string, double>(StringComparer.Ordinal), entries.ToDictionary(e => e.Id, e => e.Seconds, StringComparer.Ordinal));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(11)]
    public void TheCostPlanPutsEveryEntryInExactlyOneShardAndLeavesNoneEmpty(int n)
    {
        var costs = new ShardCosts(60, 3, new Dictionary<string, double>(StringComparer.Ordinal), Ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => 5.0 + (x.i * 37 % 101), StringComparer.Ordinal));

        var shards = ShardPlan.Balance(Entries(Ids), n, costs);

        Assert.Equal(n, shards.Count);
        Assert.Empty(ShardPlan.Problems(Ids, shards));
    }

    [Fact]
    public void TheCostPlanDoesNotDependOnInputOrder()
    {
        var costs = Costs([.. Ids.Select((id, i) => (id, 5.0 + (i % 7)))]);
        Assert.Equal(ShardPlan.Balance(Entries(Ids), 3, costs).Select(s => string.Join(",", s)), ShardPlan.Balance(Entries(Ids.Reverse()), 3, costs).Select(s => string.Join(",", s)));
    }

    /// <summary>
    /// A shard's modelled time is its slowest worker's: the baseline build, the worker's units, then its
    /// entries, the longest first to the worker that would finish soonest.
    /// </summary>
    [Fact]
    public void AShardsModelledTimeIsItsSlowestWorkers()
    {
        var costs = new ShardCosts(60, 3, new Dictionary<string, double>(StringComparer.Ordinal) { ["test:tests/A"] = 40, ["test:tests/B"] = 5 }, new Dictionary<string, double>(StringComparer.Ordinal) { ["S-a-1"] = 100, ["S-b-1"] = 30, ["S-b-2"] = 20 });
        List<PlanEntry> entries = [new("S-a-1", "test:tests/A"), new("S-b-1", "test:tests/B"), new("S-b-2", "test:tests/B")];

        // Workers 0..2 hold units A, B and none (ready at 100, 65, 60); S-a-1 goes to worker 2 (160),
        // S-b-1 to worker 1 (95), S-b-2 to worker 1 (115): the slowest is worker 2, at 160.
        Assert.Equal(160, ShardPlan.ModelledTime(entries, costs));
    }

    /// <summary>
    /// The plan reads the costs: dealt by id over 2 shards, the five heavy entries (the odd ids) all
    /// land in shard 1, where four workers leave one worker two of them; by cost no shard holds more
    /// than three. Sabotage S-shard-4 (the costs ignored).
    /// </summary>
    [Fact]
    public void FiveHeavyEntriesAreNotPutInOneShard()
    {
        var ids = Enumerable.Range(1, 10).Select(i => $"S-a-{i:D2}").ToArray();
        var costs = Costs([.. ids.Select((id, i) => (id, i % 2 == 0 ? 300.0 : 10.0))]);

        var shards = ShardPlan.Balance(Entries(ids), 2, costs);

        Assert.All(shards, s => Assert.True(s.Count(id => costs.Entry(id) == 300) <= 3, $"a shard holds {s.Count(id => costs.Entry(id) == 300)} of the five heavy entries"));
    }

    /// <summary>
    /// Inside a shard the workers are balanced too: the four heaviest entries go to four workers.
    /// Dealt by id over four workers, three of them (the 1st, 5th and 9th) would share worker 1.
    /// Sabotage S-shard-8 (the workers dealt by id).
    /// </summary>
    [Fact]
    public void TheFourHeaviestEntriesOfAShardGoToFourWorkers()
    {
        var ids = Enumerable.Range(1, 12).Select(i => $"S-w-{i:D2}").ToArray();
        var costs = Costs([.. ids.Select((id, i) => (id, i is 0 or 1 or 4 or 8 ? 200.0 : 5.0))]);

        var shares = ShardPlan.WorkerShares(Entries(ids), 4, costs);

        Assert.All(shares, share => Assert.Single(share, e => costs.Entry(e.Id) == 200));
    }

    /// <summary>A manifest entry with no recorded cost, and a cost for no entry, are refused. Sabotage S-shard-5.</summary>
    [Fact]
    public void AnEntryWithNoCostAndACostWithNoEntryAreRefused()
    {
        var f = new Findings();

        var share = ShardPlan.Select(Entries(["S-a-1", "S-a-2"]), "1/1", 28, Costs(("S-a-1", 10), ("S-gone-1", 10)), f);

        Assert.Null(share);
        Assert.Contains(f.Failures, m => m.Contains("S-a-2 has no cost", StringComparison.Ordinal));
        Assert.Contains(f.Failures, m => m.Contains("a cost for S-gone-1", StringComparison.Ordinal));
    }

    /// <summary>
    /// The reviewer's condition: a recorded cost is a claim, checked against every run's time; off by
    /// more than the stale factor either way, it fails. Sabotage S-shard-6 (the comparison never fails).
    /// </summary>
    [Theory]
    [InlineData(31, 10, true)]
    [InlineData(29, 10, false)]
    [InlineData(13, 40, true)]
    [InlineData(14, 40, false)]
    [InlineData(6, 1.8, false)]
    [InlineData(12, 1.8, true)]
    public void AnEntryFarFromItsRecordedCostIsStale(double actual, double recorded, bool stale) =>
        Assert.Equal(stale, ShardPlan.Stale(actual, recorded, 3, 10));

    [Fact]
    public void AStaleEntryFailsTheRunAndNamesBothNumbers()
    {
        var f = new Findings();

        Sabotage.CheckCosts(new Dictionary<string, double> { ["S-a-1"] = 12, ["S-a-2"] = 95, ["S-a-3"] = 60 }, Costs(("S-a-1", 10), ("S-a-2", 20), ("S-a-3", 100)), f);

        var failure = Assert.Single(f.Failures);
        Assert.Contains("S-a-2: took 95.0s against 20.0s recorded", failure, StringComparison.Ordinal);
        Assert.Contains(f.Notes, n => n.Contains("the furthest from its line is S-a-2 at 4.75", StringComparison.Ordinal));
    }

    /// <summary>
    /// The file-level condition: no entry beyond its own factor, but the shard's total beyond the
    /// shard factor of its recorded sum, fails the run. Sabotage S-shard-9 (the total never compared).
    /// </summary>
    [Fact]
    public void AShardWhoseTotalIsFarFromItsRecordedSumFailsTheRun()
    {
        var f = new Findings();

        // Five times its recorded sum against the file's shard factor of 3 (P12-08: a factor of 1.5 beyond it),
        // each entry within its own factor of 5.
        Sabotage.CheckCosts(new Dictionary<string, double> { ["S-a-1"] = 50, ["S-a-2"] = 50, ["S-a-3"] = 50 }, Costs(("S-a-1", 10), ("S-a-2", 10), ("S-a-3", 10)) with { StaleFactor = 5, StaleFloor = 30, ShardStaleFactor = 3 }, f);

        var failure = Assert.Single(f.Failures);
        Assert.Contains("this shard's entries took 150s against 30s recorded", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCostFileIsParsed()
    {
        var costs = ShardCosts.Parse("# c\nstale-factor 3\nstale-floor 10\nshard-stale-factor 2\nbuild 63.5\nunit command:dotnet \"$GATES\" preflight --allow-dirty 17\nS-a-1 12.5\n");

        Assert.Equal(3, costs.StaleFactor);
        Assert.Equal(10, costs.StaleFloor);
        Assert.Equal(2, costs.ShardStaleFactor);
        Assert.Equal(63.5, costs.Build);
        Assert.Equal(17, costs.Unit("command:dotnet \"$GATES\" preflight --allow-dirty"));
        Assert.Equal(12.5, costs.Entry("S-a-1"));
    }

    /// <summary>
    /// Spec §10's rule on limits, the reviewer's condition on the tuned cost file: at the largest
    /// manifest 11 shards hold (308 entries at 28 a shard; today's entries with their recorded costs
    /// and units, and 12 more at the costliest recorded cost), the modelled slowest shard stays under
    /// 600 local seconds, the 900-s ceiling at GitHub's worst ratio to this machine (1.5). A capacity
    /// check, not a proof that balancing matters: since P11-09 (each entry runs its target's class) the
    /// manifest fits even dealt by id, so S-shard-7 targets a balancing test instead. Its non-vacuity
    /// was shown by its own failures before P11-09 (603 to 607 s, the placeholders priced at 335 s).
    /// </summary>
    [Fact]
    public void AtTheLargestManifestElevenShardsHoldTheSlowestShardFitsTheCeiling()
    {
        var root = RepoRoot();
        var repo = Repo.Locate(root);
        var costs = ShardCosts.Parse(File.ReadAllText(Path.Combine(root, ShardPlan.CostFile)));
        var entries = SabotageSpec.LoadAll(repo, new Findings()).Where(s => !s.RunsOnHost).Select(s => new PlanEntry(s.Id, Sabotage.UnitKey(s))).ToList();
        var size = ShardPlan.ParseSize(File.ReadAllText(Path.Combine(root, ShardPlan.SizeFile)));
        var largest = (ShardPlan.Count(entries.Count, size) * size) - entries.Count;
        var heaviest = costs.Entries.Values.Max();
        var extra = Enumerable.Range(1, largest).Select(i => new PlanEntry($"S-zz-{i}", "test:tests/Raft.Core.Tests")).ToList();
        var all = entries.Concat(extra).ToList();
        var withExtra = costs with { Entries = costs.Entries.Concat(extra.Select(e => KeyValuePair.Create(e.Id, heaviest))).ToDictionary(StringComparer.Ordinal) };
        var n = ShardPlan.Count(all.Count, size);

        var byId = all.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var slowest = ShardPlan.Balance(all, n, withExtra).Max(s => ShardPlan.ModelledTime([.. s.Select(id => byId[id])], withExtra));

        Assert.Equal(ShardPlan.Count(entries.Count, size), n);
        Assert.True(slowest < 600, $"at {all.Count} entries in {n} shards the slowest shard models at {slowest:F0} s, over 600 (the 900-s ceiling at GitHub's 1.5 ratio)");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Raft.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Raft.slnx not found above " + AppContext.BaseDirectory);
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
