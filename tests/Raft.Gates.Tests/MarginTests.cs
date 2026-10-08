using System;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P12-08: the control-margin audit (phase 12 decision 7). A row is flagged when either side, the
/// unpatched code that must pass or the patched code that must fail, comes within a factor of 1.5
/// of the bound in any run, a timing taken at GitHub's worse end. Sabotage S-margin-1 (the patched
/// side not read, so a control whose sabotage reaches near the bound passes).
/// </summary>
public sealed class MarginTests
{
    private static Margins.Row Row(double bound, bool above, bool timing, double[] unpatched, double[] patched) =>
        new("check", bound, above, timing, unpatched, patched);

    [Fact]
    public void BothSidesFarFromTheBoundPass() =>
        Assert.False(Margins.Judge(Row(1.5, above: true, timing: false, [3.0, 3.2, 2.9], [0.0, 0.1])).Flagged);

    /// <summary>A threshold set on the control's own unpatched median: half its runs fail it.</summary>
    [Fact]
    public void AThresholdAtTheUnpatchedMedianIsFlagged() =>
        Assert.True(Margins.Judge(Row(1.05, above: true, timing: false, [1.04, 1.05, 1.06], [0.05])).Flagged);

    /// <summary>
    /// Phase 11's stall control: its unpatched side far above the bound, its patched side reaching
    /// within reach of it (S-bench-1 survived 2 of 13 runs on GitHub). Sabotage S-margin-1.
    /// </summary>
    [Fact]
    public void APatchedSideNearTheBoundIsFlagged()
    {
        var v = Margins.Judge(Row(150_000, above: true, timing: true, [235_000, 241_000, 248_000], [3_000, 4_000, 120_000]));

        Assert.True(v.UnpatchedMargin >= Margins.Required, $"unpatched margin {v.UnpatchedMargin}");
        Assert.True(v.Flagged, $"patched margin {v.PatchedMargin}");
    }

    /// <summary>A timing that must stay under its bound is judged at GitHub's high ratio, a dimensionless one as measured.</summary>
    [Fact]
    public void ATimingIsJudgedAtGitHubsWorseEnd()
    {
        Assert.False(Margins.Judge(Row(75_000, above: false, timing: false, [40_000, 45_000], [])).Flagged);
        Assert.True(Margins.Judge(Row(75_000, above: false, timing: true, [40_000, 45_000], [])).Flagged);
    }

    /// <summary>A count that must be zero: any unpatched run above zero is flagged; a patched side of hundreds is far from it.</summary>
    [Fact]
    public void ACountThatMustBeZero()
    {
        Assert.False(Margins.Judge(Row(0, above: false, timing: false, [0, 0, 0], [1_866, 2_443])).Flagged);
        Assert.True(Margins.Judge(Row(0, above: false, timing: false, [0, 1, 0], [2_443])).Flagged);
    }

    [Fact]
    public void ARowIsReadFromItsColumns()
    {
        var rows = Margins.Parse(["# comment", "stall-open-p97\t150000\tabove\ttiming\t235000,248000\t3000,3900\tS-bench-1", "handoff-high\t2.0\tbelow\tratio\t1.04,1.22\t-"]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(("stall-open-p97", 150_000.0, true, true, 2, 2, "S-bench-1"), (rows[0].Check, rows[0].Bound, rows[0].PassAbove, rows[0].Timing, rows[0].Unpatched.Count, rows[0].Patched.Count, rows[0].Note));
        Assert.Empty(rows[1].Patched);
        Assert.True(double.IsPositiveInfinity(Margins.Judge(rows[1]).PatchedMargin));
    }
}
