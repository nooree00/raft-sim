using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Raft.SimRun;

/// <summary>
/// Properties of an echo-counter run, computed from the trace alone.
/// Durability: whenever a node (re)starts, its recovered counter is at least the largest value any
/// peer has received from it — an announced value was durable before it left.
/// </summary>
public static class EchoCounterChecks
{
    public sealed record Result(IReadOnlyList<string> Violations, int Sends, int Delivers, int Persists, int Durables, int Starts)
    {
        public bool Ok => Violations.Count == 0;
    }

    public static Result Check(IReadOnlyList<string> trace)
    {
        var violations = new List<string>();
        var maxReceived = new Dictionary<string, long>(StringComparer.Ordinal);
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
                    break;
                case "EVENT" when f["name"] == "recovered":
                    var r = long.Parse(f["value"], CultureInfo.InvariantCulture);
                    var seen = maxReceived.GetValueOrDefault(node);
                    if (r < seen)
                    {
                        violations.Add($"{parts[0]} {node} recovered {r}, but a peer had received {seen} from it");
                    }

                    break;
            }
        }

        return new Result(violations, sends, delivers, persists, durables, starts);
    }
}
