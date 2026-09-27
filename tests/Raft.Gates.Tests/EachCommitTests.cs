using System;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// The every-commit check against fixture histories, with a trivial per-commit check ("the file
/// ok exists") in place of build-and-test. Sabotages S-each-1 and S-each-2 disable parts of it.
/// </summary>
public sealed class EachCommitTests
{
    private const string Check = "test -f ok";

    private static (GitFixture G, string Base) History(bool middleGreen)
    {
        var g = new GitFixture();
        g.Write("Raft.slnx", "<Solution />").Write("ok", "").Commit("base");
        var b = g.Git("rev-parse", "HEAD").Trim();
        g.Git("branch", "-q", "main-copy");
        g.Write("a", "1").Commit("first");
        if (!middleGreen)
        {
            g.Git("rm", "-q", "ok");
        }

        g.Write("b", "1").Commit("middle");
        g.Write("ok", "").Write("c", "1").Commit("head");
        return (g, b);
    }

    [Fact]
    public void AllGreenPasses()
    {
        var (g, b) = History(middleGreen: true);
        using (g)
        {
            var f = EachCommit.Run(g.Repo, ["--since", b, "--check", Check]);

            Assert.Empty(f.Failures);
        }
    }

    [Fact]
    public void ARedMiddleCommitIsReported()
    {
        var (g, b) = History(middleGreen: false);
        using (g)
        {
            var f = EachCommit.Run(g.Repo, ["--since", b, "--check", Check]);

            Assert.Contains(f.Failures, m => m.Contains("red  middle", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000")]
    [InlineData("1234567890abcdef1234567890abcdef12345678")]
    public void AnAllZeroOrUnknownStartFallsBackToTheMergeBase(string since)
    {
        var (g, b) = History(middleGreen: false);
        using (g)
        {
            var f = EachCommit.Run(g.Repo, ["--since", since, "--base", "main-copy", "--check", Check]);

            Assert.Contains(f.Failures, m => m.Contains("red  middle", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AnUnknownStartReallyIsABadRevisionToGit()
    {
        // The mechanism P0-13 predicted: rev-list over an unknown `before` fails outright (git says
        // "Invalid revision range", not the "bad revision" the prediction guessed).
        var (g, _) = History(middleGreen: true);
        using (g)
        {
            var r = Proc.Run("git", g.Root, "rev-list", "1234567890abcdef1234567890abcdef12345678..HEAD");

            Assert.False(r.Ok);
            Assert.Contains("Invalid revision range", r.StdErr, StringComparison.Ordinal);
        }
    }
}
