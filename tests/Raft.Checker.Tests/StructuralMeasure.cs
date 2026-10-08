using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Raft.Checker;

namespace Raft.Checker.Tests;

/// <summary>
/// P11-07: single-key histories whose difficulty is set directly, every one a history a correct store
/// produces. A store that is linearizable by construction: each operation takes effect at one instant
/// inside its interval, writes are `Put`s of unique values and reads return the value at their instant.
/// A write is lost with probability <c>lost</c>: its client never hears back (no response), it took
/// effect or not (even odds), at an instant up to three operation lengths after its invocation, and
/// the client moves on after a timeout. Since phase 8 neither soak has an undecided key (the ones phase
/// 6 measured were how retries were recorded), so the register row's question is asked of these.
/// </summary>
internal static class StructuralMeasure
{
    internal sealed record Shape(int Clients, int PerClient, double Writes, double Lost);

    /// <summary>One history of the given shape, deterministic in <paramref name="seed"/>.</summary>
    internal static List<Operation> Generate(Shape shape, int seed)
    {
        var rng = new Random(seed);
        const long Length = 20;
        var events = new List<(long At, int Index)>();
        var ops = new List<Operation>();
        var applies = new List<bool>();
        for (var c = 0; c < shape.Clients; c++)
        {
            var t = (long)rng.Next(0, (int)Length);
            for (var i = 0; i < shape.PerClient; i++)
            {
                var duration = 1 + rng.Next((int)Length);
                var write = rng.NextDouble() < shape.Writes;
                var lost = write && rng.NextDouble() < shape.Lost;
                var at = lost ? t + rng.Next((int)(3 * Length)) : t + rng.Next(duration + 1);
                var op = write
                    ? new Operation(c, OpKind.Put, "k", t, lost ? null : t + duration, Value: string.Create(CultureInfo.InvariantCulture, $"v{c}.{i}"))
                    : new Operation(c, OpKind.Get, "k", t, t + duration);
                events.Add((at, ops.Count));
                ops.Add(op);
                applies.Add(!lost || rng.Next(2) == 0);
                t += (lost ? 2 * Length : duration) + 1 + rng.Next(3);
            }
        }

        string? value = null;
        foreach (var (_, index) in events.OrderBy(e => e.At).ThenBy(e => e.Index))
        {
            var op = ops[index];
            if (op.Kind == OpKind.Put)
            {
                value = applies[index] ? op.Value : value;
            }
            else
            {
                ops[index] = op with { Output = value };
            }
        }

        return ops;
    }

    /// <summary>
    /// The register row's prediction (phase 6): the largest number of operations pending at one
    /// instant, with every indeterminate operation counted as open to the end of the history.
    /// Sabotage S-struct-2 (indeterminate operations not counted as open).
    /// </summary>
    internal static int Width(IReadOnlyList<Operation> ops)
    {
        var edges = new List<(long At, int Delta)>();
        foreach (var op in ops)
        {
            edges.Add((op.Invoke, 1));
            if (op.Response is { } r)
            {
                edges.Add((r + 1, -1));
            }
        }

        int open = 0, widest = 0;
        foreach (var (_, delta) in edges.OrderBy(e => e.At).ThenBy(e => e.Delta))
        {
            open += delta;
            widest = Math.Max(widest, open);
        }

        return widest;
    }

    /// <summary>The second candidate: indeterminate writes overlapping one another in real time (all of them, since none ends).</summary>
    internal static int LostWrites(IReadOnlyList<Operation> ops) => ops.Count(o => o.Kind != OpKind.Get && o.IsIndeterminate);

    /// <summary>
    /// The third candidate: for the read with the most, the distinct values it could have seen: every
    /// write not known to be overwritten before it began (a write is overwritten when another
    /// completed write started after it responded and responded before the read began).
    /// </summary>
    internal static int ReadChoices(IReadOnlyList<Operation> ops)
    {
        var writes = ops.Where(o => o.Kind != OpKind.Get).ToList();
        var most = 0;
        foreach (var read in ops.Where(o => o.Kind == OpKind.Get && !o.IsIndeterminate))
        {
            var candidates = writes.Count(w => w.Invoke < read.Response && !writes.Any(x => x != w && !x.IsIndeterminate && w.Response is { } wr && x.Invoke > wr && x.Response < read.Invoke));
            most = Math.Max(most, candidates + 1);
        }

        return most;
    }
}
