using System;
using System.IO;
using System.Linq;

namespace Raft.Gates;

/// <summary>
/// Spec §12, "every commit green": GitHub Actions runs only a push's head, so this builds and runs
/// the fast checks of every other commit in the pushed range, each with that commit's own scripts,
/// in a worktree. `--since` is the push's `before` SHA. When that is all zeros (a new branch) or
/// not in the fetched history (after a force-push), the range falls back to the merge-base with
/// origin/main.
/// Vacuity risk: an empty range checks nothing — so a range that should hold commits but resolves
/// to none fails, and the command reports exactly which commits it checked.
/// </summary>
internal static class EachCommit
{
    /// <summary>Each commit's own scripts; commits from before CI existed have none and say so.</summary>
    public const string DefaultCheck =
        "if [ -x scripts/ci-build.sh ] && [ -x scripts/ci-test.sh ]; then scripts/ci-build.sh && scripts/ci-test.sh; " +
        "else echo 'no CI scripts at this commit (pre-CI history)'; fi";

    public static Findings Run(Repo repo, string[] args)
    {
        var rest = args.ToList();
        var since = Options.Take(rest, "--since") ?? "";
        var baseRef = Options.Take(rest, "--base") ?? "origin/main";
        var check = Options.Take(rest, "--check") ?? DefaultCheck;
        var f = new Findings();

        var head = repo.Git("rev-parse", "HEAD").StdOut.Trim();
        var start = ResolveStart(repo, since, baseRef, f);
        if (start is null)
        {
            return f;
        }

        if (start == head)
        {
            f.Note("range is empty: the push added no commits");
            return f;
        }

        var commits = repo.Git("rev-list", "--reverse", $"{start}..{head}").StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(c => c != head).ToList();
        var total = commits.Count + 1;
        f.Note($"range {start[..7]}..{head[..7]}: {total} commit(s); the head gets the full run, {commits.Count} checked here");
        if (commits.Count == 0)
        {
            return f;
        }

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
                var r = Proc.Run("bash", wt, "-c", check);
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
