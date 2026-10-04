using System;
using System.IO;
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

    [Fact]
    public void AnAllZeroStartIsANewBranchAndFallsBackToTheMergeBase()
    {
        var (g, _) = History(middleGreen: false);
        using (g)
        {
            var f = EachCommit.Run(g.Repo, ["--since", "0000000000000000000000000000000000000000", "--base", "main-copy", "--check", Check]);

            Assert.Contains(f.Failures, m => m.Contains("red  middle", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// P6-00: an unknown previous head that cannot be fetched fails, naming what it could not
    /// determine; widening to the merge-base answered a different question (run 37146478876:
    /// 143 commits back to phase 0, none of the rewritten ones checked). Sabotage S-range-1.
    /// </summary>
    [Fact]
    public void AnUnknownStartThatCannotBeFetchedFailsRatherThanWidening()
    {
        var (g, _) = History(middleGreen: true);
        using (g)
        {
            var f = EachCommit.Run(g.Repo, ["--since", "1234567890abcdef1234567890abcdef12345678", "--base", "main-copy", "--check", Check]);

            Assert.Contains(f.Failures, m => m.Contains("could not be fetched from origin by SHA", StringComparison.Ordinal));
            Assert.DoesNotContain(f.Notes, n => n.Contains(" ok ", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A force-push as CI sees it: a fresh clone of a remote whose history was rewritten, the old
    /// head on no ref and so not in the clone. Fetched by SHA, the range is exactly the rewritten
    /// commits; once the remote has collected the old head, the command fails. Sabotage S-range-2 (no fetch: the rewrite fails like an unknown start).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AForcePushesOldHeadIsFetchedBySha(bool oldHeadStillAtTheRemote)
    {
        using var origin = new GitFixture();
        origin.Write("Raft.slnx", "<Solution />").Write("ok", "").Commit("base");
        var kept = origin.Write("a", "1").Commit("kept");
        var oldHead = origin.Write("b", "1").Commit("old head");
        origin.Git("reset", "-q", "--hard", kept);
        origin.Git("reflog", "expire", "--expire=now", "--all");
        var rewritten = origin.Write("b", "2").Commit("rewritten");
        var newHead = origin.Write("c", "1").Commit("new head");
        if (!oldHeadStillAtTheRemote)
        {
            origin.Git("gc", "-q", "--prune=now");
        }

        var clone = Path.Combine(Path.GetTempPath(), "gates-clone-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(Proc.Run("git", Path.GetTempPath(), "clone", "-q", "file://" + origin.Root, clone).Ok);
            Assert.False(Proc.Run("git", clone, "cat-file", "-e", oldHead + "^{commit}").Ok);

            var f = EachCommit.Run(Repo.Locate(clone), ["--since", oldHead, "--base", "origin/main", "--check", Check]);

            if (oldHeadStillAtTheRemote)
            {
                Assert.Empty(f.Failures);
                Assert.Contains(f.Notes, n => n.StartsWith($"range {oldHead[..7]}..{newHead[..7]}: 2 commit(s)", StringComparison.Ordinal));
                Assert.Contains(f.Notes, n => n.StartsWith(rewritten[..7] + " ok", StringComparison.Ordinal));
            }
            else
            {
                Assert.Contains(f.Failures, m => m.Contains("could not be fetched from origin by SHA", StringComparison.Ordinal));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(clone, recursive: true);
            }
            catch (IOException)
            {
            }
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

    private static (GitFixture G, string Base) ReportPush(bool reportLast, string path = "docs/phases/P3/report.md")
    {
        var g = new GitFixture();
        g.Write("Raft.slnx", "<Solution />").Write("ok", "").Commit("base");
        var b = g.Git("rev-parse", "HEAD").Trim();
        if (reportLast)
        {
            g.Write("docs/phases/P4/breakdown.md", "x").Commit("breakdown");
            g.Write(path, "report").Commit("report");
        }
        else
        {
            g.Write(path, "report").Commit("report");
            g.Write("docs/phases/P4/breakdown.md", "x").Commit("breakdown");
        }

        return (g, b);
    }

    /// <summary>P4-10: the P3 acceptance push's shape, a report followed by another commit, fails locally.</summary>
    [Fact]
    public void AReportCommitThatIsNotThePushHeadFails()
    {
        var (g, b) = ReportPush(reportLast: false);
        using (g)
        {
            var report = g.Git("rev-parse", "HEAD~1").Trim();
            var f = EachCommit.Run(g.Repo, ["--since", b, "--check", Check]);

            Assert.Contains(f.Failures, m => m.StartsWith(report[..7] + " changes docs/phases/P3/report.md but is not the head of this push", StringComparison.Ordinal));
        }
    }

    /// <summary>The twin: the same push with the report last passes, and so does a non-head commit that changes other docs.</summary>
    [Fact]
    public void AReportAtThePushHeadAndOtherDocsBeforeItPass()
    {
        var (g, b) = ReportPush(reportLast: true);
        using (g)
        {
            Assert.Empty(EachCommit.Run(g.Repo, ["--since", b, "--check", Check]).Failures);
        }

        var (h, c) = ReportPush(reportLast: false, path: "docs/phases/P3/notes.md");
        using (h)
        {
            Assert.Empty(EachCommit.Run(h.Repo, ["--since", c, "--check", Check]).Failures);
        }
    }
}
