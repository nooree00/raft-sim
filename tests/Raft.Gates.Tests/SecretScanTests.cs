using System;
using System.Collections.Generic;
using System.IO;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// The secret scan has three outcomes and says which: clean (exit 0), leaks found (exit 1), and no
/// verdict (exit 2) when the scan could not run. Once a Docker daemon that was down rendered as
/// "leaks found": the docker CLI exits 1 when it cannot reach the daemon, and gitleaks exited 1 on
/// leaks (docs/findings.md). A fake <c>docker</c> on the PATH stands in for each measured exit, so
/// this runs where there is no Docker. Sabotages S-sec-3 (any failure is a finding again) and
/// S-sec-4 (a finding renders as no verdict); S-sec-5 takes the real daemon away on the host.
/// </summary>
public sealed class SecretScanTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("secret-scan-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ProcessResult Scan(int dockerExit)
    {
        var bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(bin);
        var docker = Path.Combine(bin, "docker");
        File.WriteAllText(docker, $"#!/usr/bin/env bash\nexit {dockerExit}\n");
        Assert.True(Proc.Run("chmod", _dir, "+x", docker).Ok);

        var repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(repo);
        Assert.True(Proc.Run("git", repo, "init", "-q").Ok);
        Assert.True(Proc.Run("git", repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "x").Ok);

        var script = Repo.Locate(null).PathOf("scripts/secret-scan.sh");
        var env = new Dictionary<string, string> { ["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH") };
        return Proc.RunWithEnv("bash", _dir, env, script, repo);
    }

    [Fact]
    public void ACleanScanPasses()
    {
        var r = Scan(0);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("secret-scan: no leaks found", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void GitleaksLeakExitIsAFinding()
    {
        var r = Scan(3);

        Assert.Equal(1, r.ExitCode);
        Assert.Contains("secret-scan: leaks found", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("NO VERDICT", r.StdOut, StringComparison.Ordinal);
    }

    /// <summary>1: daemon unreachable, or a gitleaks error; 125: docker's own failure; 126: bad flag; 127: no docker.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(125)]
    [InlineData(126)]
    [InlineData(127)]
    public void AScanThatDidNotRunGivesNoVerdictNeverAFindingOrAPass(int dockerExit)
    {
        var r = Scan(dockerExit);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("secret-scan: NO VERDICT", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("leaks found", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("no leaks", r.StdOut, StringComparison.Ordinal);
    }
}
