using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Raft.Gates;

/// <summary>
/// The required `build` check since P2-01: the build job's work is split into `build-core`
/// (preflight, build, gates, reports, tests) and one `sabotage i/n` job per harness shard; this
/// collect job requires all of them, and since P3-08 the `soak` job too. It keeps the required-check
/// name unchanged.
/// Vacuity risk: a shard job that never ran (a plan with fewer shards than the manifest needs, a
/// matrix that expanded to nothing) passes if only the jobs present are checked. Guarded: the
/// shard list is recomputed here from the committed manifest, and each expected shard needs
/// exactly one successful job; a job for a shard not in the plan fails too.
/// </summary>
internal static class BuildCollect
{
    /// <summary>The soak jobs the build requires, each exactly once and green.</summary>
    internal static readonly string[] Soaks = ["soak", "soak-membership"];

    /// <summary>Every single job the build requires exactly once and green: the soaks, and phase 9's Compose run (P9-08).</summary>
    internal static readonly string[] Required = [.. Soaks, "compose"];

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

        // The soaks (P3-08; the membership soak, its own soak, P6-12): 10,000 executions each, the only
        // place the invariants run at scale. Required, never waived (spec §12): skipping one is a change
        // to this gate, argued first. The Compose run (P9-08) is required the same way: spec §11 phase
        // 9's done criterion, three processes, a killed leader and the checker green, runs only there.
        foreach (var name in Required)
        {
            var soak = doc.RootElement.GetProperty("jobs").EnumerateArray().Where(j => j.GetProperty("name").GetString() == name).ToList();
            if (soak.Count != 1)
            {
                f.Fail($"{name}: {soak.Count} jobs, expected exactly one (required, spec §12)");
            }
            else if (EachCommitMatrix.Conclusion(soak[0]) is var c && c != "success")
            {
                f.Fail($"{name}: concluded {c ?? "none"}");
            }
            else
            {
                f.Note($"{name}: passed");
            }
        }
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

        // P10-00: the plan's costs, refused when an entry has none or a cost names no entry, and
        // each shard's modelled time, so a plan near the ceiling is visible before a shard runs.
        var specs = SabotageSpec.LoadAll(repo, f).Where(s => !s.RunsOnHost).ToList();
        var costs = ShardCosts.Parse(File.ReadAllText(repo.PathOf(ShardPlan.CostFile)));
        foreach (var problem in ShardPlan.CostProblems(specs.Select(s => s.Id), costs))
        {
            f.Fail(problem);
        }

        if (f.Failures.Count == 0)
        {
            var entries = specs.Select(s => new PlanEntry(s.Id, Sabotage.UnitKey(s))).ToList();
            var byId = entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
            var times = ShardPlan.Balance(entries, shards.Count, costs).Select(s => ShardPlan.ModelledTime([.. s.Select(id => byId[id])], costs)).ToList();
            f.Note($"modelled shard times (local seconds): {string.Join(", ", times.Select(t => t.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)))}; slowest {times.Max():F0}");
        }

        return f;
    }
}
