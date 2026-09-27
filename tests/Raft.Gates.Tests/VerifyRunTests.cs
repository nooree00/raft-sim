using System;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// The CI-run verification's decision, on recorded-shape API responses. Each rule has a response
/// that must fail it. Sabotages S-run-1..3 disable one rule each.
/// </summary>
public sealed class VerifyRunTests
{
    private const string Sha = "6e6dc046953bd3251c8555dc7c9654993479a160";

    private static string Runs(string conclusion, string sha = Sha, string status = "completed") => $$"""
        {"total_count":1,"workflow_runs":[{"id":7,"name":"ci","run_number":3,"run_attempt":1,
          "head_sha":"{{sha}}","status":"{{status}}","conclusion":"{{conclusion}}"}]}
        """;

    private static string Jobs(string eachCommit = "success") => $$"""
        {"total_count":4,"jobs":[
          {"name":"build","conclusion":"success","started_at":"2026-09-27T16:21:32Z","completed_at":"2026-09-27T16:26:00Z"},
          {"name":"each-commit","conclusion":"{{eachCommit}}","started_at":"2026-09-27T16:21:32Z","completed_at":"2026-09-27T16:23:00Z"},
          {"name":"secrets","conclusion":"success","started_at":"2026-09-27T16:21:32Z","completed_at":"2026-09-27T16:22:00Z"},
          {"name":"readme-walk","conclusion":"success","started_at":"2026-09-27T16:21:32Z","completed_at":"2026-09-27T16:24:00Z"}]}
        """;

    private static Findings Evaluate(string runs, string jobs)
    {
        var f = new Findings();
        VerifyRun.Evaluate(Sha, runs, _ => jobs, f);
        return f;
    }

    [Fact]
    public void AGreenRunWithEveryJobPasses() =>
        Assert.Empty(Evaluate(Runs("success"), Jobs()).Failures);

    [Fact]
    public void ARedRunFails() =>
        Assert.Contains(Evaluate(Runs("failure"), Jobs()).Failures, m => m.Contains("completed/failure", StringComparison.Ordinal));

    [Fact]
    public void ACommitWithNoRunFails() =>
        Assert.Contains(Evaluate("""{"total_count":0,"workflow_runs":[]}""", Jobs()).Failures, m => m.Contains("no ci workflow run", StringComparison.Ordinal));

    [Fact]
    public void ASkippedJobIsNotAPass() =>
        Assert.Contains(Evaluate(Runs("success"), Jobs(eachCommit: "skipped")).Failures, m => m.Contains("'each-commit' concluded skipped", StringComparison.Ordinal));

    [Fact]
    public void ARunForAnotherCommitIsRejected() =>
        Assert.Contains(Evaluate(Runs("success", sha: "0000000000000000000000000000000000000001"), Jobs()).Failures, m => m.Contains("another commit", StringComparison.Ordinal));
}
