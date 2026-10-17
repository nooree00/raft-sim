using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P16-06: an entry whose outcome is not the one it expects leaves its run's output in the log. Vacuity
/// risk: output kept and never printed passes every other test, so the test drives the classification
/// with a run whose results omit the target and requires the output's tail in what is reported, and
/// none for an expected outcome. Sabotage S-out-1.
/// </summary>
public sealed class HarnessOutputTests
{
    private static readonly SabotageSpec Spec = new("S-a-1", new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kind"] = "test",
        ["project"] = "tests/Raft.X.Tests",
        ["target"] = "Raft.X.Tests.C.M",
        ["expect"] = "caught",
    }, "patch.diff", null);

    private static readonly string Output = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i}"));

    [Fact]
    public void AnUnexpectedOutcomeReportsItsRunsLastLinesAndAnExpectedOneNone()
    {
        var missed = Sabotage.ClassifyTestRun(Spec, new Sabotage.EntryRun([new("Raft.X.Tests.C.Other", "Passed", "")], Output), TimeSpan.Zero);
        Assert.Equal("target-not-run", missed.Result);

        var f = new Findings();
        Sabotage.Report(f, Spec.Id, missed, "caught", "S-a-1 target-not-run");
        Assert.Equal(["S-a-1 target-not-run"], f.Failures);
        var printed = Assert.Single(f.Notes);
        Assert.StartsWith($"entry output, S-a-1 (target-not-run, expected caught), its last {Sabotage.OutputLines} lines:", printed, StringComparison.Ordinal);
        var lines = printed.Split('\n').Skip(1).Select(l => l.Trim()).ToList();
        Assert.Equal(Enumerable.Range(200 - Sabotage.OutputLines + 1, Sabotage.OutputLines).Select(i => $"line {i}"), lines);

        var caught = Sabotage.ClassifyTestRun(Spec, new Sabotage.EntryRun([new("Raft.X.Tests.C.M", "Failed", "boom")], Output), TimeSpan.Zero);
        Assert.Equal("caught", caught.Result);
        var g = new Findings();
        Sabotage.Report(g, Spec.Id, caught, "caught", "S-a-1 caught");
        Assert.Empty(g.Failures);
        Assert.Equal(["S-a-1 caught"], g.Notes);
    }
}
