using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P3-01: a harness entry says what it breaks and, where its target goes red at almost any change,
/// why the target went red. Vacuity risk: a broad-target list that names no real test makes the
/// rule pass by matching nothing; guarded by requiring every line to name a test method that
/// exists. Sabotage S-audit-3 (the broad-target rule removed from the loader); S-audit-1 and
/// S-audit-2 are the harness's own controls for `reason:` and `control.diff`.
/// </summary>
public sealed partial class SabotageSpecTests : IDisposable
{
    private const string Patch = "--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1 +1 @@\n-x\n+y\n";
    private const string Broad = "Raft.Simulation.Tests.DeterminismTests.TheGoldenTraceIsReproducedExactly";
    private readonly string _dir = Directory.CreateTempSubdirectory("sabotage-spec-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private IReadOnlyList<string> Load(string fields, string? control = null, string patch = Patch)
    {
        var entry = Path.Combine(_dir, "S-x-1");
        Directory.CreateDirectory(entry);
        File.WriteAllText(Path.Combine(entry, "sabotage.txt"), fields);
        File.WriteAllText(Path.Combine(entry, "patch.diff"), patch);
        if (control is not null)
        {
            File.WriteAllText(Path.Combine(entry, "control.diff"), control);
        }

        var f = new Findings();
        SabotageSpec.Load("S-x-1", entry, [Broad], f);
        return f.Failures;
    }

    private static string Test(string target, string extra = "", bool mechanism = true) =>
        "description: d\n" + (mechanism ? "mechanism: m\n" : "") + $"kind: test\nproject: tests/P\ntarget: {target}\nexpect: caught\n" + extra;

    [Fact]
    public void AWellFormedEntryLoadsCleanly() =>
        Assert.Empty(Load(Test("Raft.X.Tests.C.M")));

    [Fact]
    public void AnEntryWithoutAMechanismIsRejected() =>
        Assert.Contains(Load(Test("Raft.X.Tests.C.M", mechanism: false)), e => e.Contains("no mechanism", StringComparison.Ordinal));

    [Fact]
    public void ABroadTargetNeedsAReasonOrAControl()
    {
        Assert.Contains(Load(Test(Broad)), e => e.Contains("is a broad test", StringComparison.Ordinal));
        Assert.Contains(Load(Test(Broad + "(case: 1)")), e => e.Contains("is a broad test", StringComparison.Ordinal));
        Assert.Empty(Load(Test(Broad, "reason: line 3 differs\n")));
        Assert.Empty(Load(Test(Broad), control: "--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1 +1 @@\n-x\n+z\n"));
    }

    [Theory]
    [InlineData(" \n", "empty control")]
    [InlineData(Patch, "the patch itself")]
    [InlineData("--- a/src/B.cs\n+++ b/src/B.cs\n@@ -1 +1 @@\n-x\n+z\n", "shares no file")]
    public void AControlMustKeepPartOfThePatchAndNotBeThePatch(string control, string expected) =>
        Assert.Contains(Load(Test("Raft.X.Tests.C.M"), control), e => e.Contains(expected, StringComparison.Ordinal));

    [Fact]
    public void AReasonOrAControlBelongsToATestEntryOnly()
    {
        const string command = "description: d\nmechanism: m\nkind: command\ncommand: false\nmessage: x\nexpect: caught\n";
        Assert.Contains(Load(command + "reason: r\n"), e => e.Contains("only a test entry has one", StringComparison.Ordinal));
        Assert.Contains(Load(command, control: "--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1 +1 @@\n-x\n+z\n"), e => e.Contains("only a test entry has one", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryBroadTargetNamesATestMethodThatExists()
    {
        var repo = Repo.Locate(null);
        var lines = SabotageSpec.ParseBroadTargets(File.ReadAllText(repo.PathOf(SabotageSpec.BroadTargetsFile)));
        Assert.True(lines.Count >= 5, $"only {lines.Count} broad targets");
        foreach (var line in lines)
        {
            var m = TestName().Match(line);
            Assert.True(m.Success, $"'{line}' is not Raft.<Project>.Tests.<Class>.<Method>");
            var file = Path.Combine(repo.PathOf("tests"), $"Raft.{m.Groups[1].Value}.Tests", m.Groups[2].Value + ".cs");
            Assert.True(File.Exists(file) && File.ReadAllText(file).Contains(" " + m.Groups[3].Value + "(", StringComparison.Ordinal), $"{line}: no such test method in {file}");
        }
    }

    [Fact]
    public void TheCommittedManifestLoadsWithoutFailures()
    {
        var f = new Findings();
        var all = SabotageSpec.LoadAll(Repo.Locate(null), f);
        Assert.Empty(f.Failures);
        Assert.True(all.Count >= 100, $"only {all.Count} entries");
    }

    [GeneratedRegex(@"^Raft\.(\w+)\.Tests\.(\w+)\.(\w+)$")]
    private static partial Regex TestName();
}
