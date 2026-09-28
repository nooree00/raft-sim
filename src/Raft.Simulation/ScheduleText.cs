using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Raft.Core;

namespace Raft.Simulation;

/// <summary>
/// The reproduction artifact (spec §7 as amended): a fault schedule as stable text, with the seed
/// that drives in-run randomness, the generator-config hash and the commit. A shrunk schedule no
/// longer comes from its seed's generator, so this text — not the seed — is what reproduces a run.
/// One fault per line: its kind, then its fields in a fixed order.
/// </summary>
public static class ScheduleText
{
    public const string Magic = "# raft-sim schedule v1";

    public sealed record Header(ulong Seed, long Duration, int Nodes, string ConfigHash, string Commit);

    public static string Write(Header h, FaultSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(schedule);
        var sb = new StringBuilder();
        sb.Append(Magic).Append('\n');
        sb.Append("seed=").Append(Num(h.Seed)).Append('\n');
        sb.Append("duration=").Append(Num(h.Duration)).Append('\n');
        sb.Append("nodes=").Append(Num(h.Nodes)).Append('\n');
        sb.Append("config=").Append(h.ConfigHash).Append('\n');
        sb.Append("commit=").Append(h.Commit).Append('\n');
        foreach (var f in schedule.Faults)
        {
            sb.Append(Line(f)).Append('\n');
        }

        return sb.ToString();
    }

    public static (Header Header, FaultSchedule Schedule) Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || lines[0] != Magic)
        {
            throw new FormatException("not a raft-sim schedule (missing '" + Magic + "')");
        }

        var header = new Dictionary<string, string>(StringComparer.Ordinal);
        var faults = new List<Fault>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Contains(' ', StringComparison.Ordinal))
            {
                faults.Add(Parse(line));
            }
            else
            {
                var kv = line.Split('=', 2);
                header[kv[0]] = kv[1];
            }
        }

        var h = new Header(
            ulong.Parse(header["seed"], CultureInfo.InvariantCulture),
            long.Parse(header["duration"], CultureInfo.InvariantCulture),
            int.Parse(header["nodes"], CultureInfo.InvariantCulture),
            header["config"],
            header["commit"]);
        return (h, new FaultSchedule(faults));
    }

    public static string Line(Fault f) => f switch
    {
        Drop d => Link("Drop", d),
        Duplicate d => Link("Duplicate", d),
        Delay d => Link("Delay", d) + " extra=" + Num(d.Extra),
        Reorder r => Link("Reorder", r),
        Partition p => Link("Partition", p),
        Heal h => Link("Heal", h),
        Crash c => Node("Crash", c) + " loss=" + c.Loss,
        Restart r => Node("Restart", r),
        SlowDisk s => Node("SlowDisk", s) + " latency=" + Num(s.Latency) + " until=" + Num(s.Until),
        BarrierViolation b => Node("BarrierViolation", b) + " until=" + Num(b.Until),
        Pause p => Node("Pause", p),
        Unpause u => Node("Unpause", u),
        Skew s => Node("Skew", s) + " num=" + Num(s.Numerator) + " den=" + Num(s.Denominator),
        Fifo x => "Fifo at=" + Num(x.At),
        CrashWhenInFlight c => Node("CrashWhenInFlight", c) + " min=" + Num(c.MinPending) + " loss=" + c.Loss + " down=" + Num(c.Down),
        CrashAfterWrite c => Node("CrashAfterWrite", c) + " loss=" + c.Loss + " down=" + Num(c.Down),
        Isolate i => Node("Isolate", i) + " until=" + Num(i.Until),
        CrashAll a => "CrashAll at=" + Num(a.At) + " until=" + Num(a.Until),
        CrashMajority m => "CrashMajority at=" + Num(m.At) + " until=" + Num(m.Until),
        _ => throw new ArgumentException("unknown fault " + f.GetType().Name),
    };

    private static Fault Parse(string line)
    {
        var parts = line.Split(' ');
        var f = parts.Skip(1).Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        long L(string k) => long.Parse(f[k], CultureInfo.InvariantCulture);
        NodeId N(string k) => new(int.Parse(f[k].AsSpan(1), CultureInfo.InvariantCulture));
        var at = L("at");
        return parts[0] switch
        {
            "Drop" => new Drop(at, N("from"), N("to")),
            "Duplicate" => new Duplicate(at, N("from"), N("to")),
            "Delay" => new Delay(at, N("from"), N("to"), L("extra")),
            "Reorder" => new Reorder(at, N("from"), N("to")),
            "Partition" => new Partition(at, N("from"), N("to")),
            "Heal" => new Heal(at, N("from"), N("to")),
            "Crash" => new Crash(at, N("node"), Enum.Parse<DiskLoss>(f["loss"])),
            "Restart" => new Restart(at, N("node")),
            "SlowDisk" => new SlowDisk(at, N("node"), L("latency"), L("until")),
            "BarrierViolation" => new BarrierViolation(at, N("node"), L("until")),
            "Pause" => new Pause(at, N("node")),
            "Unpause" => new Unpause(at, N("node")),
            "Skew" => new Skew(at, N("node"), L("num"), L("den")),
            "Fifo" => new Fifo(at),
            "CrashWhenInFlight" => new CrashWhenInFlight(at, N("node"), (int)L("min"), Enum.Parse<DiskLoss>(f["loss"]), L("down")),
            "CrashAfterWrite" => new CrashAfterWrite(at, N("node"), Enum.Parse<DiskLoss>(f["loss"]), L("down")),
            "Isolate" => new Isolate(at, N("node"), L("until")),
            "CrashAll" => new CrashAll(at, L("until")),
            "CrashMajority" => new CrashMajority(at, L("until")),
            _ => throw new FormatException("unknown fault kind in '" + line + "'"),
        };
    }

    private static string Link(string kind, LinkFault l) => kind + " at=" + Num(l.At) + " from=" + l.From + " to=" + l.To;

    private static string Node(string kind, NodeFault n) => kind + " at=" + Num(n.At) + " node=" + n.Node;

    private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Num(ulong v) => v.ToString(CultureInfo.InvariantCulture);
}
