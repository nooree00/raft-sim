using System;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// The CI-run verification's decision, on recorded-shape API responses. Each rule has a response
/// that must fail it. Sabotages S-run-1..3 disable one rule each; S-run-4 and S-run-5 move the
/// phase from which the soak is required.
/// </summary>
public sealed class VerifyRunTests
{
    private const string Sha = "6e6dc046953bd3251c8555dc7c9654993479a160";

    private static string Runs(string conclusion, string sha = Sha, string status = "completed") => $$"""
        {"total_count":1,"workflow_runs":[{"id":7,"name":"ci","run_number":3,"run_attempt":1,
          "head_sha":"{{sha}}","status":"{{status}}","conclusion":"{{conclusion}}"}]}
        """;

    private static string Jobs(string eachCommit = "success", bool soak = true, bool membershipSoak = true) => $$"""
        {"total_count":6,"jobs":[{{(soak ? SoakJob : "")}}{{(membershipSoak ? MembershipSoakJob : "")}}
          {"name":"build","conclusion":"success","started_at":"2026-09-27T16:21:32Z","completed_at":"2026-09-27T16:26:00Z"},
          {"name":"each-commit","conclusion":"{{eachCommit}}","started_at":"2026-09-27T16:21:32Z","completed_at":"2026-09-27T16:23:00Z"},
          {"name":"secrets","conclusion":"success","started_at":"2026-09-27T16:21:32Z","completed_at":"2026-09-27T16:22:00Z"},
          {"name":"readme-walk","conclusion":"success","started_at":"2026-09-27T16:21:32Z","completed_at":"2026-09-27T16:24:00Z"}]}
        """;

    private const string SoakJob = """{"name":"soak","conclusion":"success","started_at":"2026-09-27T16:21:32Z","completed_at":"2026-09-27T16:26:00Z"},""";

    private const string MembershipSoakJob = """{"name":"soak-membership","conclusion":"success","started_at":"2026-09-27T16:21:32Z","completed_at":"2026-09-27T16:28:00Z"},""";

    private static Findings Evaluate(string runs, string jobs, int phase = 3)
    {
        var f = new Findings();
        VerifyRun.Evaluate(Sha, runs, _ => jobs, f, VerifyRun.RequiredFor(phase));
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

    /// <summary>P3 acceptance: the soak is required in its own right where a phase-3 report is certified.</summary>
    [Fact]
    public void APhaseThreeReportsRunWithoutTheSoakFails() =>
        Assert.Contains(Evaluate(Runs("success"), Jobs(soak: false)).Failures, m => m.Contains("has no job 'soak'", StringComparison.Ordinal));

    /// <summary>The P1 and P2 reports certify runs from before the soak job existed; requiring it there would turn every run red.</summary>
    [Fact]
    public void APhaseTwoReportsRunNeedsNoSoak() =>
        Assert.Empty(Evaluate(Runs("success"), Jobs(soak: false), phase: 2).Failures);

    /// <summary>P6-12: the membership soak is required in its own right where a phase-6 report is certified, and not before.</summary>
    [Fact]
    public void APhaseSixReportsRunWithoutTheMembershipSoakFails()
    {
        Assert.Contains(Evaluate(Runs("success"), Jobs(membershipSoak: false), phase: 6).Failures, m => m.Contains("has no job 'soak-membership'", StringComparison.Ordinal));
        Assert.Empty(Evaluate(Runs("success"), Jobs(membershipSoak: false), phase: 5).Failures);
    }
}
