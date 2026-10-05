using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Simulation;

namespace Raft.Core.Tests;

/// <summary>
/// P8-07: what sessions and reads did in an execution, each read where it happened (phase 6's
/// lesson: a dimension counted from the party that decided it measures the decision, not the
/// effect). A retry deduplicated is counted from the committed log, never from a client's resend: a
/// resend whose original never committed deduplicates nothing. A read's wait is the simulator's own
/// record at the leader, from the request's arrival to the answer's release, and the later term from
/// the election history built from observations. Sabotage S-cov-14.
/// </summary>
internal static class SessionCoverage
{
    /// <summary>The dimensions every soak measures (P8-07).</summary>
    public static readonly string[] Dimensions =
    [
        "retry-committed-twice-applied-once", "retry-deduplicated-by-a-restored-table", "stale-sequence-refused",
        "read-answered-after-a-later-term-began", "read-waited-past-a-heartbeat-interval",
    ];

    /// <summary>The <see cref="Dimensions"/> one execution hit. <paramref name="trace"/> is the simulator's; a construction without one passes none.</summary>
    public static HashSet<string> Of(IReadOnlyList<Observation> observations, LogAnalysis log, ElectionHistory elections, IReadOnlyList<ClientOp> clients, IReadOnlyList<string> trace, long heartbeat)
    {
        var hit = new HashSet<string>(StringComparer.Ordinal);

        // The committed log, in index order, as (session, sequence) where an entry carries one.
        var previous = new Dictionary<long, long>();
        var lastCopy = new Dictionary<(long, long), long>();
        var latest = new Dictionary<long, long>();
        foreach (var c in log.Commits.OrderBy(c => c.Index))
        {
            if (log.CommandOf(c.Ghost) is not { } bytes || ClientHistory.InSession(Encoding.ASCII.GetString(bytes)) is not { } k)
            {
                continue;
            }

            // A second copy while its number is still the session's latest is answered from the cache;
            // below the latest it is refused as stale, which is the next dimension, not this one.
            var stale = latest.TryGetValue(k.Session, out var max) && k.Sequence < max;
            if (lastCopy.TryGetValue((k.Session, k.Sequence), out var before) && !stale)
            {
                hit.Add("retry-committed-twice-applied-once");
                previous[c.Index] = before;
            }

            lastCopy[(k.Session, k.Sequence)] = c.Index;

            if (stale)
            {
                hit.Add("stale-sequence-refused");
            }

            latest[k.Session] = Math.Max(max, k.Sequence);
        }

        // A node that restored a snapshot at r and then applied, at i, a copy of a command committed
        // at or below r, with no copy between: its answer came from the table the snapshot carried.
        // The apply and restore events are the node's; the agreement check holds each restored state
        // and each final state to the committed entries replayed (P7-08).
        var restored = new Dictionary<NodeId, long>();
        foreach (var o in observations)
        {
            if (o is StartObservation)
            {
                restored.Remove(o.Node);
            }

            if (o is not EmittedObservation { Event.Name: "apply" or "restore" } e)
            {
                continue;
            }

            var index = long.Parse(e.Event.Fields.Single(f => f.Key == "index").Value, CultureInfo.InvariantCulture);
            if (e.Event.Name == "restore")
            {
                restored[e.Node] = index;
                continue;
            }

            if (restored.TryGetValue(e.Node, out var restoredAt) && index > restoredAt && previous.TryGetValue(index, out var copy) && copy <= restoredAt)
            {
                hit.Add("retry-deduplicated-by-a-restored-table");
            }
        }

        // Reads, from the simulator's trace at the node: arrival (REQUEST) and release (RESPONSE).
        // A read is a `Get` outside a session that its client saw answered `ok|`, with no entry of its
        // bytes committed (P8-09): a read routed through the log is not a ReadIndex read.
        var logged = log.Commits.Select(c => log.CommandOf(c.Ghost)).Where(b => b is [(byte)'G', (byte)'e', (byte)'t', (byte)'|', ..]).Select(b => Encoding.ASCII.GetString(b!)).ToHashSet(StringComparer.Ordinal);
        var arrived = new Dictionary<string, (long Time, string Node)>(StringComparer.Ordinal);
        var reads = clients.Where(c => c.Response is not null && Encoding.ASCII.GetString(c.Request.Span) is var r && r.StartsWith("Get|", StringComparison.Ordinal) && !logged.Contains(r)
                && Encoding.ASCII.GetString(c.Reply.Span).StartsWith("ok|", StringComparison.Ordinal))
            .Select(c => c.RequestId.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        var elected = elections.Elections();
        foreach (var line in trace)
        {
            var p = line.Split(' ');
            if (p.Length < 4)
            {
                continue;
            }

            var time = long.Parse(p[0], CultureInfo.InvariantCulture);
            var request = Field(p, "request");
            if (p[2] == "REQUEST" && request is not null)
            {
                arrived[request] = (time, p[1]);
            }
            else if (p[2] == "RESPONSE" && request is not null && reads.Contains(request) && arrived.TryGetValue(request, out var a) && a.Node == p[1])
            {
                if (time - a.Time > heartbeat)
                {
                    hit.Add("read-waited-past-a-heartbeat-interval");
                }

                var own = elected.Where(x => x.Key.Candidate.ToString() == p[1] && x.Value <= time).Select(x => x.Key.Term.Value).DefaultIfEmpty(0).Max();
                if (elected.Any(x => x.Key.Candidate.ToString() != p[1] && x.Key.Term.Value > own && x.Value <= time))
                {
                    hit.Add("read-answered-after-a-later-term-began");
                }
            }
        }

        return hit;
    }

    private static string? Field(string[] parts, string key)
    {
        for (var i = 3; i < parts.Length; i++)
        {
            if (parts[i].StartsWith(key + "=", StringComparison.Ordinal))
            {
                return parts[i][(key.Length + 1)..];
            }
        }

        return null;
    }
}
