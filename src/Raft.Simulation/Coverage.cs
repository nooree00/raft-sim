using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Raft.Simulation;

/// <summary>
/// What a generated execution actually did (spec §7): every dimension is computed from the trace's
/// effect lines, never from the schedule's intentions — a Torn crash that met no write in flight
/// tore nothing, a partition that blocked no message partitioned nothing.
/// </summary>
public static class Coverage
{
    public static readonly string[] Dimensions =
    [
        "asymmetric-block", "dropped", "duplicate-delivered", "reordered-delivered", "delayed",
        "crashed", "crash-lost-writes", "torn-record", "paused", "clock-skewed",
    ];

    public static IReadOnlySet<string> Of(IReadOnlyList<string> trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var hit = new HashSet<string>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var deliveries = new Dictionary<string, int>(StringComparer.Ordinal);
        var sendOrder = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var deliverOrder = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var linkOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var persists = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in trace)
        {
            var p = line.Split(' ');
            var node = p[1];
            var f = p.Skip(3).Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.Ordinal);
            switch (p[2])
            {
                case "FAULT" when f["kind"] == "Partition":
                    blocked.Add(f["detail"]);
                    break;
                case "FAULT" when f["kind"] == "Heal":
                    blocked.Remove(f["detail"]);
                    break;
                case "BLOCKED":
                    // Blocked while the reverse direction was open: an asymmetric partition in effect.
                    if (!blocked.Contains(f["to"] + "->" + node))
                    {
                        hit.Add("asymmetric-block");
                    }

                    break;
                case "SEND":
                    var link = node + "->" + f["to"];
                    linkOf[f["id"]] = link;
                    Add(sendOrder, link, f["id"]);
                    break;
                case "DROP":
                    dropped.Add(f["id"]);
                    break;
                case "DELAYED":
                    hit.Add("delayed");
                    break;
                case "DELIVER":
                    deliveries[f["id"]] = deliveries.GetValueOrDefault(f["id"]) + 1;
                    if (deliveries[f["id"]] == 1 && linkOf.TryGetValue(f["id"], out var l))
                    {
                        Add(deliverOrder, l, f["id"]);
                    }

                    break;
                case "CRASH":
                    hit.Add("crashed");
                    if (Lost(f) > 0 || f.ContainsKey("kept"))
                    {
                        hit.Add("crash-lost-writes");
                    }

                    if (f.ContainsKey("kept"))
                    {
                        hit.Add("torn-record");
                    }

                    break;
                case "RESUME":
                    hit.Add("paused");
                    break;
                case "PERSIST":
                    persists[node] = persists.GetValueOrDefault(node) + 1;
                    break;
            }
        }

        if (deliveries.Values.Any(c => c >= 2))
        {
            hit.Add("duplicate-delivered");
        }

        if (dropped.Any(id => !deliveries.ContainsKey(id)))
        {
            hit.Add("dropped");
        }

        foreach (var (link, sent) in sendOrder)
        {
            if (deliverOrder.TryGetValue(link, out var got) && !got.SequenceEqual(sent.Where(got.Contains)))
            {
                hit.Add("reordered-delivered");
                break;
            }
        }

        // Skew shows as one node's write rate diverging from the others' by 8% or more.
        if (persists.Count >= 2 && persists.Values.Min() > 0 && persists.Values.Max() * 100 >= persists.Values.Min() * 108)
        {
            hit.Add("clock-skewed");
        }

        return hit;
    }

    private static int Lost(Dictionary<string, string> f)
    {
        var lost = f.TryGetValue("lost", out var l) ? int.Parse(l, CultureInfo.InvariantCulture) : 0;
        var survived = f.TryGetValue("pending", out var pend) && pend != "none" ? pend.Split(',').Length : 0;
        return lost + survived;
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
    /// The build rules: a dimension hit in no execution, or in every one, fails (100% usually means
    /// the generator cannot produce the other case) unless declared always-on; two dimensions hit by
    /// exactly the same set of executions fail as one measurement wearing two names, unless the pair
    /// is allowed with a reason. Equal counts over different sets are a coincidence, reported as a note
    /// (spec §7 names identical counts; identical sets is the property it means).
    /// </summary>
    public static (IReadOnlyList<string> Failures, IReadOnlyList<string> Notes) Evaluate(
        IReadOnlyDictionary<string, IReadOnlySet<int>> hits, int total,
        IReadOnlySet<string> alwaysOn, IReadOnlyDictionary<(string, string), string> allowedIdentical)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(alwaysOn);
        ArgumentNullException.ThrowIfNull(allowedIdentical);
        var failures = new List<string>();
        var notes = new List<string>();
        IReadOnlySet<int> Hit(string d) => hits.TryGetValue(d, out var h) ? h : new HashSet<int>();
        foreach (var d in Dimensions)
        {
            var c = Hit(d).Count;
            if (c == 0)
            {
                failures.Add(d + ": 0 of " + total.ToString(CultureInfo.InvariantCulture));
            }
            else if (c == total && !alwaysOn.Contains(d))
            {
                failures.Add(d + ": 100% (" + total.ToString(CultureInfo.InvariantCulture) + ") — can the generator produce the other case?");
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
                    failures.Add(a + " and " + b + ": hit by exactly the same " + ha.Count.ToString(CultureInfo.InvariantCulture) + " executions — one measurement wearing two names?");
                }
                else if (!ha.SetEquals(hb))
                {
                    notes.Add(a + " and " + b + ": equal counts (" + ha.Count.ToString(CultureInfo.InvariantCulture) + ") over different executions");
                }
            }
        }

        return (failures, notes);
    }
}
