using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// The preflight's workflow checks. Vacuity risk: the checks are regexes, and a regex that never
/// matches passes everything; so each rule has a line that must trip it, next to lines that must not.
/// </summary>
public sealed class WorkflowCheckTests
{
    private const string Pinned = "mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29";
    private const string Sha = "3d3c42e5aac5ba805825da76410c181273ba90b1";

    private static string[] Problems(params string[] lines)
    {
        var seen = false;
        return Preflight.WorkflowProblems(lines, Pinned, ref seen).ToArray();
    }

    [Fact]
    public void ACleanWorkflowHasNoProblems()
    {
        var seen = false;
        var problems = Preflight.WorkflowProblems(
            [$"    container: {Pinned}", $"      - uses: actions/checkout@{Sha}", "      - run: scripts/ci-build.sh"],
            Pinned, ref seen).ToArray();

        Assert.Empty(problems);
        Assert.True(seen);
    }

    [Theory]
    [InlineData("    container: mcr.microsoft.com/dotnet/sdk:10.0")]
    [InlineData("    container: mcr.microsoft.com/dotnet/sdk:10.0.401")]
    [InlineData("    container: mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    public void AnSdkImageOtherThanThePinnedDigestIsAProblem(string line) =>
        Assert.Single(Problems(line));

    [Theory]
    [InlineData("      - run: docker run --rm zricethezav/gitleaks:v8.28.0 detect")]
    [InlineData("    image: zricethezav/gitleaks:latest")]
    public void AnyOtherImageWithoutADigestIsAProblem(string line) =>
        Assert.Single(Problems(line));

    [Theory]
    [InlineData("      - uses: actions/checkout@v7")]
    [InlineData("      - uses: actions/checkout@v7.0.1")]
    [InlineData("      - uses: actions/checkout@main")]
    public void AnActionNotPinnedByCommitShaIsAProblem(string line) =>
        Assert.Single(Problems(line));

    [Theory]
    [InlineData("        continue-on-error: true")]
    [InlineData("      - run: dotnet test || true")]
    [InlineData("      - run: set +e; dotnet test")]
    public void SuppressedFailureIsAProblem(string line) =>
        Assert.Single(Problems(line));

    [Fact]
    public void ACommentMentioningARuleIsNotAProblem() =>
        Assert.Empty(Problems("      # never use continue-on-error or || true here"));
}
