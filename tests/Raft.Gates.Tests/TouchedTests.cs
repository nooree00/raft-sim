using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P9-00: the touched-file selection (phase 9 decision 1). An entry is selected when its patch or its
/// control names a file the push changed; the plan (count, fraction of the manifest, estimated time)
/// is decided before anything runs, and a selection estimated past the threshold runs only when
/// accepted. Vacuity risk: a selection that always comes back empty passes every push; guarded by
/// the cases that must select. Sabotage S-gate-1 (the control not read).
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

    [Fact]
    public void ThePlanStatesTheCountTheFractionAndTheEstimateBeforeAnythingRuns()
    {
        var plan = Touched.PlanFor(selected: 94, manifest: 292, secondsPerEntry: 13.7, confirmAboveMinutes: 11, accepted: false);

        Assert.Contains("94 of 292 entries (32.2% of the manifest)", plan.Line, StringComparison.Ordinal);
        Assert.Contains("estimated 21.5 min", plan.Line, StringComparison.Ordinal);
        Assert.False(plan.Run);
    }

    [Fact]
    public void ASelectionPastTheThresholdRunsOnlyWhenAccepted()
    {
        Assert.True(Touched.PlanFor(94, 292, 13.7, 11, accepted: true).Run);
        Assert.True(Touched.PlanFor(10, 292, 13.7, 11, accepted: false).Run);
    }

    /// <summary>An accepted selection runs under its own ceiling, not the shards' 15 minutes, which it would exceed by construction.</summary>
    [Fact]
    public void AnAcceptedSelectionRunsUnderItsOwnCeiling()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), Touched.PlanFor(10, 292, 13.7, 11, accepted: false).Ceiling);
        Assert.True(Touched.PlanFor(80, 296, 13.7, 11, accepted: true).Ceiling > TimeSpan.FromMinutes(18.5));
    }

    [Fact]
    public void AnEmptySelectionRunsNothingAndSaysSo()
    {
        var plan = Touched.PlanFor(0, 292, 13.7, 11, accepted: false);
        Assert.False(plan.Run);
        Assert.Contains("0 of 292 entries", plan.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRateAndTheThresholdAreReadFromTheirFile()
    {
        var (rate, threshold) = Touched.ParseConfig("# comment\nseconds-per-entry 13.7\nconfirm-above-minutes 11\n");
        Assert.Equal(13.7, rate);
        Assert.Equal(11, threshold);
    }
}
