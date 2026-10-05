using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// A node that every 200 ticks replaces a file the way compaction will (spec §8, phase 7 decision 2):
/// it writes a fresh, uniquely named file and renames it over the real one. Its writes are slow, so a
/// write is still in flight whenever the rename is emitted.
/// </summary>
internal sealed class RenamingNode : INode
{
    private long _since;
    private long _round;

    public IReadOnlyList<Effect> Handle(Input input)
    {
        if (input is not Tick t)
        {
            return [];
        }

        _since += t.Elapsed;
        if (_since < 200)
        {
            return [];
        }

        _since = 0;
        _round++;
        var name = "snap." + _round.ToString(CultureInfo.InvariantCulture);
        return [new PersistAppend(name, Encoding.ASCII.GetBytes("round" + _round.ToString(CultureInfo.InvariantCulture))), new PersistRename(name, "snap")];
    }
}

/// <summary>
/// P7-00: the rename barrier (phase 7 decision 1). The world holds a rename until every write the node
/// emitted before it is durable, the fsync before the rename that the node cannot do, and the disk
/// models a rename whose source data was lost as an empty file under the real name, the state a real
/// file system can leave. Vacuity risks: a rename never emitted with a write in flight never meets
/// the barrier (guarded: the node's writes are slow, and the test counts renames emitted with a write
/// in flight); a barrier that holds renames forever passes every safety check (guarded: the real
/// file must end holding the last round). Sabotages S-barrier-1, S-barrier-2.
/// </summary>
public sealed class RenameBarrierTests
{
    private static readonly NodeId N1 = new(1);

    private static Simulator Run(ulong seed, bool early, params Fault[] faults) =>
        new(new SimulationConfig { Nodes = 1, Duration = 6_000, ReleaseRenamesEarly = early }, _ => new RenamingNode(), seed,
            new FaultSchedule([new SlowDisk(0, N1, 120, 1_000_000), .. faults])) { Observe = true };

    /// <summary>Every rename is issued after every write issued before it is durable, and the real file ends holding the last round.</summary>
    [Fact]
    public void ARenameWaitsForEveryEarlierWriteAndThenHappens()
    {
        var sim = Run(1, early: false);
        sim.Run();
        var lines = TraceLine.Parse(sim.Trace.Lines);
        var durableAt = lines.Where(l => l.Kind == "DURABLE").ToDictionary(l => long.Parse(l.Fields["seq"], CultureInfo.InvariantCulture), l => l.Time);
        var renames = lines.Where(l => l.Kind == "PERSIST" && l.Fields["op"] == nameof(PersistRename)).ToList();
        Assert.True(renames.Count >= 10, "too few renames: " + renames.Count);

        foreach (var r in renames)
        {
            var seq = long.Parse(r.Fields["seq"], CultureInfo.InvariantCulture);
            for (var earlier = 1L; earlier < seq; earlier++)
            {
                Assert.True(durableAt.TryGetValue(earlier, out var at) && at <= r.Time, $"rename {seq} issued at {r.Time} before write {earlier} was durable");
            }
        }

        var rounds = sim.Observations.OfType<IssuedObservation>().Count(o => o.Op is PersistRename);
        var snap = Encoding.ASCII.GetString(sim.Disks[0].Snapshot()["snap"].Span);
        Assert.StartsWith("round", snap, StringComparison.Ordinal);
        Assert.True(int.Parse(snap["round".Length..], CultureInfo.InvariantCulture) >= rounds - 1, $"the real file holds {snap} after {rounds} renames: renames are held, not released");
    }

    /// <summary>
    /// Crashes with two writes in flight and a reordered loss, over many seeds: with the barrier the
    /// real file is never left empty; with renames released early (the positive control) it is, in
    /// some, because a crash kept a rename and lost the write it renames.
    /// </summary>
    [Fact]
    public void WithoutTheBarrierACrashLeavesAnEmptyFileUnderTheRealName()
    {
        (int Empty, int RenameInFlight) Count(bool early)
        {
            int empty = 0, inFlight = 0;
            for (var seed = 1UL; seed <= 60; seed++)
            {
                var sim = Run(seed, early, new CrashWhenInFlight(1_000 + (long)seed * 37, N1, 2, DiskLoss.Reordered, 50));
                sim.Run();
                var observations = sim.Observations;
                var crash = observations.Select((o, i) => (o, i)).Where(x => x.o is CrashObservation).Select(x => x.i).DefaultIfEmpty(-1).First();
                if (crash < 0)
                {
                    continue;
                }

                var pendingOps = observations.Take(crash).OfType<IssuedObservation>().Select(o => o.Op).ToList();
                var completed = observations.Take(crash).OfType<DurableObservation>().Count(o => o.Completed is not null);
                inFlight += pendingOps.Skip(completed).Any(op => op is PersistRename) ? 1 : 0;
                var after = observations.Skip(crash).OfType<DurableObservation>().FirstOrDefault(o => o.Completed is null && o.File == "snap");
                empty += after is { Content: { Length: 0 } } ? 1 : 0;
            }

            return (empty, inFlight);
        }

        var (withBarrier, inFlightWithBarrier) = Count(early: false);
        var (withoutBarrier, _) = Count(early: true);

        Assert.True(inFlightWithBarrier > 0, "no crash met a rename in flight: the barrier was never tested");
        Assert.Equal(0, withBarrier);
        Assert.True(withoutBarrier > 0, "with renames released early no crash left the real file empty: the control shows nothing");
    }
}
