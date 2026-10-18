using System;
using System.Collections.Generic;
using System.IO;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P16-09: the README walk removes its copy of the working tree on exit, whether its steps pass or fail. It
/// once left every copy behind in a fresh temporary directory, and 195 of them, about 109 MB each, filled the
/// disk. Each run here walks a small repository of its own (a README with one cold-walk step, and the walk
/// script copied in) under an empty <c>TMPDIR</c>, which must be empty afterwards; a fake <c>docker</c> on
/// the PATH answers the walk's removal of its NuGet volume, so nothing real is touched. Vacuity risk: a
/// cleanup on the passing path only, guarded by the failing walk. Sabotage S-walk-2 (the removal dropped).
/// </summary>
public sealed class ReadmeWalkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("readme-walk-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (ProcessResult Run, string Tmp) Walk(string step)
    {
        var bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(bin);
        var docker = Path.Combine(bin, "docker");
        File.WriteAllText(docker, "#!/usr/bin/env bash\nexit 0\n");
        Assert.True(Proc.Run("chmod", _dir, "+x", docker).Ok);

        var repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "scripts"));
        var script = Path.Combine(repo, "scripts", "readme-walk.sh");
        File.Copy(Repo.Locate(null).PathOf("scripts/readme-walk.sh"), script);
        File.WriteAllText(Path.Combine(repo, "README.md"), $"# A walk\n\n```sh cold-walk\n{step}\n```\n");
        Assert.True(Proc.Run("git", repo, "init", "-q").Ok);

        var tmp = Path.Combine(_dir, "tmp");
        Directory.CreateDirectory(tmp);
        var env = new Dictionary<string, string>
        {
            ["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH"),
            ["TMPDIR"] = tmp,
        };
        return (Proc.RunWithEnv("bash", repo, env, script), tmp);
    }

    [Fact]
    public void APassingWalkLeavesNothingBehind()
    {
        var (r, tmp) = Walk("echo walked");

        Assert.True(r.ExitCode == 0, r.StdOut + r.StdErr);
        Assert.Contains("walked", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("readme-walk: completed", r.StdOut, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tmp));
    }

    /// <summary>The failing step's output is in the walk's own output, so removing its log loses nothing. Sabotage S-walk-2.</summary>
    [Fact]
    public void AFailingWalkLeavesNothingBehindAndItsOutputIsPrinted()
    {
        var (r, tmp) = Walk("echo the step broke; exit 3");

        Assert.True(r.ExitCode == 3, r.StdOut + r.StdErr);
        Assert.Contains("the step broke", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("readme-walk: stopped (exit 3)", r.StdOut, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tmp));
    }
}
