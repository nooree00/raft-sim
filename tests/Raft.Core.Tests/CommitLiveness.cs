using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Simulation;

namespace Raft.Core.Tests;

/// <summary>
/// Invariant 11's second clause, "a submitted command commits" (P3 decision 3), in phase 4 (P4-06).
/// A command counts by its ghost id (P4 decision 5): an entry with the same bytes created before the
/// window does not count, and neither does a leader's no-op, which commits without any client. The
/// fact is commitment in fact (P4 decision 2), never a node's claim.
/// </summary>
internal static class CommitLiveness
{
    /// <summary>
    /// After the last fault heals at <paramref name="stableFrom"/>, with a majority up, a client's
    /// command created at or after it is committed in fact within <paramref name="window"/>. The
    /// stable suffix is the one <see cref="ElectionInvariants.Liveness"/> requires; without one the
    /// execution is counted, not passed.
    /// </summary>
    public static InvariantResult Clause(ElectionHistory h, IReadOnlyList<Observation> observations, LogAnalysis log, long stableFrom, long end, long window, long minimumSuffix)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (ElectionInvariants.Liveness(h, stableFrom, end, window, minimumSuffix).Count("no-stable-suffix") > 0)
        {
            counts["no-stable-suffix"] = 1;
            return new("liveness-commit", [], counts);
        }

        long Time(long seq) => observations[(int)seq - 1].Time;
        var commands = log.Commits.Where(c => c.Command && c.CreatedSeq > 0 && Time(c.CreatedSeq) >= stableFrom).ToList();
        counts["checked"] = 1;
        counts["commands-committed"] = commands.Count;
        var first = commands.Select(c => Time(c.Seq)).DefaultIfEmpty(long.MaxValue).Min();
        if (first == long.MaxValue || first - stableFrom > window)
        {
            return new("liveness-commit", [$"no client command created after the stable suffix began at {stableFrom} was committed in fact within {window}"], counts);
        }

        counts["time-to-commit"] = first - stableFrom;
        return new("liveness-commit", [], counts);
    }

    /// <summary>
    /// The clause under a fault that never heals (P3-06's one-way partition): from
    /// <paramref name="from"/> to <paramref name="to"/>, no interval of <paramref name="window"/>
    /// passes without a client's command becoming committed in fact.
    /// </summary>
    public static InvariantResult Continuity(IReadOnlyList<Observation> observations, LogAnalysis log, long from, long to, long window)
    {
        var times = log.Commits.Where(c => c.Command).Select(c => observations[(int)c.Seq - 1].Time).Where(t => t >= from && t <= to).Order().ToList();
        var violations = new List<string>();
        var last = from;
        foreach (var t in times.Append(to))
        {
            if (t - last > window)
            {
                violations.Add($"no client command was committed in fact from {last} to {t}");
            }

            last = Math.Max(last, t);
        }

        return new("commit-continuity", violations, new Dictionary<string, long>(StringComparer.Ordinal) { ["commands-committed"] = times.Count });
    }
}
