using System;
using System.Collections.Generic;
using System.Linq;

namespace Raft.Gates;

/// <summary>
/// Spec §12, "predict before implementing", made mechanical at the one unavoidable moment — the
/// commit. Every commit reachable from HEAD that changes anything outside the documentation
/// allowlist carries a `Task:` trailer naming a real task; and the first commit carrying a task's
/// trailer is a strict descendant of the commit that introduced that task's current Prediction
/// line. Revising a prediction after its implementation began therefore fails, by design.
/// Vacuity risks: a shallow clone shows one commit (guarded: shallow fails); zero commits need a
/// trailer (guarded: at least one must); a root commit shows no changed paths without --root.
/// </summary>
internal static class Trailers
{
    public static Findings Run(Repo repo, string[] args)
    {
        var f = new Findings();

        var shallow = repo.Git("rev-parse", "--is-shallow-repository");
        if (!shallow.Ok || shallow.StdOut.Trim() != "false")
        {
            f.Fail($"repository is shallow or git failed ({shallow.StdOut.Trim()}{shallow.StdErr.Trim()}); history gates need full history");
            return f;
        }

        var tasks = Breakdown.LoadAll(repo, new Findings()).ToDictionary(t => t.Id, StringComparer.Ordinal);
        var commits = Commits(repo);
        f.Require(commits.Count > 0, "no commits reachable from HEAD");

        var needing = 0;
        var firstUse = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in commits)
        {
            var codePaths = c.Paths.Where(p => !IsDocumentation(p)).ToList();
            if (c.IsMerge || codePaths.Count == 0)
            {
                continue;
            }

            needing++;
            if (c.Tasks.Count == 0)
            {
                f.Fail($"{c.Short} '{c.Subject}': changes {string.Join(", ", codePaths.Take(3))} but has no Task: trailer");
            }

            foreach (var task in c.Tasks)
            {
                if (!tasks.ContainsKey(task))
                {
                    f.Fail($"{c.Short}: Task: {task} names no task in any breakdown");
                }
            }
        }

        foreach (var c in commits.Where(c => !c.IsMerge))
        {
            foreach (var task in c.Tasks)
            {
                firstUse.TryAdd(task, c.Sha);
            }
        }

        f.Require(needing > 0, "no commit needed a trailer — the gate checked nothing");

        foreach (var (task, first) in firstUse.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!tasks.TryGetValue(task, out var t) || !t.Fields.TryGetValue("Prediction", out var prediction))
            {
                continue;
            }

            var line = $"- **Prediction:** {prediction}";
            var intro = repo.Git("log", "--format=%H", "--reverse", "-S", line, "--", t.File);
            var introSha = intro.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (introSha is null)
            {
                f.Fail($"{task}: its Prediction line is not in any commit — commit the prediction first");
                continue;
            }

            var before = introSha != first && repo.Git("merge-base", "--is-ancestor", introSha, first).Ok;
            f.Require(before, $"{task}: first implementing commit {first[..7]} is not after the commit that introduced its prediction ({introSha[..7]})");
        }

        f.Note($"{commits.Count} commits, {needing} needed a trailer, {firstUse.Count} tasks in use");
        return f;
    }

    internal static bool IsDocumentation(string path) =>
        path.StartsWith("docs/", StringComparison.Ordinal) || (!path.Contains('/', StringComparison.Ordinal) && path.EndsWith(".md", StringComparison.Ordinal));

    internal sealed record Commit(string Sha, string Subject, bool IsMerge, IReadOnlyList<string> Tasks, IReadOnlyList<string> Paths)
    {
        public string Short => Sha[..7];
    }

    /// <summary>All commits reachable from HEAD, oldest first.</summary>
    internal static List<Commit> Commits(Repo repo)
    {
        const char Rs = '\u001e';
        const char Us = '\u001f';
        var log = repo.Git("log", "--reverse", "--topo-order", $"--format=%H{Us}%P{Us}%s{Us}%(trailers:key=Task,valueonly,separator=%x2C){Rs}");
        if (!log.Ok)
        {
            throw new InvalidOperationException($"git log failed: {log}");
        }

        var commits = new List<Commit>();
        foreach (var record in log.StdOut.Split(Rs, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = record.Trim('\n').Split(Us);
            if (parts.Length != 4)
            {
                continue;
            }

            var sha = parts[0];
            var parents = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tasks = parts[3].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var paths = repo.Git("diff-tree", "--no-commit-id", "--name-only", "-r", "--root", sha)
                .StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            commits.Add(new Commit(sha, parts[2], parents.Length > 1, tasks, paths));
        }

        return commits;
    }
}
