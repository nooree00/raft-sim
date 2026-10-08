using System;
using System.Collections.Generic;
using System.IO;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P12-01: docs/phases/status.md is kept by hand, so the register gate checks it against the reports,
/// both ways. Every rule has a case that must fail it. Sabotages S-status-1 (an accepted report's
/// phase left unmarked passes) and S-status-2 (a phase marked complete with no accepted report passes).
/// </summary>
public sealed class RegisterCompletionTests
{
    private static Dictionary<string, string?> Reports(params (string Phase, string? Status)[] reports)
    {
        var d = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (phase, status) in reports)
        {
            d[phase] = status;
        }

        return d;
    }

    private static readonly string[] Accepted = ["P1", "P5", "P10", "P11"];

    private static HashSet<string> Complete(params string[] phases) => new(phases, StringComparer.Ordinal);

    [Fact]
    public void ReportsAndStatusThatAgreePass() =>
        Assert.Empty(Register.CompletionProblems(Reports(("P1", "accepted by the reviewer"), ("P2", "for review.")), Complete("P1")));

    /// <summary>Phase 11's acceptance: four accepted phases unmarked, and a row left open behind them. Sabotage S-status-1.</summary>
    [Fact]
    public void AnAcceptedReportWhosePhaseIsNotMarkedCompleteFails() =>
        Assert.Contains(
            Register.CompletionProblems(Reports(("P8", "accepted (reviewer, on run 1)")), Complete()),
            p => p.Contains("P8: its report says it is accepted", StringComparison.Ordinal));

    /// <summary>The one exception: P0's open row is the person's cold walk, which no report closes.</summary>
    [Fact]
    public void P0MayStayInProgressWithAnAcceptedReport() =>
        Assert.Empty(Register.CompletionProblems(Reports(("P0", "accepted by the reviewer; one register row promised to P0 is open")), Complete()));

    /// <summary>How a phase is closed without evidence: marked complete, and no report behind it. Sabotage S-status-2.</summary>
    [Fact]
    public void APhaseMarkedCompleteWithNoReportFails() =>
        Assert.Contains(
            Register.CompletionProblems(Reports(), Complete("P9")),
            p => p.Contains("P9 marked complete without docs/phases/P9/report.md", StringComparison.Ordinal));

    /// <summary>Marked complete while its report is still for review. Sabotage S-status-2.</summary>
    [Fact]
    public void APhaseMarkedCompleteWhoseReportIsNotAcceptedFails() =>
        Assert.Contains(
            Register.CompletionProblems(Reports(("P9", "for review.")), Complete("P9")),
            p => p.Contains("P9 marked complete, and its report does not say it is accepted", StringComparison.Ordinal));

    [Fact]
    public void AReportWithNoStatusLineFails() =>
        Assert.Contains(
            Register.CompletionProblems(Reports(("P3", null)), Complete()),
            p => p.Contains("docs/phases/P3/report.md has no '**Status:' line", StringComparison.Ordinal));

    /// <summary>The vacuity guard: every report of this repository has a status line the gate parses, and every accepted one is read as accepted.</summary>
    [Fact]
    public void EveryReportHereHasAStatusTheGateReads()
    {
        var statuses = Register.ReportStatuses(Repo.Locate(RepoRoot()));

        Assert.True(statuses.Count >= 12, $"{statuses.Count} reports found");
        Assert.All(statuses, s => Assert.False(s.Value is null, $"{s.Key}: no status line"));
        Assert.All(Accepted, p => Assert.StartsWith("accepted", statuses[p], StringComparison.Ordinal));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RAFT_PROJECT_SPEC.md")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }
}
