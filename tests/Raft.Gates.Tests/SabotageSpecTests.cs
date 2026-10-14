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

    /// <summary>
    /// P14-01: an entry is retired only when its target is gone, so retiring cannot silence a live check
    /// (S-retire-1). The lookup is the caller's, so the rule is tested apart from the file system.
    /// </summary>
    [Fact]
    public void RetiringAnEntryWhoseTargetStillExistsFails()
    {
        var f = new Findings();
        var entry = Path.Combine(_dir, "S-x-1");
        Directory.CreateDirectory(entry);
        File.WriteAllText(Path.Combine(entry, "sabotage.txt"), Test("Raft.X.Tests.C.M", "retired: 0abc3e4 its code removed\n"));
        File.WriteAllText(Path.Combine(entry, "patch.diff"), Patch);
        SabotageSpec.Load("S-x-1", entry, [Broad], f, (_, _) => true);
        Assert.Contains(f.Failures, e => e.Contains("still exists in tests/P", StringComparison.Ordinal));
        var gone = new Findings();
        Assert.Equal("0abc3e4 its code removed", SabotageSpec.Load("S-x-1", entry, [Broad], gone, (_, _) => false)!.Retired);
        Assert.Empty(gone.Failures);
    }

    [Theory]
    [InlineData("retired: its code removed\n", "must be '<commit> <reason>'")]
    [InlineData("retired: 0abc3e4\n", "must be '<commit> <reason>'")]
    public void ARetirementNamesTheCommitItLastRanAtAndWhy(string line, string expected) =>
        Assert.Contains(Load(Test("Raft.X.Tests.C.M", line)), e => e.Contains(expected, StringComparison.Ordinal));

    [Fact]
    public void OnlyATestEntryCanBeRetired() =>
        Assert.Contains(Load("description: d\nmechanism: m\nkind: command\ncommand: false\nmessage: x\nexpect: caught\nretired: 0abc3e4 gone\n"),
            e => e.Contains("only a test entry can be retired", StringComparison.Ordinal));

    /// <summary>The lookup itself, on the tree: a test that exists is found and a removed one is not, so a lookup that never finds anything (every retirement allowed) fails here.</summary>
    [Fact]
    public void ATargetIsFoundInItsProjectOnlyWhileItsMethodIsThere()
    {
        var repo = Repo.Locate(null);
        Assert.True(SabotageSpec.TargetExists(repo, "tests/Raft.Gates.Tests", "Raft.Gates.Tests.SabotageSpecTests.AWellFormedEntryLoadsCleanly"));
        Assert.True(SabotageSpec.TargetExists(repo, "tests/Raft.Gates.Tests", "Raft.Gates.Tests.SabotageSpecTests.ARetirementNamesTheCommitItLastRanAtAndWhy(line: \"x\", expected: \"y\")"));
        Assert.False(SabotageSpec.TargetExists(repo, "tests/Raft.Gates.Tests", "Raft.Gates.Tests.SabotageSpecTests.NoSuchTest"));
        Assert.False(SabotageSpec.TargetExists(repo, "tests/Raft.Host.Tests", "Raft.Host.Tests.EffectThreadTests.NothingRunsAfterAFailedSync"));
    }

    /// <summary>The harness, the patch gate and the cost file read the live entries: the committed retirements are loaded, and left out of them.</summary>
    [Fact]
    public void TheRetiredEntriesAreLoadedButDoNotRun()
    {
        var repo = Repo.Locate(null);
        var all = SabotageSpec.LoadAll(repo, new Findings());
        var live = SabotageSpec.LoadLive(repo, new Findings()).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var retired = all.Where(s => s.Retired is not null).Select(s => s.Id).ToList();
        Assert.NotEmpty(retired);
        Assert.All(retired, id => Assert.DoesNotContain(id, live));
        Assert.Equal(all.Count - retired.Count, live.Count);
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
