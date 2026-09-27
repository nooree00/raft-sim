using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Raft.SimRun;

/// <summary>
/// Properties of an echo-counter run, computed from the trace alone.
/// Durability: whenever a node (re)starts, its recovered counter is at least the largest value any
/// peer has received from it — an announced value was durable before it left.
/// Echo validity: every echo a node receives carries a value that node announced (a peer received).
/// </summary>
public static class EchoCounterChecks
{
    /// <summary>A failed property at a node; <see cref="Signature"/> is what the shrinker preserves.</summary>
    public sealed record Violation(string Property, string Node, string Text)
    {
        public string Signature => Property + "@" + Node;

        public override string ToString() => Text;
    }

    public sealed record Result(IReadOnlyList<Violation> Violations, int Sends, int Delivers, int Persists, int Durables, int Starts)
    {
        public bool Ok => Violations.Count == 0;
    }

    public static Result Check(IReadOnlyList<string> trace)
    {
        var violations = new List<Violation>();
        var maxReceived = new Dictionary<string, long>(StringComparer.Ordinal);
        var announced = new HashSet<(string Node, string Value)>();
        int sends = 0, delivers = 0, persists = 0, durables = 0, starts = 0;
        foreach (var line in trace)
        {
            var parts = line.Split(' ');
            var node = parts[1];
            var kind = parts[2];
            var f = parts.Skip(3).Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
            switch (kind)
            {
                case "SEND": sends++; break;
                case "DELIVER": delivers++; break;
                case "PERSIST": persists++; break;
                case "DURABLE": durables++; break;
                case "START": starts++; break;
                case "EVENT" when f["name"] == "got":
                    var v = long.Parse(f["value"], CultureInfo.InvariantCulture);
                    maxReceived[f["from"]] = Math.Max(maxReceived.GetValueOrDefault(f["from"]), v);
                    announced.Add((f["from"], f["value"]));
                    break;
                case "EVENT" when f["name"] == "echo":
                    if (!announced.Contains((node, f["value"])))
                    {
                        violations.Add(new Violation("echo-valid", node, $"{parts[0]} {node} got echo {f["value"]} from {f["from"]}, a value no peer received from it"));
                    }

                    break;
                case "EVENT" when f["name"] == "recovered":
                    var r = long.Parse(f["value"], CultureInfo.InvariantCulture);
                    var seen = maxReceived.GetValueOrDefault(node);
                    if (r < seen)
                    {
                        violations.Add(new Violation("durability", node, $"{parts[0]} {node} recovered {r}, but a peer had received {seen} from it"));
                    }

                    break;
            }
        }

        return new Result(violations, sends, delivers, persists, durables, starts);
    }
}
