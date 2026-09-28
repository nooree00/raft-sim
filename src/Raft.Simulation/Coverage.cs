using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Raft.Simulation;

/// <summary>
/// What a generated execution actually did (spec §7). Since P2-02 every dimension is named for an
/// *effect the algorithm depends on*, never for a fault: a dimension named after a fault measures
/// the injection, one named after an effect measures the test. Every dimension is computed from
/// the trace's effect lines (deliveries, blocks, crashes and what they did to the disk, starts,
/// resumes, durable writes) and never from FAULT or FIRED lines. A Delay that changed no delivery
/// time, a crash that met no write in flight, a partition that blocked nothing: none counts.
/// The residual (docs/findings.md): a dimension list cannot contain an event nobody listed; the list
/// is owned by the Figure 2 rules (the traceability table names the effects each rule depends on
/// and a test requires every name to be a dimension here that clears the floor), not by the faults.
/// </summary>
public static class Coverage
{
    /// <summary>The scales an effect is measured against; defaults match <see cref="SimulationConfig"/>.</summary>
    public sealed record Limits
    {
        public long MaxNetworkDelay { get; init; } = 10;

        public long MaxDiskLatency { get; init; } = 3;

        /// <summary>An election timeout's order of magnitude (Raft's 150–300 ms): the silence a node notices.</summary>
        public long Timeout { get; init; } = 150;
    }

    public static readonly string[] Dimensions =
    [
        "delivered-after-later-send", "lost-in-transit-to-a-live-receiver", "delivered-twice",
        "delivered-later-than-normal-delay", "one-way-reachability", "node-isolated-for-a-timeout",
        "majority-down", "all-down", "unsynced-write-lost", "writes-completed-out-of-order-at-crash",
        "partial-record-left-on-disk", "write-slower-than-normal-latency",
        "step-after-silence-longer-than-a-timeout", "restarted-from-disk", "clock-rate-diverged",
    ];

    /// <summary>
    /// Effects of the positive controls (off in generated runs by design): named in the traceability
    /// table, exercised by their own tests, exempt from the floor.
    /// </summary>
    public static readonly string[] Controls = ["control:synced-write-lost", "control:send-before-its-persist-durable"];

    /// <summary>Below this many executions a dimension is effectively untested: a bug needing that event alone is missed with probability about e^-3 ≈ 5%.</summary>
    public const int Floor = 3;

    /// <summary>At or above this share a dimension must be declared always-on with a reason: the generator can rarely produce the other case.</summary>
    public const double NearAlways = 0.95;

    public static IReadOnlySet<string> Of(IReadOnlyList<string> trace, Limits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        limits ??= new Limits();
        var hit = new HashSet<string>(StringComparer.Ordinal);
        var deliveries = new Dictionary<string, int>(StringComparer.Ordinal);
        var sentAt = new Dictionary<string, long>(StringComparer.Ordinal);
        var sendOrder = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var deliverOrder = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var linkOf = new Dictionary<string, (string From, string To)>(StringComparer.Ordinal);
        var undelivered = new HashSet<string>(StringComparer.Ordinal);
        var lostToDown = new HashSet<string>(StringComparer.Ordinal);
        var persists = new Dictionary<string, int>(StringComparer.Ordinal);
        var persistAt = new Dictionary<(string, string), long>();
        var lastBlocked = new Dictionary<(string, string), long>();
        var lastDelivered = new Dictionary<(string, string), long>();
        var nodes = new List<string>(); // a list: enumeration order must not depend on a hash
        var down = new HashSet<string>(StringComparer.Ordinal);
        var contact = new Dictionary<string, long>(StringComparer.Ordinal);
        var blockedOut = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var blockedIn = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        void Touch(string n, long t)
        {
            contact[n] = t;
            blockedOut[n] = new HashSet<string>(StringComparer.Ordinal);
            blockedIn[n] = new HashSet<string>(StringComparer.Ordinal);
        }

        // Isolated: up throughout, no delivery to or from it for a timeout, and in that time its
        // sends to every peer and every peer's sends to it were blocked.
        void CheckIsolation(string n, long t)
        {
            if (!down.Contains(n) && contact.TryGetValue(n, out var since) && t - since >= limits.Timeout)
            {
                var peers = nodes.Where(p => p != n).ToList();
                if (peers.Count > 0 && peers.All(blockedOut[n].Contains) && peers.All(blockedIn[n].Contains))
                {
                    hit.Add("node-isolated-for-a-timeout");
                }
            }
        }

        long now = 0;
        foreach (var line in trace)
        {
            var p = line.Split(' ');
            now = long.Parse(p[0], CultureInfo.InvariantCulture);
            var node = p[1];
            var f = p.Skip(3).Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.Ordinal);
            switch (p[2])
            {
                case "START":
                    if (!nodes.Contains(node))
                    {
                        nodes.Add(node);
                    }

                    down.Remove(node);
                    Touch(node, now);
                    if (f.TryGetValue("incarnation", out var inc) && inc != "1")
                    {
                        hit.Add("restarted-from-disk");
                    }

                    break;
                case "CRASH":
                    CheckIsolation(node, now);
                    down.Add(node);
                    if (nodes.Count > 0 && down.Count * 2 > nodes.Count)
                    {
                        hit.Add("majority-down");
                    }

                    if (nodes.Count > 0 && down.Count == nodes.Count)
                    {
                        hit.Add("all-down");
                    }

                    CrashEffects(f, hit);
                    break;
                case "SEND":
                    linkOf[f["id"]] = (node, f["to"]);
                    sentAt[f["id"]] = now;
                    Add(sendOrder, node + "->" + f["to"], f["id"]);
                    break;
                case "DROP":
                    undelivered.Add(f["id"]);
                    break;
                case "BLOCKED":
                    lastBlocked[(node, f["to"])] = now;
                    if (blockedOut.TryGetValue(node, out var outs))
                    {
                        outs.Add(f["to"]);
                    }

                    if (blockedIn.TryGetValue(f["to"], out var ins))
                    {
                        ins.Add(node);
                    }

                    break;
                case "LOST":
                    lostToDown.Add(f["id"]);
                    break;
                case "DELIVER":
                    var id = f["id"];
                    deliveries[id] = deliveries.GetValueOrDefault(id) + 1;
                    if (deliveries[id] == 2)
                    {
                        hit.Add("delivered-twice");
                    }

                    if (linkOf.TryGetValue(id, out var l))
                    {
                        if (deliveries[id] == 1)
                        {
                            Add(deliverOrder, l.From + "->" + l.To, id);
                            if (now - sentAt[id] > limits.MaxNetworkDelay + 1)
                            {
                                hit.Add("delivered-later-than-normal-delay");
                            }
                        }

                        // One-way: A→B arrives while the latest event on B→A is a block.
                        var reverse = (l.To, l.From);
                        if (lastBlocked.TryGetValue(reverse, out var b) && b > lastDelivered.GetValueOrDefault(reverse, -1))
                        {
                            hit.Add("one-way-reachability");
                        }

                        lastDelivered[l] = now;
                        CheckIsolation(l.From, now);
                        CheckIsolation(l.To, now);
                        Touch(l.From, now);
                        Touch(l.To, now);
                    }

                    break;
                case "PERSIST":
                    persists[node] = persists.GetValueOrDefault(node) + 1;
                    persistAt[(node, f["seq"])] = now;
                    break;
                case "DURABLE":
                    if (persistAt.TryGetValue((node, f["seq"]), out var issued) && now - issued > limits.MaxDiskLatency)
                    {
                        hit.Add("write-slower-than-normal-latency");
                    }

                    break;
                case "RESUME":
                    if (long.Parse(f["tick"], CultureInfo.InvariantCulture) >= limits.Timeout)
                    {
                        hit.Add("step-after-silence-longer-than-a-timeout");
                    }

                    break;
            }
        }

        foreach (var n in nodes)
        {
            CheckIsolation(n, now);
        }

        if (undelivered.Any(id => !deliveries.ContainsKey(id) && !lostToDown.Contains(id)))
        {
            hit.Add("lost-in-transit-to-a-live-receiver");
        }

        foreach (var (key, sent) in sendOrder)
        {
            if (deliverOrder.TryGetValue(key, out var got) && !got.SequenceEqual(sent.Where(got.Contains)))
            {
                hit.Add("delivered-after-later-send");
                break;
            }
        }

        // Skew shows as one node's write rate diverging from the others' by 8% or more.
        if (persists.Count >= 2 && persists.Values.Min() > 0 && persists.Values.Max() * 100 >= persists.Values.Min() * 108)
        {
            hit.Add("clock-rate-diverged");
        }

        return hit;
    }

    /// <summary>What a crash did to the disk, from the CRASH line's structured fields (docs/design/node-interface.md §4).</summary>
    private static void CrashEffects(Dictionary<string, string> f, HashSet<string> hit)
    {
        var lost = f.TryGetValue("lost", out var l) ? int.Parse(l, CultureInfo.InvariantCulture) : 0;
        if (f.ContainsKey("kept"))
        {
            hit.Add("partial-record-left-on-disk");
            hit.Add("unsynced-write-lost");
        }

        if (lost > 0)
        {
            hit.Add("unsynced-write-lost");
        }

        if (f.TryGetValue("pending", out var pend) && pend != "none")
        {
            var pending = pend.Split(',').Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToList();
            var survived = f["survived"] == "none" ? [] : f["survived"].Split(',').Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToList();
            if (survived.Count < pending.Count)
            {
                hit.Add("unsynced-write-lost");
            }

            // Out of order: some write survived while an earlier one was lost.
            var firstLost = pending.Except(survived).DefaultIfEmpty(long.MaxValue).Min();
            if (survived.Any(s => s > firstLost))
            {
                hit.Add("writes-completed-out-of-order-at-crash");
            }
        }
    }

    private static void Add(Dictionary<string, List<string>> d, string k, string v)
    {
        if (!d.TryGetValue(k, out var list))
        {
            d[k] = list = [];
        }

        list.Add(v);
    }

    /// <summary>
    /// The build rules. A dimension hit in fewer than <see cref="Floor"/> executions is effectively
    /// untested and fails, unless declared rare with a reason. One hit in at least
    /// <see cref="NearAlways"/> of them fails unless declared always-on with a reason: it usually
    /// means the generator cannot produce the other case, or the dimension counts something broader
    /// than its name. Two dimensions hit by exactly the same set of executions fail as one
    /// measurement wearing two names, unless the pair is allowed with a reason; equal counts over
    /// different sets are a note.
    /// </summary>
    public static (IReadOnlyList<string> Failures, IReadOnlyList<string> Notes) Evaluate(
        IReadOnlyDictionary<string, IReadOnlySet<int>> hits, int total,
        IReadOnlyDictionary<string, string> alwaysOn, IReadOnlyDictionary<(string, string), string> allowedIdentical,
        IReadOnlyDictionary<string, string>? rare = null)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(alwaysOn);
        ArgumentNullException.ThrowIfNull(allowedIdentical);
        rare ??= new Dictionary<string, string>();
        var failures = new List<string>();
        var notes = new List<string>();
        IReadOnlySet<int> Hit(string d) => hits.TryGetValue(d, out var h) ? h : new HashSet<int>();
        string N(int v) => v.ToString(CultureInfo.InvariantCulture);
        foreach (var d in Dimensions)
        {
            var c = Hit(d).Count;
            if (c < Floor && !rare.ContainsKey(d))
            {
                failures.Add(d + ": " + N(c) + " of " + N(total) + " — below the floor of " + N(Floor) + ", effectively untested");
            }
            else if (c < Floor)
            {
                notes.Add(d + ": " + N(c) + " of " + N(total) + ", declared rare: " + rare[d]);
            }

            if (c >= NearAlways * total)
            {
                if (alwaysOn.TryGetValue(d, out var why))
                {
                    notes.Add(d + ": " + N(c) + " of " + N(total) + ", declared always-on: " + why);
                }
                else
                {
                    failures.Add(d + ": " + N(c) + " of " + N(total) + " (95% or more) — can the generator produce the other case, or does it count something broader than its name?");
                }
            }
        }

        for (var i = 0; i < Dimensions.Length; i++)
        {
            for (var j = i + 1; j < Dimensions.Length; j++)
            {
                var (a, b) = (Dimensions[i], Dimensions[j]);
                var (ha, hb) = (Hit(a), Hit(b));
                if (ha.Count == 0 || ha.Count != hb.Count)
                {
                    continue;
                }

                if (ha.SetEquals(hb) && !allowedIdentical.ContainsKey((a, b)))
                {
                    failures.Add(a + " and " + b + ": hit by exactly the same " + N(ha.Count) + " executions — one measurement wearing two names?");
                }
                else if (!ha.SetEquals(hb))
                {
                    notes.Add(a + " and " + b + ": equal counts (" + N(ha.Count) + ") over different executions");
                }
            }
        }

        return (failures, notes);
    }
}
