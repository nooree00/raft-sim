using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Raft.Gates;

/// <summary>
/// Spec §12, "every commit green", run locally: GitHub Actions runs only a push's head, so this runs
/// the checks of every other commit in the pushed range, each with that commit's own scripts, in a
/// worktree, one after another. CI runs the same checks as a matrix (EachCommitMatrix). `--since` is the push's `before` SHA. When that is all zeros (a new branch) or
/// not in the fetched history (after a force-push), the range falls back to the merge-base with
/// origin/main.
/// Vacuity risk: an empty range checks nothing — so a range that should hold commits but resolves
/// to none fails, and the command reports exactly which commits it checked.
/// </summary>
internal static class EachCommit
{
    /// <summary>
    /// Each commit's own preflight, build, gates, tests and harness, sequenced by the head's
    /// scripts/ci-commit.sh — the same sequence as CI's per-commit matrix jobs (P1-12). Commits
    /// from before the gate scripts are reported by it as pre-gate.
    /// </summary>
    public const string DefaultCheck = "\"$RAFT_HEAD_ROOT/scripts/ci-commit.sh\" checks";

    public static Findings Run(Repo repo, string[] args)
    {
        var rest = args.ToList();
        var since = Options.Take(rest, "--since") ?? "";
        var baseRef = Options.Take(rest, "--base") ?? "origin/main";
        var check = Options.Take(rest, "--check") ?? DefaultCheck;
        var f = new Findings();

        var commits = NonHeadCommits(repo, since, baseRef, f);
        if (commits is null || commits.Count == 0)
        {
            return f;
        }

        ReportsOnlyAtTheHead(repo, commits, f);

        // A unique path: the tests of each checked commit exercise this command too, and a fixed
        // path collided with the outer run's own worktree (seen in CI).
        var wt = Path.Combine(Path.GetTempPath(), "raft-each-commit-" + Guid.NewGuid().ToString("N"));
        repo.Git("worktree", "prune");
        var add = repo.Git("worktree", "add", "--detach", wt, commits[0]);
        if (!add.Ok)
        {
            f.Fail($"worktree add failed: {add}");
            return f;
        }

        try
        {
            foreach (var c in commits)
            {
                Proc.Run("git", wt, "checkout", "-q", "--detach", c);
                Proc.Run("git", wt, "clean", "-fdq");
                var subject = Proc.Run("git", wt, "log", "-1", "--format=%s").StdOut.Trim();
                var r = Proc.RunWithEnv("bash", wt, new Dictionary<string, string> { ["RAFT_HEAD_ROOT"] = repo.Root }, "-c", check);
                if (r.Ok)
                {
                    f.Note($"{c[..7]} ok   {subject}");
                }
                else
                {
                    var tail = string.Join('\n', (r.StdOut + r.StdErr).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(8));
                    f.Fail($"{c[..7]} red  {subject}\n{tail}");
                }
            }
        }
        finally
        {
            repo.Git("worktree", "remove", "--force", wt);
        }

        return f;
    }

    /// <summary>The range's commits other than HEAD, oldest first; null (with a failure) if the range cannot be resolved.</summary>
    internal static List<string>? NonHeadCommits(Repo repo, string since, string baseRef, Findings f)
    {
        var head = repo.Git("rev-parse", "HEAD").StdOut.Trim();
        var start = ResolveStart(repo, since, baseRef, f);
        if (start is null)
        {
            return null;
        }

        if (start == head)
        {
            f.Note("range is empty: the push added no commits");
            return [];
        }

        var commits = repo.Git("rev-list", "--reverse", $"{start}..{head}").StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(c => c != head).ToList();
        f.Note($"range {start[..7]}..{head[..7]}: {commits.Count + 1} commit(s); the head gets the full run, {commits.Count} checked here");
        return commits;
    }

    /// <summary>
    /// A phase report certifies the commit that contains it (spec §12), and GitHub runs CI only for a
    /// push's head. A non-head commit that changes a report therefore makes a report no run can ever
    /// certify; `gates reports`, the one gate the local sequence skips, would be the only other place
    /// it shows (P4-10, from the red P3 acceptance run).
    /// </summary>
    internal static void ReportsOnlyAtTheHead(Repo repo, IEnumerable<string> nonHead, Findings f)
    {
        foreach (var c in nonHead)
        {
            var changed = repo.Git("diff-tree", "--no-commit-id", "--name-only", "-r", c).StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in changed.Where(IsReport))
            {
                f.Fail($"{c[..7]} changes {path} but is not the head of this push: a report certifies the commit that contains it and CI runs only a push's head, so this report could never be certified. Push the report's commit last.");
            }
        }
    }

    internal static bool IsReport(string path)
    {
        var parts = path.Split('/');
        return parts.Length == 4 && parts[0] == "docs" && parts[1] == "phases" && parts[2].Length > 1 && parts[2][0] == 'P'
            && parts[2][1..].All(char.IsAsciiDigit) && parts[3] == "report.md";
    }

    internal static string? ResolveStart(Repo repo, string since, string baseRef, Findings f)
    {
        var known = since.Length > 0 && since.Trim('0').Length > 0 && repo.Git("cat-file", "-e", since + "^{commit}").Ok;
        if (known)
        {
            return repo.Git("rev-parse", since).StdOut.Trim();
        }

        var mb = repo.Git("merge-base", "HEAD", baseRef);
        if (!mb.Ok)
        {
            f.Fail($"'{since}' is not a usable start and there is no merge-base with {baseRef}: {mb.StdErr.Trim()}");
            return null;
        }

        f.Note($"'{(since.Length == 0 ? "(none)" : since[..Math.Min(7, since.Length)])}' is not in the history; using the merge-base with {baseRef}");
        return mb.StdOut.Trim();
    }
}
