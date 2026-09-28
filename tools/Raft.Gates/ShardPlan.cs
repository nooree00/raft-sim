using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Raft.Gates;

/// <summary>
/// How the sabotage harness is split across CI jobs (breakdown P2-01). The shard count is derived
/// from the manifest, never chosen per run: n = ceil(entries / K), with K committed in
/// <see cref="SizeFile"/>, so adding entries adds shards and the 15-minute ceiling applies per
/// shard. Entries (those the harness runs, not the host ones) are dealt round-robin over their
/// sorted ids, so a shard's content changes only when entries are added or removed.
/// Vacuity risk: a plan that deals some entry to no shard, or yields an empty shard, reports green
/// having run less. Guarded by <see cref="Problems"/>, checked by the harness on every run and by
/// the collect job, and by tests.
/// </summary>
internal static class ShardPlan
{
    public const string SizeFile = "ci/sabotage-shard-size.txt";

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
    public static IReadOnlyList<string>? Select(IReadOnlyList<string> ids, string shard, int size, Findings f)
    {
        var (index, of) = Parse(shard);
        var expected = Count(ids.Count, size);
        if (of != expected)
        {
            f.Fail($"shard {shard}: the manifest has {ids.Count} harness entries, which is {expected} shard(s) of {size}, not {of}");
            return null;
        }

        var shards = Deal(ids, of);
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
