using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Raft.Gates;

/// <summary>
/// Spec §12, "every commit green", in CI (P1-12): a list job names the push's non-head commits, a
/// matrix runs one job per commit (`commit SHA`, each with that commit's own preflight, build,
/// gates, tests and sabotage harness, via scripts/ci-commit.sh), and a collect job — named
/// `each-commit`, the required check — decides.
/// Vacuity risk: a matrix over an empty list runs zero jobs and reports green, the zero-job CI
/// again. Guarded: the collect job recomputes the range itself and requires the list to equal it,
/// and requires exactly one job per listed commit, and no job for a commit not listed.
/// A commit from before the gates existed is reported as pre-gate, never as passed.
/// </summary>
internal static class EachCommitMatrix
{
    public const string ChecksStep = "checks";
    public const string ClassifyStep = "classify";

    /// <summary>`each-commit-list`: the non-head commits as a JSON array, to $GITHUB_OUTPUT when set.</summary>
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

        var json = JsonSerializer.Serialize(commits);
        f.Note($"commits={json}");
        var output = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        if (!string.IsNullOrEmpty(output))
        {
            File.AppendAllText(output, $"commits={json}\ncount={commits.Count}\n");
        }

        return f;
    }

    /// <summary>`each-commit-collect`: one result per listed commit, and the list equal to the range.</summary>
    public static Findings Collect(Repo repo, string[] args)
    {
        var rest = args.ToList();
        var since = Options.Take(rest, "--since") ?? "";
        var baseRef = Options.Take(rest, "--base") ?? "origin/main";
        var listed = Options.Take(rest, "--commits") ?? Environment.GetEnvironmentVariable("EACH_COMMIT_LIST") ?? "";
        var listResult = Options.Take(rest, "--list-result") ?? Environment.GetEnvironmentVariable("EACH_COMMIT_LIST_RESULT") ?? "";
        var f = new Findings();
        f.Require(listResult == "success", $"the list job concluded '{listResult}'");
        var expected = EachCommit.NonHeadCommits(repo, since, baseRef, f);
        if (expected is null)
        {
            return f;
        }

        string JobsJson()
        {
            var run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? throw new InvalidOperationException("GITHUB_RUN_ID is required");
            var attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "1";
            return Api.FromEnvironment(rest).Get($"actions/runs/{run}/attempts/{attempt}/jobs?per_page=100");
        }

        Evaluate(expected, listed, expected.Count == 0 ? """{"jobs":[]}""" : JobsJson(), f);
        return f;
    }

    /// <summary>The decision, separated from git and HTTP so it can be tested on recorded responses.</summary>
    internal static void Evaluate(IReadOnlyList<string> expected, string listedJson, string jobsJson, Findings f)
    {
        List<string> listed;
        try
        {
            listed = JsonSerializer.Deserialize<List<string>>(listedJson) ?? [];
        }
        catch (JsonException e)
        {
            f.Fail($"the list job's output is not a JSON array of commits: '{listedJson}' ({e.Message})");
            return;
        }

        if (!listed.SequenceEqual(expected, StringComparer.Ordinal))
        {
            f.Fail($"the list job named {listed.Count} commit(s) but the range holds {expected.Count}: " +
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
        foreach (var extra in jobs.Select(j => j.Name[7..]).Where(s => !expected.Contains(s, StringComparer.Ordinal)).Distinct())
        {
            f.Fail($"a job ran for {Short(extra)}, which is not in the range");
        }

        int passed = 0, preGate = 0;
        foreach (var sha in expected)
        {
            var mine = jobs.Where(j => j.Name == "commit " + sha).Select(j => j.Job).ToList();
            if (mine.Count != 1)
            {
                f.Fail($"{Short(sha)}: {mine.Count} jobs, expected exactly one");
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
                f.Fail($"{Short(sha)}: job concluded {conclusion ?? "none"}, classify {(classified ? "ok" : "missing or failed")}, checks {checksConclusion ?? "missing"}");
            }
            else if (checksConclusion == "success")
            {
                passed++;
                f.Note($"{Short(sha)}: passed (its own preflight, build, gates, tests, harness)");
            }
            else if (checksConclusion == "skipped")
            {
                preGate++;
                f.Note($"{Short(sha)}: pre-gate — from before the gate scripts existed; not checked, not passed");
            }
            else
            {
                f.Fail($"{Short(sha)}: checks step {checksConclusion ?? "missing"}");
            }
        }

        f.Note($"{expected.Count} commit(s): {passed} passed, {preGate} pre-gate");
    }

    private static string? Conclusion(JsonElement e) =>
        e.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

    private static string Short(string sha) => sha[..Math.Min(7, sha.Length)];
}
