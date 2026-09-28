using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Raft.Gates;

/// <summary>
/// Spec §12, "every commit green", in CI (P1-12, sharded in P2-01): a plan job names the push's
/// non-head commits and, for each, its harness shards; a matrix runs one job per (commit, shard),
/// named `commit SHA i/n`, each with that commit's own scripts via scripts/ci-commit.sh (shard 1
/// also runs the commit's preflight, gates and tests); and a collect job — named `each-commit`,
/// the required check — decides. A commit from before sharding has one job, `1/1`, running its
/// own unsharded harness.
/// Vacuity risk: a matrix over an empty list runs zero jobs and reports green, the zero-job CI
/// again. Guarded: the collect job recomputes the range and every commit's plan itself, requires
/// the list to equal the range, exactly one job per (commit, shard), and no job it did not expect.
/// A commit from before the gates existed is reported as pre-gate, never as passed.
/// </summary>
internal static class EachCommitMatrix
{
    public const string ChecksStep = "checks";
    public const string ClassifyStep = "classify";

    /// <summary>The jobs a commit gets: one per harness shard at that commit, or `1/1` if it predates sharding.</summary>
    public static IReadOnlyList<string> ShardsOf(Repo repo, string sha) =>
        ShardPlan.AtCommit(repo, sha) is { } plan
            ? Enumerable.Range(1, plan.Of).Select(i => $"{i}/{plan.Of}").ToList()
            : ["1/1"];

    /// <summary>
    /// `each-commit-list`: the non-head commits, and the (commit, shard) matrix, to $GITHUB_OUTPUT
    /// when set; also the head's own harness shards (`shards`), for the build's sabotage jobs.
    /// </summary>
    public static Findings List(Repo repo, string[] args)
    {
        var rest = args.ToList();
        var since = Options.Take(rest, "--since") ?? "";
        var baseRef = Options.Take(rest, "--base") ?? "origin/main";
        var f = new Findings();
        var commits = EachCommit.NonHeadCommits(repo, since, baseRef, f);
        if (commits is null)
        {
            return f;
        }

        var items = commits.SelectMany(c => ShardsOf(repo, c).Select(s => new { commit = c, shard = s })).ToList();
        var json = JsonSerializer.Serialize(commits);
        var matrix = JsonSerializer.Serialize(items);
        var head = HeadShards(repo, f);
        var shards = JsonSerializer.Serialize(head);
        f.Note($"commits={json}");
        f.Note($"matrix={matrix}");
        f.Note($"shards={shards}");
        var output = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        if (!string.IsNullOrEmpty(output))
        {
            File.AppendAllText(output, $"commits={json}\ncount={commits.Count}\nmatrix={matrix}\nshards={shards}\n");
        }

        return f;
    }

    /// <summary>The head's harness shards, from the working tree's manifest (as the harness itself reads it).</summary>
    public static IReadOnlyList<string> HeadShards(Repo repo, Findings f)
    {
        var ids = SabotageSpec.LoadAll(repo, f).Where(s => !s.RunsOnHost).Select(s => s.Id).ToList();
        var n = ShardPlan.Count(ids.Count, ShardPlan.ParseSize(File.ReadAllText(repo.PathOf(ShardPlan.SizeFile))));
        return Enumerable.Range(1, n).Select(i => $"{i}/{n}").ToList();
    }

    /// <summary>`each-commit-collect`: one result per (commit, shard), and the list equal to the range.</summary>
    public static Findings Collect(Repo repo, string[] args)
    {
        var rest = args.ToList();
        var since = Options.Take(rest, "--since") ?? "";
        var baseRef = Options.Take(rest, "--base") ?? "origin/main";
        var listed = Options.Take(rest, "--commits") ?? Environment.GetEnvironmentVariable("EACH_COMMIT_LIST") ?? "";
        var listResult = Options.Take(rest, "--list-result") ?? Environment.GetEnvironmentVariable("EACH_COMMIT_LIST_RESULT") ?? "";
        var f = new Findings();
        f.Require(listResult == "success", $"the plan job concluded '{listResult}'");
        var expected = EachCommit.NonHeadCommits(repo, since, baseRef, f);
        if (expected is null)
        {
            return f;
        }

        var items = expected.SelectMany(c => ShardsOf(repo, c).Select(s => (c, s))).ToList();
        Evaluate(expected, listed, items, expected.Count == 0 ? """{"jobs":[]}""" : JobsOfThisRun(rest), f);
        return f;
    }

    /// <summary>Every job of this run's current attempt, across pages, as one {"jobs": [...]} document.</summary>
    public static string JobsOfThisRun(List<string> rest)
    {
        var run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? throw new InvalidOperationException("GITHUB_RUN_ID is required");
        var attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "1";
        var api = Api.FromEnvironment(rest);
        var all = new JsonArray();
        for (var page = 1; ; page++)
        {
            var doc = JsonNode.Parse(api.Get($"actions/runs/{run}/attempts/{attempt}/jobs?per_page=100&page={page}"))!;
            var jobs = doc["jobs"]!.AsArray();
            foreach (var j in jobs.ToList())
            {
                jobs.Remove(j);
                all.Add(j);
            }

            if (all.Count >= (int)doc["total_count"]! || page >= 20)
            {
                break;
            }
        }

        return new JsonObject { ["jobs"] = all }.ToJsonString();
    }

    /// <summary>The decision, separated from git and HTTP so it can be tested on recorded responses.</summary>
    internal static void Evaluate(IReadOnlyList<string> expected, string listedJson, IReadOnlyList<(string Commit, string Shard)> items, string jobsJson, Findings f)
    {
        List<string> listed;
        try
        {
            listed = JsonSerializer.Deserialize<List<string>>(listedJson) ?? [];
        }
        catch (JsonException e)
        {
            f.Fail($"the plan job's output is not a JSON array of commits: '{listedJson}' ({e.Message})");
            return;
        }

        if (!listed.SequenceEqual(expected, StringComparer.Ordinal))
        {
            f.Fail($"the plan job named {listed.Count} commit(s) but the range holds {expected.Count}: " +
                   $"listed [{string.Join(", ", listed.Select(Short))}], range [{string.Join(", ", expected.Select(Short))}]");
            return;
        }

        if (expected.Count == 0)
        {
            f.Note("no non-head commits in this push; the head gets the full run in `build`");
            return;
        }

        using var doc = JsonDocument.Parse(jobsJson);
        var jobs = doc.RootElement.GetProperty("jobs").EnumerateArray()
            .Select(j => (Name: j.GetProperty("name").GetString() ?? "", Job: j))
            .Where(j => j.Name.StartsWith("commit ", StringComparison.Ordinal))
            .ToList();
        var wanted = items.Select(i => JobName(i.Commit, i.Shard)).ToHashSet(StringComparer.Ordinal);
        foreach (var extra in jobs.Select(j => j.Name).Where(n => !wanted.Contains(n)).Distinct(StringComparer.Ordinal))
        {
            f.Fail($"a job ran that the plan does not contain: '{extra}'");
        }

        int passed = 0, preGate = 0;
        foreach (var (sha, shard) in items)
        {
            var name = JobName(sha, shard);
            var mine = jobs.Where(j => j.Name == name).Select(j => j.Job).ToList();
            if (mine.Count != 1)
            {
                f.Fail($"{Short(sha)} {shard}: {mine.Count} jobs, expected exactly one");
                continue;
            }

            var job = mine[0];
            var conclusion = Conclusion(job);
            var steps = job.TryGetProperty("steps", out var s) ? s.EnumerateArray().ToList() : [];
            var classify = steps.FirstOrDefault(x => x.GetProperty("name").GetString() == ClassifyStep);
            var checks = steps.FirstOrDefault(x => x.GetProperty("name").GetString() == ChecksStep);
            var checksConclusion = checks.ValueKind == JsonValueKind.Object ? Conclusion(checks) : null;
            var classified = classify.ValueKind == JsonValueKind.Object && Conclusion(classify) == "success";
            if (conclusion != "success" || !classified)
            {
                f.Fail($"{Short(sha)} {shard}: job concluded {conclusion ?? "none"}, classify {(classified ? "ok" : "missing or failed")}, checks {checksConclusion ?? "missing"}");
            }
            else if (checksConclusion == "success")
            {
                passed++;
                f.Note($"{Short(sha)} {shard}: passed (its own scripts{(shard.StartsWith("1/", StringComparison.Ordinal) ? ": preflight, build, gates, tests," : ": build,")} harness share)");
            }
            else if (checksConclusion == "skipped")
            {
                preGate++;
                f.Note($"{Short(sha)} {shard}: pre-gate — from before the gate scripts existed; not checked, not passed");
            }
            else
            {
                f.Fail($"{Short(sha)} {shard}: checks step {checksConclusion ?? "missing"}");
            }
        }

        f.Note($"{expected.Count} commit(s), {items.Count} job(s): {passed} passed, {preGate} pre-gate");
    }

    public static string JobName(string sha, string shard) => $"commit {sha} {shard}";

    internal static string? Conclusion(JsonElement e) =>
        e.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

    private static string Short(string sha) => sha[..Math.Min(7, sha.Length)];
}
