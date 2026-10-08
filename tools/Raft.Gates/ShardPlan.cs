using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Raft.Gates;

/// <summary>An entry as the shard plan sees it: its id and the baseline unit that proves its target passes unpatched.</summary>
internal sealed record PlanEntry(string Id, string Unit);

/// <summary>
/// The recorded costs the plan balances (P10-00, <see cref="ShardPlan.CostFile"/>): each entry's
/// seconds, each baseline unit's, a worker's baseline build, and the factor beyond which an entry's
/// measured time says its recorded cost is wrong.
/// </summary>
internal sealed record ShardCosts(double Build, double StaleFactor, IReadOnlyDictionary<string, double> Units, IReadOnlyDictionary<string, double> Entries, double StaleFloor = 10, double ShardStaleFactor = 2)
{
    /// <summary>A baseline unit with no recorded cost: a new project or command, until its line is added.</summary>
    public const double DefaultUnit = 30;

    public double Unit(string key) => Units.TryGetValue(key, out var c) ? c : DefaultUnit;

    public double Entry(string id) => Entries.TryGetValue(id, out var c) ? c : 0;

    /// <summary>Lines "stale-factor F", "stale-floor S", "shard-stale-factor G", "build B", "unit KEY SECONDS" and "ID SECONDS"; '#' comments.</summary>
    public static ShardCosts Parse(string text)
    {
        double? build = null, factor = null, floor = null, shardFactor = null;
        var units = new Dictionary<string, double>(StringComparer.Ordinal);
        var entries = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var cut = line.LastIndexOf(' ');
            var value = double.Parse(line[(cut + 1)..], CultureInfo.InvariantCulture);
            var key = line[..cut];
            if (key == "stale-factor")
            {
                factor = value;
            }
            else if (key == "stale-floor")
            {
                floor = value;
            }
            else if (key == "shard-stale-factor")
            {
                shardFactor = value;
            }
            else if (key == "build")
            {
                build = value;
            }
            else if (key.StartsWith("unit ", StringComparison.Ordinal))
            {
                units[key[5..]] = value;
            }
            else if (!entries.TryAdd(key, value))
            {
                throw new FormatException($"{ShardPlan.CostFile}: {key} given twice");
            }
        }

        return new ShardCosts(build ?? throw new FormatException($"{ShardPlan.CostFile}: no build"), factor ?? throw new FormatException($"{ShardPlan.CostFile}: no stale-factor"), units, entries, floor ?? throw new FormatException($"{ShardPlan.CostFile}: no stale-floor"), shardFactor ?? throw new FormatException($"{ShardPlan.CostFile}: no shard-stale-factor"));
    }

    /// <summary>Equal costs for every entry (tests).</summary>
    public static ShardCosts Uniform(IEnumerable<string> ids) =>
        new(60, 3, new Dictionary<string, double>(StringComparer.Ordinal), ids.ToDictionary(id => id, _ => 10.0, StringComparer.Ordinal));
}

/// <summary>
/// How the sabotage harness is split across CI jobs (breakdown P2-01). The shard count is derived
/// from the manifest, never chosen per run: n = ceil(entries / K), with K committed in
/// <see cref="SizeFile"/>, so adding entries adds shards and the 15-minute ceiling applies per
/// shard. Entries (those the harness runs, not the host ones) are assigned by recorded cost
/// (P10-00, <see cref="Balance"/>): dealt by id, the slowest shard was whichever collected several
/// of the costliest entries, and changing the size only reshuffled which.
/// Vacuity risk: a plan that deals some entry to no shard, or yields an empty shard, reports green
/// having run less. Guarded by <see cref="Problems"/>, checked by the harness on every run and by
/// the collect job, and by tests.
/// </summary>
internal static class ShardPlan
{
    public const string SizeFile = "ci/sabotage-shard-size.txt";

    public const string CostFile = "ci/sabotage-costs.txt";

    /// <summary>The workers a CI shard job runs (its runner's four cores); the plan models that many.</summary>
    public const int Workers = 4;

    /// <summary>
    /// The plan: longest entry first, each to the shard whose modelled time after it is least; ties to
    /// the shard with fewer entries, then the lower one, entries of equal cost in id order, so the plan
    /// is deterministic. A shard already paying an entry's baseline unit takes it more cheaply than
    /// one that is not, so entries of one unit tend to share a shard: the unit's check runs once.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Balance(IReadOnlyList<PlanEntry> entries, int n, ShardCosts costs)
    {
        var shards = Enumerable.Range(0, n).Select(_ => new List<PlanEntry>()).ToList();
        foreach (var e in entries.OrderByDescending(e => costs.Entry(e.Id)).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            // A shard's modelled time does not grow until its workers are all busy, so ties are
            // common: they go to the shard with fewer entries, then the lower one.
            var best = 0;
            var bestTime = double.MaxValue;
            for (var k = 0; k < n; k++)
            {
                var time = ModelledTime([.. shards[k], e], costs);
                if (time < bestTime || (time == bestTime && shards[k].Count < shards[best].Count))
                {
                    best = k;
                    bestTime = time;
                }
            }

            shards[best].Add(e);
        }

        return shards.Select(s => (IReadOnlyList<string>)s.Select(e => e.Id).Order(StringComparer.Ordinal).ToList()).ToList();
    }

    /// <summary>
    /// A shard's entries over its workers: each worker's baseline units as the harness deals them
    /// (<see cref="Sabotage.AssignBaselines"/>), then the longest entry first to the worker that would
    /// finish soonest, counting its baseline build and units (no barrier: in CI a worker starts its
    /// entries when its own baseline is ready).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<PlanEntry>> WorkerShares(IReadOnlyList<PlanEntry> entries, int workers, ShardCosts costs)
    {
        var w = Math.Max(1, Math.Min(workers, entries.Count));
        var units = Sabotage.AssignBaselines(entries.Select(e => e.Unit).Distinct(StringComparer.Ordinal).ToList(), w);
        var load = units.Select(u => costs.Build + u.Sum(costs.Unit)).ToArray();
        var shares = Enumerable.Range(0, w).Select(_ => new List<PlanEntry>()).ToList();
        foreach (var e in entries.OrderByDescending(e => costs.Entry(e.Id)).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            var k = Array.IndexOf(load, load.Min());
            shares[k].Add(e);
            load[k] += costs.Entry(e.Id);
        }

        return shares;
    }

    /// <summary>The shard's modelled time: its slowest worker's baseline build, units and entries.</summary>
    public static double ModelledTime(IReadOnlyList<PlanEntry> entries, ShardCosts costs, int workers = Workers)
    {
        if (entries.Count == 0)
        {
            return 0;
        }

        var w = Math.Max(1, Math.Min(workers, entries.Count));
        var units = Sabotage.AssignBaselines(entries.Select(e => e.Unit).Distinct(StringComparer.Ordinal).ToList(), w);
        var shares = WorkerShares(entries, w, costs);
        return Enumerable.Range(0, w).Max(k => costs.Build + units[k].Sum(costs.Unit) + shares[k].Sum(e => costs.Entry(e.Id)));
    }

    /// <summary>A manifest entry with no recorded cost, and a cost for no entry: each a refusal (P10-00).</summary>
    public static IReadOnlyList<string> CostProblems(IEnumerable<string> ids, ShardCosts costs)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        return set.Where(id => !costs.Entries.ContainsKey(id)).Order(StringComparer.Ordinal).Select(id => $"{id} has no cost in {CostFile}: add its line, from its own run")
            .Concat(costs.Entries.Keys.Where(id => !set.Contains(id)).Order(StringComparer.Ordinal).Select(id => $"{CostFile} has a cost for {id}, which is not a harness entry"))
            .ToList();
    }

    /// <summary>
    /// The reviewer's condition on recorded costs (P10-00): an entry that took more than the factor
    /// times its recorded cost, or less than its recorded cost over the factor, has a wrong line, when
    /// it is also off by more than the floor in seconds: a 2-s entry taking 7 s is noise, not a claim.
    /// <paramref name="speedup"/> widens the lower bound only: a run with more processors per entry
    /// than the line was timed with may be that much faster, and is no slower for it.
    /// </summary>
    public static bool Stale(double actual, double recorded, double factor, double floor, double speedup = 1) =>
        recorded > 0 && Math.Abs(actual - recorded) > floor && (actual > recorded * factor || actual < recorded / (factor * speedup));

    public static int Count(int entries, int size)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "shard size must be positive");
        }

        return Math.Max(1, (entries + size - 1) / size);
    }

    /// <summary>Shard k (0-based) of n: the sorted ids at positions k, k+n, k+2n, ...</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Deal(IEnumerable<string> ids, int n)
    {
        var sorted = ids.Order(StringComparer.Ordinal).ToList();
        return Enumerable.Range(0, n).Select(k => (IReadOnlyList<string>)sorted.Where((_, i) => i % n == k).ToList()).ToList();
    }

    /// <summary>Every id in exactly one shard, and no shard empty.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyCollection<string> ids, IReadOnlyList<IReadOnlyList<string>> shards)
    {
        var problems = new List<string>();
        var dealt = shards.SelectMany(s => s).ToList();
        foreach (var missing in ids.Except(dealt, StringComparer.Ordinal))
        {
            problems.Add($"{missing} is in no shard");
        }

        foreach (var twice in dealt.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"{twice.Key} is in {twice.Count()} shards");
        }

        foreach (var extra in dealt.Except(ids, StringComparer.Ordinal).Distinct(StringComparer.Ordinal))
        {
            problems.Add($"{extra} is dealt but not in the manifest");
        }

        for (var k = 0; k < shards.Count; k++)
        {
            if (shards[k].Count == 0)
            {
                problems.Add($"shard {k + 1}/{shards.Count} is empty");
            }
        }

        return problems;
    }

    /// <summary>
    /// Shard "i/n"'s entries, after checking that n is the count the manifest implies and that the
    /// plan deals every entry exactly once; null (with the failure recorded) otherwise. A shard job
    /// started with a stale count — entries added since the plan was made — fails rather than
    /// silently skipping the entries a missing shard would have run.
    /// </summary>
    public static IReadOnlyList<string>? Select(IReadOnlyList<PlanEntry> entries, string shard, int size, ShardCosts costs, Findings f)
    {
        var ids = entries.Select(e => e.Id).ToList();
        var (index, of) = Parse(shard);
        var expected = Count(ids.Count, size);
        if (of != expected)
        {
            f.Fail($"shard {shard}: the manifest has {ids.Count} harness entries, which is {expected} shard(s) of {size}, not {of}");
            return null;
        }

        var refused = CostProblems(ids, costs);
        foreach (var problem in refused)
        {
            f.Fail("shard plan: " + problem);
        }

        if (refused.Count > 0)
        {
            return null;
        }

        var shards = Balance(entries, of, costs);
        var problems = Problems(ids.ToList(), shards);
        foreach (var problem in problems)
        {
            f.Fail("shard plan: " + problem);
        }

        return problems.Count == 0 ? shards[index - 1] : null;
    }

    /// <summary>"i/n" (1-based) → (i, n).</summary>
    public static (int Index, int Of) Parse(string text)
    {
        var parts = text.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var i)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || i < 1 || i > n)
        {
            throw new ArgumentException($"shard '{text}' is not i/n with 1 <= i <= n");
        }

        return (i, n);
    }

    /// <summary>
    /// The plan at a commit, read from its tree: null if the commit predates sharding (no
    /// <see cref="SizeFile"/>), else the harness entries and the shard count.
    /// </summary>
    public static (IReadOnlyList<string> Ids, int Of)? AtCommit(Repo repo, string sha)
    {
        var size = repo.Git("show", $"{sha}:{SizeFile}");
        if (!size.Ok)
        {
            return null;
        }

        var ids = repo.Git("ls-tree", "--name-only", $"{sha}:sabotage").StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(id => !IsHost(repo.Git("show", $"{sha}:sabotage/{id}/sabotage.txt").StdOut))
            .ToList();
        return (ids, Count(ids.Count, ParseSize(size.StdOut)));
    }

    public static int ParseSize(string text) =>
        int.Parse(text.Split('\n').Select(l => l.Trim()).First(l => l.Length > 0 && !l.StartsWith('#')), CultureInfo.InvariantCulture);

    private static bool IsHost(string sabotageTxt) =>
        sabotageTxt.Split('\n').Any(l => l.Trim() == "runner: host");
}
