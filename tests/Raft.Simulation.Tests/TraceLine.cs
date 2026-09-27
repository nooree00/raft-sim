using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Raft.Simulation.Tests;

/// <summary>One parsed trace line: time, node, kind, fields.</summary>
internal sealed record TraceLine(long Time, string Node, string Kind, IReadOnlyDictionary<string, string> Fields)
{
    public string this[string key] => Fields[key];

    public long Long(string key) => long.Parse(Fields[key], CultureInfo.InvariantCulture);

    public static List<TraceLine> Parse(IEnumerable<string> lines) => lines.Select(l =>
    {
        var p = l.Split(' ');
        var f = p.Skip(3).Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .GroupBy(x => x[0]).ToDictionary(g => g.Key, g => g.First()[1], StringComparer.Ordinal);
        return new TraceLine(long.Parse(p[0], CultureInfo.InvariantCulture), p[1], p[2], f);
    }).ToList();
}
