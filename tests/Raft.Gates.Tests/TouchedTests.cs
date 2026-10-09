using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P9-00: the touched-file selection (phase 9 decision 1). An entry is selected when its patch or its
/// control names a file the push changed; the plan (count, fraction of the manifest, estimated time)
/// is decided before anything runs, and a selection estimated past the threshold runs only when
/// accepted. Vacuity risk: a selection that always comes back empty passes every push; guarded by
/// the cases that must select. Sabotage S-gate-1 (the control not read). P12-02: a change to the node
/// also selects the soak-derived entries; sabotages S-touch-1 (the rule removed) and S-touch-2 (the
/// rule firing on any change). P13-01: the estimate is the shard plan's model, and the run is
/// compared with it afterwards (the observer touched-run); sabotage S-touch-3 (the run compared with
/// the estimate itself).
/// </summary>
public sealed class TouchedTests
{
    private static string Diff(string path) => $"--- a/{path}\n+++ b/{path}\n@@ -1,1 +1,1 @@\n-x\n+y\n";

    private static readonly HashSet<string> Changed = new(StringComparer.Ordinal) { "src/Raft.Core/RaftNode.cs" };

    [Fact]
    public void ThePathsOfADiffAreItsOldAndNewFiles()
    {
        var paths = Touched.PathsIn(Diff("src/A.cs") + "diff --git a/src/B.cs b/src/B.cs\n--- /dev/null\n+++ b/src/B.cs\n@@ -0,0 +1 @@\n+z\n");
        Assert.Equal(["src/A.cs", "src/B.cs"], paths.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnEntryWhosePatchTouchesAChangedFileIsSelected() =>
        Assert.Equal(["S-a-1"], Touched.Select([new("S-a-1", Diff("src/Raft.Core/RaftNode.cs"), null)], Changed));

    [Fact]
    public void AnEntryTouchingNoChangedFileIsNotSelected() =>
        Assert.Empty(Touched.Select([new("S-a-1", Diff("src/Raft.Kv/KvStateMachine.cs"), null)], Changed));

    /// <summary>The prediction's case: the control is part of the entry's footprint. Sabotage S-gate-1.</summary>
    [Fact]
    public void AnEntryWhoseControlAloneTouchesAChangedFileIsSelected() =>
        Assert.Equal(["S-a-1"], Touched.Select([new("S-a-1", Diff("src/Raft.Kv/KvStateMachine.cs"), Diff("src/Raft.Core/RaftNode.cs"))], Changed));

    private static readonly Touched.Config Config = new(EntryScale: 1.25, StaleFactor: 3.25, StaleFloor: 30, ConfirmAboveMinutes: 11);

    [Fact]
    public void ThePlanStatesTheCountTheFractionAndTheEstimateBeforeAnythingRuns()
    {
        var plan = Touched.PlanFor(selected: 94, manifest: 292, estimateSeconds: 1290, Config, accepted: false);

        Assert.Contains("94 of 292 entries (32.2% of the manifest)", plan.Line, StringComparison.Ordinal);
        Assert.Contains("estimated 21.5 min", plan.Line, StringComparison.Ordinal);
        Assert.False(plan.Run);
    }

    [Fact]
    public void ASelectionPastTheThresholdRunsOnlyWhenAccepted()
    {
        Assert.True(Touched.PlanFor(94, 292, 1290, Config, accepted: true).Run);
        Assert.True(Touched.PlanFor(10, 292, 137, Config, accepted: false).Run);
    }

    /// <summary>An accepted selection runs under its own ceiling, not the shards' 15 minutes, which it would exceed by construction.</summary>
    [Fact]
    public void AnAcceptedSelectionRunsUnderItsOwnCeiling()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), Touched.PlanFor(10, 292, 137, Config, accepted: false).Ceiling);
        Assert.True(Touched.PlanFor(80, 296, 1110, Config, accepted: true).Ceiling > TimeSpan.FromMinutes(18.5));
    }

    [Fact]
    public void AnEmptySelectionRunsNothingAndSaysSo()
    {
        var plan = Touched.PlanFor(0, 292, 0, Config, accepted: false);
        Assert.False(plan.Run);
        Assert.Contains("0 of 292 entries", plan.Line, StringComparison.Ordinal);
    }

    /// <summary>P12-02: a soak-derived entry whose patch names no changed file is selected because the node changed. Sabotage S-touch-1.</summary>
    [Fact]
    public void AChangeToTheNodeSelectsTheSoakDerivedEntries() =>
        Assert.Equal(
            ["S-b-1"],
            Touched.Select([new("S-b-1", Diff("tests/Raft.Scale.Tests/SoakConfig.cs"), null, "Raft.Budget.Tests.BudgetTests.TheSoaksHardestDecidedHistoriesAreDecidedWithinTheBudget(seed: 9532)"), new("S-c-1", Diff("tests/Raft.Scale.Tests/SoakConfig.cs"), null, "Raft.Scale.Tests.ReplicationCostTests.TheLeaderSendsEachFollowerABoundedNumberOfEntriesPerCommittedEntry")], Changed));

    /// <summary>P12-02: a push that leaves the node alone adds nothing. Sabotage S-touch-2.</summary>
    [Fact]
    public void AChangeElsewhereDoesNotSelectTheSoakDerivedEntries() =>
        Assert.Empty(Touched.Select([new("S-b-1", Diff("tests/Raft.Scale.Tests/SoakConfig.cs"), null, "Raft.Scale.Tests.SoakTests.TheGeneratedSampleHoldsEveryInvariant")], new HashSet<string>(StringComparer.Ordinal) { "docs/findings.md", "src/Raft.Host/NodeHost.cs" }));

    /// <summary>The rule on this repository's manifest: a change to RaftNode.cs selects S-soak-6, which phase 11's main push could not select and which survived on GitHub.</summary>
    [Fact]
    public void OnThisManifestAChangeToTheNodeSelectsSoak6()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "RAFT_PROJECT_SPEC.md")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        var specs = SabotageSpec.LoadAll(Repo.Locate(root), new Findings());
        var selected = Touched.Select(specs.Select(s => new TouchedEntry(s.Id, File.ReadAllText(s.PatchPath), s.ControlPath is null ? null : File.ReadAllText(s.ControlPath), s.Fields.GetValueOrDefault("target"))), Changed);

        Assert.Contains("S-soak-6", selected);
    }

    [Fact]
    public void TheScaleTheFactorTheFloorAndTheThresholdAreReadFromTheirFile() =>
        Assert.Equal(Config, Touched.ParseConfig("# comment\nentry-scale 1.25\nstale-factor 3.25\nstale-floor 30\nconfirm-above-minutes 11\n"));

    /// <summary>P13-01: the estimate is the shard plan's model of the selection, each line scaled to this machine: a single long entry is its build, its unit and its line, not a flat rate.</summary>
    [Fact]
    public void TheEstimateIsTheShardPlansModelWithEachLineScaled()
    {
        var costs = new ShardCosts(Build: 60, StaleFactor: 5, Units: new Dictionary<string, double>(StringComparer.Ordinal) { ["test:tests/A"] = 10 }, Entries: new Dictionary<string, double>(StringComparer.Ordinal) { ["S-kill-1"] = 80, ["S-a-1"] = 8 });

        Assert.Equal(60 + 10 + (1.25 * 80), Touched.Estimate([new("S-kill-1", "test:tests/A")], costs, workers: 4, entryScale: 1.25), 6);
        // Two entries on two workers: the unit's baseline goes to the first, so the long entry to the second.
        Assert.Equal(60 + (1.25 * 80), Touched.Estimate([new("S-kill-1", "test:tests/A"), new("S-a-1", "test:tests/A")], costs, workers: 4, entryScale: 1.25), 6);
    }

    /// <summary>P13-01: a run beyond the factor of its estimate makes the file stale, and the failure names the scale that would have fitted it. Sabotage S-touch-3.</summary>
    [Fact]
    public void ARunFarFromItsEstimateIsStale()
    {
        var f = new Findings();

        Touched.CheckRun(Config, estimateSeconds: 100, actualSeconds: 410, scale => 60 + (32 * scale), f);

        var failure = Assert.Single(f.Failures);
        Assert.Contains("ci/sabotage-touched.txt: this run's time claims 100", failure, StringComparison.Ordinal);
        Assert.Contains("410 (4.10 times)", failure, StringComparison.Ordinal);
        Assert.Contains("At entry-scale 10.94 this run's estimate would equal its time", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(100, 300)]
    [InlineData(100, 31)]
    [InlineData(20, 45)]
    public void ARunWithinTheFactorOrTheFloorOfItsEstimateIsCurrent(double estimate, double actual)
    {
        var f = new Findings();

        Touched.CheckRun(Config, estimate, actual, _ => estimate, f);

        Assert.Empty(f.Failures);
    }
}
