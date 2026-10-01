using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>The breakdown format rules, each with a malformed task that must trip it.</summary>
public sealed class BreakdownTests
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal) { "S-widget-1" };

    private static Findings Check(string markdown, bool mustResolve = true)
    {
        var f = new Findings();
        var tasks = Breakdown.Parse("b.md", markdown.Split('\n'), f);
        foreach (var t in tasks)
        {
            Breakdown.CheckTask(t, Known, new HashSet<string>(StringComparer.Ordinal), f, mustResolve);
        }

        if (tasks.Count == 0)
        {
            f.Fail("no tasks");
        }

        return f;
    }

    [Fact]
    public void AWellFormedTaskPasses() =>
        Assert.Empty(Check(GitFixture.Breakdown()).Failures);

    [Theory]
    [InlineData("- **Vacuity:** It could pass with no widget.\n", "missing field Vacuity")]
    [InlineData("- **Vacuity:** It could pass with no widget.", "placeholder")]
    public void AMissingOrPlaceholderFieldFails(string remove, string expected)
    {
        var md = GitFixture.Breakdown();
        md = expected == "placeholder" ? md.Replace(remove, "- **Vacuity:** TBD", StringComparison.Ordinal) : md.Replace(remove, "", StringComparison.Ordinal);

        Assert.Contains(Check(md).Failures, m => m.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownSabotageIdFailsOnceTheTaskHasStarted()
    {
        var md = GitFixture.Breakdown().Replace("S-widget-1", "S-widget-9", StringComparison.Ordinal);

        Assert.Contains(Check(md).Failures, m => m.Contains("S-widget-9 has no", StringComparison.Ordinal));
        Assert.Empty(Check(md, mustResolve: false).Failures);
    }

    [Fact]
    public void AnUnparseableHeadingFails() =>
        Assert.Contains(Check(GitFixture.Breakdown().Replace("### P9-01 — Widget", "### P9-1 Widget", StringComparison.Ordinal)).Failures,
            m => m.Contains("unparseable task heading", StringComparison.Ordinal));

    [Fact]
    public void APredictionWithoutAnObservableFails() =>
        Assert.Contains(Check(GitFixture.Breakdown().Replace(" **Observable:** a red test.", "", StringComparison.Ordinal)).Failures,
            m => m.Contains("no **Observable:**", StringComparison.Ordinal));

    [Theory]
    [InlineData("- **Outcome:** right (evidence) — it overflowed.", true)]
    [InlineData("- **Outcome:** wrong (forcing) — writing it down made me avoid it.", true)]
    [InlineData("- **Outcome:** right — it overflowed.", false)]
    [InlineData("- **Outcome:** right (lucky) — it overflowed.", false)]
    public void AnOutcomeMustSayWhetherItIsEvidenceOrForcing(string outcome, bool valid)
    {
        var f = Check(GitFixture.Breakdown().Replace("- **Outcome:** pending", outcome, StringComparison.Ordinal));

        Assert.Equal(valid, !f.Failures.Any(m => m.Contains("Outcome must be", StringComparison.Ordinal)));
    }

    private static BreakdownTask Task(string id, string sabotage, string outcome = "pending")
    {
        var md = GitFixture.Breakdown()
            .Replace("### P9-01", "### " + id, StringComparison.Ordinal)
            .Replace("- **Sabotage:** S-widget-1", "- **Sabotage:** " + sabotage, StringComparison.Ordinal)
            .Replace("- **Outcome:** pending", "- **Outcome:** " + outcome, StringComparison.Ordinal);
        return Assert.Single(Breakdown.Parse($"docs/phases/{id[..id.IndexOf('-', StringComparison.Ordinal)]}/breakdown.md", md.Split('\n'), new Findings()));
    }

    [Fact]
    public void ASabotageIdOwnedByADoneTaskInOnePhaseAndCitedByAPendingTaskInAnotherFails()
    {
        var f = new Findings();
        Breakdown.CheckOwnership([Task("P0-15", "S-hist-1, S-hist-2", "right (evidence) — the oracle agreed."), Task("P5-01", "S-hist-1")], f);

        Assert.Contains(f.Failures, m => m.StartsWith("S-hist-1: cited as its own by P0-15 and P5-01", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Failures, m => m.StartsWith("S-hist-2", StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherTasksIdIsCitedAsSharedAndASharedIdNobodyOwnsFails()
    {
        var ok = new Findings();
        Breakdown.CheckOwnership([Task("P0-10", "S-pre-4"), Task("P0-05", "S-ci-1; shared: S-pre-4; manual: one red run by hand")], ok);
        Assert.Empty(ok.Failures);

        var orphan = new Findings();
        Breakdown.CheckOwnership([Task("P0-05", "S-ci-1; shared: S-pre-9")], orphan);
        Assert.Contains(orphan.Failures, m => m.Contains("shares S-pre-9, which must be owned by exactly one other task (owned by: none)", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOutcomeWithoutAVerdictFails() =>
        Assert.Contains(Check(GitFixture.Breakdown().Replace("- **Outcome:** pending", "- **Outcome:** it went fine", StringComparison.Ordinal)).Failures,
            m => m.Contains("Outcome must be", StringComparison.Ordinal));
}
