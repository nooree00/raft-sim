using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Raft.Simulation;

/// <summary>
/// The canonical trace (spec §9, P1-08): one line per event — zero-padded logical time, node, kind,
/// then key=value fields in the order given. Invariant formatting only; no hash codes, no
/// dictionary-order dependence. Byte-identical across processes for the same run.
/// </summary>
public sealed class Trace
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines => _lines;

    public void Add(long time, string node, string kind, params (string Key, object Value)[] fields)
    {
        var sb = new StringBuilder();
        sb.Append(time.ToString("D10", CultureInfo.InvariantCulture)).Append(' ').Append(node).Append(' ').Append(kind);
        foreach (var (k, v) in fields)
        {
            sb.Append(' ').Append(k).Append('=').Append(Format(v));
        }

        _lines.Add(sb.ToString());
    }

    public string Text() => string.Join('\n', _lines) + "\n";

    private static string Format(object v) => v switch
    {
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        ulong u => u.ToString("x16", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => throw new ArgumentException($"unsupported trace field type {v.GetType().Name}"),
    };
}
