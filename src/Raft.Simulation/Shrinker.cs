using System;
using System.Collections.Generic;
using System.Linq;

namespace Raft.Simulation;

/// <summary>
/// Reduces a failing schedule to a small one that fails the same way (spec §7). A candidate counts
/// only if its run shows the target failure signature — the property that failed and the node it
/// failed at — never merely "some failure": a shrinker that accepts any failure converges on
/// whatever fails most easily, which may be another bug or a runner crash.
/// </summary>
public static class Shrinker
{
    /// <summary>
    /// The outcome. <see cref="OrderSensitive"/> lists the adjacent pairs (indices into
    /// <see cref="Schedule"/>) whose two times, exchanged, make the failure go away: removal can find
    /// the events in their failing order, but only this probe says the order is part of the cause.
    /// </summary>
    public sealed record Result(FaultSchedule Schedule, string Signature, int Runs, bool OneMinimal, IReadOnlyList<(int First, int Second)> OrderSensitive);

    /// <param name="failing">A schedule whose run shows <paramref name="signature"/>.</param>
    /// <param name="signature">The failure to preserve.</param>
    /// <param name="signaturesOf">Runs a schedule and returns the failure signatures its run shows.</param>
    public static Result Shrink(FaultSchedule failing, string signature, Func<FaultSchedule, IReadOnlySet<string>> signaturesOf)
    {
        ArgumentNullException.ThrowIfNull(failing);
        ArgumentNullException.ThrowIfNull(signaturesOf);
        var runs = 0;
        bool Fails(IReadOnlyList<Fault> faults)
        {
            runs++;
            return signaturesOf(new FaultSchedule(faults)).Contains(signature);
        }

        if (!Fails(failing.Faults))
        {
            throw new ArgumentException($"the schedule does not fail with '{signature}'", nameof(failing));
        }

        var kept = Ddmin(failing.Faults.ToList(), Fails);
        kept = ShrinkParameters(kept, Fails);
        var oneMinimal = Enumerable.Range(0, kept.Count).All(i => !Fails(Without(kept, i)));
        var ordered = new List<(int, int)>();
        for (var i = 0; i + 1 < kept.Count; i++)
        {
            if (!Fails(Swapped(kept, i)))
            {
                ordered.Add((i, i + 1));
            }
        }

        return new Result(new FaultSchedule(kept), signature, runs, oneMinimal, ordered);
    }

    /// <summary>
    /// Zeller's ddmin: try each of n chunks alone, then each complement; on success restart from the
    /// smaller list, otherwise double n. Ends when no single event can be removed (n = length).
    /// Kept events keep their relative order.
    /// </summary>
    private static List<Fault> Ddmin(List<Fault> faults, Func<IReadOnlyList<Fault>, bool> fails)
    {
        var n = 2;
        while (faults.Count >= 2)
        {
            var chunks = Split(faults, n);
            var reduced = false;
            foreach (var chunk in chunks)
            {
                if (fails(chunk))
                {
                    (faults, n, reduced) = (chunk, 2, true);
                    break;
                }
            }

            if (!reduced)
            {
                for (var i = 0; i < chunks.Count; i++)
                {
                    var complement = chunks.Where((_, j) => j != i).SelectMany(c => c).ToList();
                    if (fails(complement))
                    {
                        (faults, n, reduced) = (complement, Math.Max(n - 1, 2), true);
                        break;
                    }
                }
            }

            if (!reduced)
            {
                if (n >= faults.Count)
                {
                    break;
                }

                n = Math.Min(n * 2, faults.Count);
            }
        }

        if (faults.Count == 1 && fails([]))
        {
            faults = [];
        }

        return faults;
    }

    private static List<List<Fault>> Split(List<Fault> faults, int n)
    {
        var chunks = new List<List<Fault>>();
        var start = 0;
        for (var i = 0; i < n; i++)
        {
            var end = start + ((faults.Count - start) / (n - i));
            chunks.Add(faults.GetRange(start, end - start));
            start = end;
        }

        return chunks.Where(c => c.Count > 0).ToList();
    }

    /// <summary>
    /// Each remaining event's parameters, one at a time, toward the simplest value that still fails:
    /// the mildest disk loss, shorter extra delays and slow-disk windows, an unskewed clock. Times are
    /// not moved — moving one can change which message a link fault meets.
    /// </summary>
    private static List<Fault> ShrinkParameters(List<Fault> faults, Func<IReadOnlyList<Fault>, bool> fails)
    {
        for (var i = 0; i < faults.Count; i++)
        {
            foreach (var simpler in Simpler(faults[i]))
            {
                var candidate = faults.ToList();
                candidate[i] = simpler;
                if (fails(candidate))
                {
                    faults = candidate;
                    i--; // shrink the same event further
                    break;
                }
            }
        }

        return faults;
    }

    /// <summary>
    /// Strictly simpler variants of a fault, simplest first. A list, not an iterator: the compiler's
    /// iterator state machine references System.Environment, which the ambient scan rejects.
    /// </summary>
    private static List<Fault> Simpler(Fault f)
    {
        var simpler = new List<Fault>();
        switch (f)
        {
            case Crash c when c.Loss != DiskLoss.Pending:
                simpler.Add(c with { Loss = DiskLoss.Pending });
                if (c.Loss > DiskLoss.Torn)
                {
                    simpler.Add(c with { Loss = DiskLoss.Torn });
                }

                break;
            case Delay d when d.Extra > 1:
                simpler.Add(d with { Extra = d.Extra / 2 });
                break;
            case SlowDisk s:
                if (s.Latency > 1)
                {
                    simpler.Add(s with { Latency = s.Latency / 2 });
                }

                if (s.Until - s.At > 1)
                {
                    simpler.Add(s with { Until = s.At + ((s.Until - s.At) / 2) });
                }

                break;
            case BarrierViolation b when b.Until - b.At > 1:
                simpler.Add(b with { Until = b.At + ((b.Until - b.At) / 2) });
                break;
            case Skew k when k.Numerator != k.Denominator:
                simpler.Add(k with { Numerator = k.Denominator });
                break;
            case CrashWhenInFlight c:
                if (c.Loss != DiskLoss.Pending)
                {
                    simpler.Add(c with { Loss = DiskLoss.Pending });
                }

                if (c.MinPending > 1)
                {
                    simpler.Add(c with { MinPending = c.MinPending - 1 });
                }

                break;
        }

        return simpler;
    }

    private static List<Fault> Without(List<Fault> faults, int i) => faults.Where((_, j) => j != i).ToList();

    /// <summary>
    /// Events i and i+1 in the opposite order: their times exchanged (the runner orders faults by
    /// time, so reordering the list alone would change nothing), and the list kept sorted.
    /// </summary>
    private static List<Fault> Swapped(List<Fault> faults, int i)
    {
        var (a, b) = (faults[i], faults[i + 1]);
        var result = faults.ToList();
        result[i] = Retimed(b, a.At);
        result[i + 1] = Retimed(a, b.At);
        return result;
    }

    /// <summary>The fault moved to <paramref name="at"/>; a window it opens moves with it.</summary>
    private static Fault Retimed(Fault f, long at) => f switch
    {
        SlowDisk s => s with { At = at, Until = s.Until - s.At + at },
        BarrierViolation b => b with { At = at, Until = b.Until - b.At + at },
        Isolate i => i with { At = at, Until = i.Until - i.At + at },
        CrashAll a => a with { At = at, Until = a.Until - a.At + at },
        CrashMajority m => m with { At = at, Until = m.Until - m.At + at },
        _ => f with { At = at },
    };
}
