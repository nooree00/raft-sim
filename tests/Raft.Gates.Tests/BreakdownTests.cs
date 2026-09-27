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

    [Fact]
    public void AnOutcomeWithoutAVerdictFails() =>
        Assert.Contains(Check(GitFixture.Breakdown().Replace("- **Outcome:** pending", "- **Outcome:** it went fine", StringComparison.Ordinal)).Failures,
            m => m.Contains("Outcome must be", StringComparison.Ordinal));
}
