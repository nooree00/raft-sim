using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Raft.Core;

namespace Raft.Simulation;

/// <summary>
/// Rates for the generator, in chances per 10,000 per window: per directed link for network faults,
/// per node for node faults. The positive controls (lose-synced crashes, barrier violations) are off
/// unless <see cref="Controls"/> is set.
/// </summary>
public sealed record GeneratorConfig
{
    public int Nodes { get; init; } = 3;

    public long Duration { get; init; } = 60_000;

    public long Window { get; init; } = 1_000;

    public int Drop { get; init; } = 100;

    public int Duplicate { get; init; } = 100;

    public int Delay { get; init; } = 100;

    public int Reorder { get; init; } = 100;

    public int Partition { get; init; } = 200;

    public int Crash { get; init; } = 200;

    public int Pause { get; init; } = 150;

    public int SlowDisk { get; init; } = 150;

    /// <summary>Chance per run of FIFO links (TCP-like delivery in send order).</summary>
    public int Fifo { get; init; } = 4_000;

    /// <summary>Chance per node, per run, of a skewed clock.</summary>
    public int Skew { get; init; } = 3_000;

    public bool Controls { get; init; }

    /// <summary>A stable hash of every setting, recorded in each reproduction artifact.</summary>
    public string Hash()
    {
        var text = string.Join(';', new long[] { Nodes, Duration, Window, Drop, Duplicate, Delay, Reorder, Partition, Crash, Pause, SlowDisk, Skew, Fifo, Controls ? 1 : 0 }
            .Select(v => v.ToString(CultureInfo.InvariantCulture)));
        return Mix.Hash(text).ToString("x16", CultureInfo.InvariantCulture);
    }
}

/// <summary>Seed → fault schedule (spec §7). Each fault kind draws from its own stream.</summary>
public static class FaultGenerator
{
    public static FaultSchedule Generate(ulong seed, GeneratorConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var s = new Streams(seed);
        var faults = new List<Fault>();
        var nodes = Enumerable.Range(1, config.Nodes).Select(i => new NodeId(i)).ToList();
        var links = nodes.SelectMany(a => nodes.Where(b => b != a).Select(b => (a, b))).ToList();
        IRandomSource R(string kind) => s.For("gen:" + kind);
        var (drop, dup, delay, reorder, part, crash, pause, slow, skew) =
            (R("drop"), R("dup"), R("delay"), R("reorder"), R("partition"), R("crash"), R("pause"), R("slow"), R("skew"));
        bool Chance(IRandomSource r, int per10k) => r.NextLong(10_000) < per10k;
        long Within(IRandomSource r, long lo, long hi) => lo + r.NextLong(hi - lo + 1);

        if (Chance(R("fifo"), config.Fifo))
        {
            faults.Add(new Fifo(0));
        }

        foreach (var n in nodes)
        {
            if (Chance(skew, config.Skew))
            {
                faults.Add(new Skew(0, n, Within(skew, 8, 12), 10));
            }
        }

        for (long w = 0; w < config.Duration; w += config.Window)
        {
            foreach (var (a, b) in links)
            {
                if (Chance(drop, config.Drop))
                {
                    faults.Add(new Drop(w + drop.NextLong(config.Window), a, b));
                }

                if (Chance(dup, config.Duplicate))
                {
                    faults.Add(new Duplicate(w + dup.NextLong(config.Window), a, b));
                }

                if (Chance(delay, config.Delay))
                {
                    faults.Add(new Delay(w + delay.NextLong(config.Window), a, b, Within(delay, 20, 200)));
                }

                if (Chance(reorder, config.Reorder))
                {
                    faults.Add(new Reorder(w + reorder.NextLong(config.Window), a, b));
                }

                if (Chance(part, config.Partition))
                {
                    var at = w + part.NextLong(config.Window);
                    faults.Add(new Partition(at, a, b));
                    faults.Add(new Heal(at + Within(part, 500, 5_000), a, b));
                }
            }

            foreach (var n in nodes)
            {
                if (Chance(crash, config.Crash))
                {
                    var at = w + crash.NextLong(config.Window);
                    var losses = config.Controls
                        ? new[] { DiskLoss.Pending, DiskLoss.Torn, DiskLoss.Reordered, DiskLoss.LoseSynced }
                        : new[] { DiskLoss.Pending, DiskLoss.Torn, DiskLoss.Reordered };
                    faults.Add(new Crash(at, n, losses[crash.NextLong(losses.Length)]));
                    faults.Add(new Restart(at + Within(crash, 100, 3_000), n));
                }

                if (Chance(pause, config.Pause))
                {
                    var at = w + pause.NextLong(config.Window);
                    faults.Add(new Pause(at, n));
                    faults.Add(new Unpause(at + Within(pause, 100, 3_000), n));
                }

                if (Chance(slow, config.SlowDisk))
                {
                    var at = w + slow.NextLong(config.Window);
                    faults.Add(new SlowDisk(at, n, Within(slow, 20, 200), at + Within(slow, 500, 5_000)));
                }
            }
        }

        // Canonical order: by time, then generation order (a stable sort).
        return new FaultSchedule(faults.Select((f, i) => (f, i)).OrderBy(x => x.f.At).ThenBy(x => x.i).Select(x => x.f).ToList());
    }
}
