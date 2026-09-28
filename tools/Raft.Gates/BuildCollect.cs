using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Raft.Gates;

/// <summary>
/// The required `build` check since P2-01: the build job's work is split into `build-core`
/// (preflight, build, gates, reports, tests) and one `sabotage i/n` job per harness shard; this
/// collect job requires all of them. It keeps the required-check name unchanged.
/// Vacuity risk: a shard job that never ran (a plan with fewer shards than the manifest needs, a
/// matrix that expanded to nothing) passes if only the jobs present are checked. Guarded: the
/// shard list is recomputed here from the committed manifest, and each expected shard needs
/// exactly one successful job; a job for a shard not in the plan fails too.
/// </summary>
internal static class BuildCollect
{
    public static Findings Run(Repo repo, string[] args)
    {
        var rest = args.ToList();
        var coreResult = Options.Take(rest, "--core-result") ?? Environment.GetEnvironmentVariable("BUILD_CORE_RESULT") ?? "";
        var f = new Findings();
        var shards = EachCommitMatrix.HeadShards(repo, f);
        if (f.Failures.Count > 0)
        {
            return f;
        }

        Evaluate(coreResult, shards, EachCommitMatrix.JobsOfThisRun(rest), f);
        return f;
    }

    internal static void Evaluate(string coreResult, IReadOnlyList<string> shards, string jobsJson, Findings f)
    {
        f.Require(coreResult == "success", $"build-core concluded '{coreResult}'");
        using var doc = JsonDocument.Parse(jobsJson);
        var jobs = doc.RootElement.GetProperty("jobs").EnumerateArray()
            .Select(j => (Name: j.GetProperty("name").GetString() ?? "", Job: j))
            .Where(j => j.Name.StartsWith("sabotage ", StringComparison.Ordinal))
            .ToList();
        var wanted = shards.Select(s => "sabotage " + s).ToHashSet(StringComparer.Ordinal);
        foreach (var extra in jobs.Select(j => j.Name).Where(n => !wanted.Contains(n)).Distinct(StringComparer.Ordinal))
        {
            f.Fail($"a harness job ran that the plan does not contain: '{extra}'");
        }

        foreach (var shard in shards)
        {
            var mine = jobs.Where(j => j.Name == "sabotage " + shard).Select(j => j.Job).ToList();
            if (mine.Count != 1)
            {
                f.Fail($"sabotage {shard}: {mine.Count} jobs, expected exactly one");
                continue;
            }

            var conclusion = EachCommitMatrix.Conclusion(mine[0]);
            if (conclusion == "success")
            {
                f.Note($"sabotage {shard}: passed");
            }
            else
            {
                f.Fail($"sabotage {shard}: concluded {conclusion ?? "none"}");
            }
        }

        f.Note($"{shards.Count} harness shard(s) required");
    }
}

/// <summary>`sabotage-plan`: the head's shard count, for scripts/ci-sabotage.sh to run every shard locally.</summary>
internal static class SabotagePlan
{
    public static Findings Run(Repo repo, string[] args)
    {
        var f = new Findings();
        var shards = EachCommitMatrix.HeadShards(repo, f);
        f.Note($"count={shards.Count}");
        return f;
    }
}
