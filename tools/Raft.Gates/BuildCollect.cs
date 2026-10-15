using System;
using System.Collections.Generic;
using System.Globalization;
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

        var jobs = EachCommitMatrix.JobsOfThisRun(rest);
        Evaluate(coreResult, shards, jobs, f);
        if (f.Failures.Count == 0)
        {
            var api = Api.FromEnvironment(rest);
            CheckCosts(shards, jobs, id => api.Get($"check-runs/{id}/annotations?per_page=100"), ShardCosts.Parse(repo.ReadText(ShardPlan.CostFile)), f);
            var targets = SabotageSpec.LoadLive(repo, f).Where(e => e.Kind == "test").Select(e => e.Get("target")!).ToList();
            CheckReach(shards, jobs, id => api.Get($"check-runs/{id}/annotations?per_page=100"), SabotageSpec.ParseBroadTargets(repo.ReadText(SabotageSpec.ClassReachFile)), targets, f);
        }

        return f;
    }

    /// <summary>The run's total over the file's sum: beyond this factor either way, the cost file is stale (P13-01).</summary>
    public const double CostFactor = 2;

    /// <summary>
    /// P13-01, the observer <see cref="Currency.GitHubRun"/>: every head shard's entry-time notice,
    /// read through the API, and the run's total compared with the cost file's sum over the same
    /// entries. One line stays at the harness's factor of 5 (held out, an entry's median over three
    /// runs reached 2.07 times its line, and over 20 jobs of one run 2.45); the total does not
    /// average that away by luck: four runs came to 0.83 to 1.02 of their sums, so a factor of 2
    /// keeps 1.65 times the furthest (the margin audit's rule asks 1.5). Vacuity risk: a total over
    /// the shards that reported compares less work than the run did; guarded by requiring every
    /// head shard's notice. The refreshed lines go to the log, so a refresh is copying.
    /// </summary>
    internal static void CheckCosts(IReadOnlyList<string> shards, string jobsJson, Func<long, string> annotations, ShardCosts costs, Findings f)
    {
        using var doc = JsonDocument.Parse(jobsJson);
        var times = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var shard in shards)
        {
            var job = doc.RootElement.GetProperty("jobs").EnumerateArray().Single(j => j.GetProperty("name").GetString() == "sabotage " + shard);
            var notice = Notice(annotations(job.GetProperty("id").GetInt64()));
            if (notice is null)
            {
                f.Fail($"sabotage {shard}: no 'sabotage entry times' notice, so the run's total cannot be compared with {ShardPlan.CostFile}");
                continue;
            }

            foreach (var (id, seconds) in notice)
            {
                times[id] = seconds;
            }
        }

        if (f.Failures.Count > 0)
        {
            return;
        }

        var observed = times.Values.Sum();
        var claimed = times.Keys.Sum(costs.Entry);
        Currency.Factor(Currency.GitHubRun, [($"the run's {times.Count} entries", claimed, observed)], CostFactor, costs.StaleFloor, f,
            (_, _) => $"Refresh the file's entry lines from this run's notice below (and its predecessors'), as P12-08 did");
        Console.WriteLine("::notice::sabotage entry times, the whole run%0A" + string.Join("%0A", times.Select(t => FormattableString.Invariant($"{t.Key} {t.Value:F1}"))));
    }

    /// <summary>A test's class's other entries, at least this many, for its reach to be judged (P14-05, P15-06).</summary>
    public const int ReachMinimum = 4;

    /// <summary>
    /// P15-06, the observer <see cref="Currency.ClassReach"/> (P14-05's broad-run, renamed for what it can
    /// see): every head shard's notice of the tests red beside each entry's target, summed over the run.
    /// Since P11-09 an entry runs its target's whole class, so a test is exposed only to its own class's
    /// patches, and the entries that ran it are the run's test entries targeting another method of its
    /// class (<paramref name="targets"/>, the live manifest's). A test red beside at least half of them,
    /// and at least <see cref="ReachMinimum"/>, reaches its class: it is specific to none of its class's
    /// entries, so it must be in the class-reach list, and an entry targeting it needs a reason or a
    /// control, as a broad target's does (P3-01). Breadth across classes cannot be seen here at any
    /// threshold; ci/broad-targets.txt holds it as a judgement (decision 5). A shard reports only its
    /// reds, because a notice listing every test that ran passes GitHub's cap of 4,096 characters, and a
    /// list of reds alone would lose the runs of a test red in no entry of a shard. Vacuity risk: an
    /// observation taken from the list compares the list with itself (S-reach-1); a total over the shards
    /// (or parts) that reported counts less than the run did, guarded by requiring every part of every
    /// head shard's notice.
    /// </summary>
    internal static void CheckReach(IReadOnlyList<string> shards, string jobsJson, Func<long, string> annotations, IReadOnlyList<string> reach, IReadOnlyList<string> targets, Findings f)
    {
        using var doc = JsonDocument.Parse(jobsJson);
        var red = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var shard in shards)
        {
            var job = doc.RootElement.GetProperty("jobs").EnumerateArray().Single(j => j.GetProperty("name").GetString() == "sabotage " + shard);
            var (notice, problem) = NeighbourNotice(annotations(job.GetProperty("id").GetInt64()));
            if (notice is null)
            {
                f.Fail($"sabotage {shard}: {problem ?? $"no '{Sabotage.NeighbourTitle}' notice"}, so the run's red neighbours cannot be compared with {SabotageSpec.ClassReachFile}");
                continue;
            }

            foreach (var (test, n) in notice)
            {
                red[test] = red.GetValueOrDefault(test) + n;
            }
        }

        if (f.Failures.Count > 0)
        {
            return;
        }

        var found = red.Select(r => (Test: r.Key, Red: r.Value, Ran: RanBeside(r.Key, targets)))
            .Where(t => t.Ran >= ReachMinimum && 2 * t.Red >= t.Ran)
            .Select(t => (t.Test, $"red beside {t.Red} of its class's {t.Ran} other entries"));
        Currency.Listed(Currency.ClassReach, found, test => reach.Any(b => test.StartsWith(b, StringComparison.Ordinal)),
            $"every test red beside at least half of its class's other entries, and at least {ReachMinimum}", f, test => $"Add the line: {test}");
    }

    /// <summary>The run's test entries that ran a test beside their target: those targeting another method of its class (P11-09).</summary>
    internal static int RanBeside(string test, IReadOnlyList<string> targets)
    {
        var cls = test[..test.LastIndexOf('.')];
        return targets.Count(t => Sabotage.TargetClass(t) == cls && Sabotage.TargetMethod(t) != test);
    }

    /// <summary>
    /// A shard's red neighbours from its annotations, every part of its notice summed (<see cref="Sabotage.NeighbourNotices"/>);
    /// null with no problem when it wrote none; null with the problem when a part is missing (a step
    /// keeps ten notices and drops the rest silently), a part is at the cap (cut), or a line is unreadable.
    /// </summary>
    internal static (IReadOnlyDictionary<string, int>? Red, string? Problem) NeighbourNotice(string annotationsJson)
    {
        using var doc = JsonDocument.Parse(annotationsJson);
        var parts = new SortedDictionary<int, string>();
        var counts = new SortedSet<int>();
        foreach (var a in doc.RootElement.EnumerateArray())
        {
            var message = a.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (!message.StartsWith(Sabotage.NeighbourTitle + " ", StringComparison.Ordinal))
            {
                continue;
            }

            var first = message.Split('\n')[0];
            var number = first[(Sabotage.NeighbourTitle.Length + 1)..].Split('/');
            if (number.Length != 2 || !int.TryParse(number[0], NumberStyles.None, CultureInfo.InvariantCulture, out var k) || !int.TryParse(number[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return (null, $"a red-neighbours notice without its part number ('{first}')");
            }

            if (message.Length >= Sabotage.AnnotationCap)
            {
                return (null, $"red-neighbours part {k}/{n} is {message.Length} characters, GitHub's cap, so it was cut");
            }

            parts[k] = message;
            counts.Add(n);
        }

        if (parts.Count == 0)
        {
            return (null, null);
        }

        if (counts.Count != 1 || Enumerable.Range(1, counts.Min).Any(k => !parts.ContainsKey(k)) || parts.Count != counts.Min)
        {
            return (null, $"red-neighbours parts {string.Join(", ", parts.Keys)} of {string.Join(" or ", counts)}: a part is missing");
        }

        var red = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var message in parts.Values)
        {
            string? cls = null;
            foreach (var line in message.Split('\n').Skip(1).Where(l => l.Length > 0))
            {
                if (line.StartsWith('@'))
                {
                    cls = line[1..];
                    continue;
                }

                var p = line.Split(' ');
                if (cls is null || p.Length != 2 || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                {
                    return (null, $"an unreadable red-neighbours line ('{line}')");
                }

                red[$"{cls}.{p[0]}"] = red.GetValueOrDefault($"{cls}.{p[0]}") + count;
            }
        }

        return (red, null);
    }

    /// <summary>An annotations response's entry times, or null when it holds no entry-time notice.</summary>
    internal static IReadOnlyList<(string Id, double Seconds)>? Notice(string annotationsJson)
    {
        using var doc = JsonDocument.Parse(annotationsJson);
        foreach (var a in doc.RootElement.EnumerateArray())
        {
            var message = a.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (!message.StartsWith("sabotage entry times", StringComparison.Ordinal))
            {
                continue;
            }

            return [.. message.Split('\n').Skip(1).Select(l => l.Trim().Split(' ')).Where(p => p.Length == 2)
                .Select(p => (p[0], double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))];
        }

        return null;
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
        var specs = SabotageSpec.LoadLive(repo, f).Where(s => !s.RunsOnHost).ToList();
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
