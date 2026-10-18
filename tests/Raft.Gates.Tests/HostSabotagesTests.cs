using System;
using System.Collections.Generic;
using System.IO;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P16-10: the host sabotages remove each entry's directory, its worktree and the temporary directories its
/// baseline and command make, whether the entry is caught or its baseline fails. They once removed only the
/// worktree, leaving its parent and the secret-scan entries' clones behind on every run, as the README walk
/// left its copies (P16-09). Each run here is the script on a small repository of its own (the script and its
/// entries, committed) under an empty <c>TMPDIR</c>, which must be empty afterwards, and the repository must
/// register no worktree but its own. Each entry's baseline and command make a directory with
/// <c>mktemp -d</c>, as the secret-scan entries clone into one. Vacuity risk: a removal on the caught path
/// only, guarded by the failing baseline. Sabotage S-hostsab-1 (the entry's directory no longer removed).
/// </summary>
public sealed class HostSabotagesTests : IDisposable
{
    private const string MakesADirectory = "d=$(mktemp -d) && touch \"$d/clone\"";

    private readonly string _dir = Directory.CreateTempSubdirectory("host-sabotages-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (ProcessResult Run, string Tmp, string Repo) Sabotages(params (string Id, string Baseline, string Command, string Message)[] entries)
    {
        var repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "scripts"));
        var script = Path.Combine(repo, "scripts", "host-sabotages.sh");
        File.Copy(Repo.Locate(null).PathOf("scripts/host-sabotages.sh"), script);
        foreach (var (id, baseline, command, message) in entries)
        {
            var entry = Path.Combine(repo, "sabotage", id);
            Directory.CreateDirectory(entry);
            File.WriteAllText(Path.Combine(entry, "sabotage.txt"), $"runner: host\nbaseline: {baseline}\ncommand: {command}\nmessage: {message}\n");
        }

        Assert.True(Proc.Run("git", repo, "init", "-q").Ok);
        Assert.True(Proc.Run("git", repo, "add", "-A").Ok);
        Assert.True(Proc.Run("git", repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "entries").Ok);

        var tmp = Path.Combine(_dir, "tmp");
        Directory.CreateDirectory(tmp);
        var env = new Dictionary<string, string> { ["TMPDIR"] = tmp };
        return (Proc.RunWithEnv("bash", repo, env, script), tmp, repo);
    }

    private static void NoWorktreeLeft(string repo)
    {
        var list = Proc.Run("git", repo, "worktree", "list", "--porcelain");
        Assert.True(list.Ok, list.StdErr);
        Assert.Single(list.StdOut.Split('\n'), l => l.StartsWith("worktree ", StringComparison.Ordinal));
    }

    [Fact]
    public void CaughtEntriesLeaveNothingBehind()
    {
        var (r, tmp, repo) = Sabotages(
            ("S-a-1", MakesADirectory, $"{MakesADirectory} && echo the leak was found && exit 1", "the leak was found"),
            ("S-a-2", MakesADirectory, $"{MakesADirectory} && echo found again && exit 1", "found again"));

        Assert.True(r.ExitCode == 0, r.StdOut + r.StdErr);
        Assert.Contains("S-a-1: caught", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("S-a-2: caught", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("2 host sabotages, 0 failure(s)", r.StdOut, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tmp));
        NoWorktreeLeft(repo);
    }

    /// <summary>A failing baseline leaves through a branch of its own, before the command runs. Sabotage S-hostsab-1.</summary>
    [Fact]
    public void AFailingBaselineLeavesNothingBehind()
    {
        var (r, tmp, repo) = Sabotages(
            ("S-a-1", $"{MakesADirectory} && echo the baseline broke && exit 1", "exit 1", "never read"),
            ("S-a-2", MakesADirectory, $"{MakesADirectory} && echo the leak was found && exit 1", "the leak was found"));

        Assert.True(r.ExitCode == 1, r.StdOut + r.StdErr);
        Assert.Contains("S-a-1: BASELINE FAILS", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("the baseline broke", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("S-a-2: caught", r.StdOut, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tmp));
        NoWorktreeLeft(repo);
    }
}
