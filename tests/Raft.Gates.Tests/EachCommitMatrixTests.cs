using System;
using System.IO;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// The every-commit matrix's collect decision (P1-12), on recorded-shape jobs responses: every rule
/// has a response that must fail it. Sabotages S-each-3 and S-each-4 disable one rule each.
/// </summary>
public sealed class EachCommitMatrixTests
{
    private const string A = "1111111111111111111111111111111111111111";
    private const string B = "2222222222222222222222222222222222222222";

    private static string Job(string sha, string conclusion = "success", string checks = "success", string classify = "success") => $$"""
        {"name":"commit {{sha}}","conclusion":"{{conclusion}}","steps":[
          {"name":"Set up job","conclusion":"success"},
          {"name":"classify","conclusion":"{{classify}}"},
          {"name":"checks","conclusion":"{{checks}}"}]}
        """;

    private static string Jobs(params string[] jobs) =>
        $$"""{"total_count":{{jobs.Length + 2}},"jobs":[{"name":"each-commit-list","conclusion":"success","steps":[]},{{string.Join(",", jobs)}},{"name":"each-commit","conclusion":null,"steps":[]}]}""";

    private static Findings Evaluate(string[] range, string listed, string jobs)
    {
        var f = new Findings();
        EachCommitMatrix.Evaluate(range, listed, jobs, f);
        return f;
    }

    [Fact]
    public void OnePassingJobPerCommitPasses()
    {
        var f = Evaluate([A, B], $"[\"{A}\",\"{B}\"]", Jobs(Job(A), Job(B)));

        Assert.Empty(f.Failures);
        Assert.Contains(f.Notes, n => n.Contains("2 passed, 0 pre-gate", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyListWhenThePushCarriedCommitsFails() =>
        Assert.Contains(Evaluate([A, B], "[]", Jobs()).Failures, m => m.Contains("named 0 commit(s) but the range holds 2", StringComparison.Ordinal));

    [Fact]
    public void AListThatDiffersFromTheRangeFails() =>
        Assert.NotEmpty(Evaluate([A, B], $"[\"{A}\"]", Jobs(Job(A))).Failures);

    [Fact]
    public void AnEmptyRangeWithAnEmptyListPasses() =>
        Assert.Empty(Evaluate([], "[]", """{"jobs":[]}""").Failures);

    [Fact]
    public void ACommitWithNoJobFails() =>
        Assert.Contains(Evaluate([A, B], $"[\"{A}\",\"{B}\"]", Jobs(Job(A))).Failures, m => m.Contains("222222: 0 jobs", StringComparison.Ordinal));

    [Fact]
    public void ARedCommitFails() =>
        Assert.Contains(Evaluate([A, B], $"[\"{A}\",\"{B}\"]", Jobs(Job(A), Job(B, conclusion: "failure", checks: "failure"))).Failures,
            m => m.StartsWith("2222222", StringComparison.Ordinal));

    [Fact]
    public void APreGateCommitIsReportedAsPreGateNotPassed()
    {
        var f = Evaluate([A, B], $"[\"{A}\",\"{B}\"]", Jobs(Job(A, checks: "skipped"), Job(B)));

        Assert.Empty(f.Failures);
        Assert.Contains(f.Notes, n => n.Contains("1111111: pre-gate", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("1 passed, 1 pre-gate", StringComparison.Ordinal));
    }

    [Fact]
    public void ASkippedCheckWithoutASuccessfulClassificationFails() =>
        Assert.NotEmpty(Evaluate([A], $"[\"{A}\"]", Jobs(Job(A, checks: "skipped", classify: "failure"))).Failures);

    [Fact]
    public void AJobForACommitOutsideTheRangeFails() =>
        Assert.Contains(Evaluate([A], $"[\"{A}\"]", Jobs(Job(A), Job(B))).Failures, m => m.Contains("not in the range", StringComparison.Ordinal));

    [Fact]
    public void AListThatIsNotJsonFails() =>
        Assert.NotEmpty(Evaluate([A], "", Jobs(Job(A))).Failures);

    private static GitFixture WithCommitScript()
    {
        var g = new GitFixture();
        g.Write("scripts/ci-commit.sh", File.ReadAllText(Repo.Locate(null).PathOf("scripts/ci-commit.sh"))).Write("README.md", "x");
        return g;
    }

    private static void Script(GitFixture g, string name, int exit)
    {
        g.Write($"scripts/{name}.sh", $"#!/usr/bin/env bash\necho {name}\nexit {exit}\n");
        Proc.Run("chmod", g.Root, "+x", $"scripts/{name}.sh");
    }

    [Fact]
    public void ACommitFromBeforeTheGateScriptsIsClassifiedPreGateAndALaterOneGated()
    {
        using var g = WithCommitScript();
        var early = g.Commit("early");
        foreach (var s in new[] { "ci-build", "ci-test", "ci-gates" })
        {
            Script(g, s, 0);
        }

        var late = g.Commit("late");

        Assert.Contains("kind=pre-gate", Proc.Run("bash", g.Root, "scripts/ci-commit.sh", "classify", early).StdOut, StringComparison.Ordinal);
        Assert.Contains("kind=gated", Proc.Run("bash", g.Root, "scripts/ci-commit.sh", "classify", late).StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChecksOfAGatedCommitFailWhenItsOwnTestScriptFails()
    {
        using var g = WithCommitScript();
        Script(g, "ci-build", 0);
        Script(g, "ci-gates", 0);
        Script(g, "ci-test", 1);
        var sha = g.Commit("red");

        var r = Proc.Run("bash", g.Root, "scripts/ci-commit.sh", "run", sha);

        Assert.False(r.Ok, r.ToString());
        Assert.Contains("ci-test", r.StdOut, StringComparison.Ordinal);
    }
}
