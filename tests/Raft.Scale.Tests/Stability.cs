using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Simulation;

namespace Raft.Scale.Tests;

/// <summary>The stable suffix of a generated execution (P3-08), shared by the soak and the membership sample (P6-08).</summary>
internal static class Stability
{
    /// <summary>
    /// When the last fault's effect ends: heals, restarts, unpauses and "until"s from the schedule, and
    /// the last observed restart, which covers the state-placed crashes (their firing time is not in
    /// the schedule). Clock skew and FIFO links are conditions, not faults that heal.
    /// </summary>
    internal static long StableFrom(FaultSchedule schedule, IEnumerable<Observation> observations)
    {
        var f = schedule.Faults;
        long After<T>(Fault x, Func<T, bool> match)
            where T : Fault => f.OfType<T>().Where(y => y.At >= x.At && match(y)).Select(y => y.At).DefaultIfEmpty(long.MaxValue).Min();
        var end = 0L;
        foreach (var x in f)
        {
            end = Math.Max(end, x switch
            {
                Partition p => After<Heal>(p, h => h.From == p.From && h.To == p.To),
                Crash c => After<Restart>(c, r => r.Node == c.Node),
                Pause p => After<Unpause>(p, u => u.Node == p.Node),
                Isolate i => i.Until,
                CrashAll a => a.Until,
                CrashMajority m => m.Until,
                SlowDisk s => s.Until,
                BarrierViolation b => b.Until,
                Delay d => d.At + d.Extra,
                Skew or Fifo => 0,
                _ => x.At,
            });
        }

        var restarts = observations.OfType<StartObservation>().Where(s => s.Incarnation > 1).Select(s => s.Time);
        return Math.Max(end, restarts.DefaultIfEmpty(0).Max());
    }
}
