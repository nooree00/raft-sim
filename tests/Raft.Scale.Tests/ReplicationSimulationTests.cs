using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Core.Tests;
using Raft.Simulation;
using Xunit;

namespace Raft.Scale.Tests;

/// <summary>
/// P4-03 in the simulator: three nodes with clients, each node in turn unable to send and then crashed,
/// so that leadership changes and old leaders' suffixes conflict, and every invariant, election and
/// log, holding in every run. Vacuity risk: a run in which
/// one leader holds office throughout satisfies every log invariant trivially (one term, no
/// conflicts); guarded by requiring commits in at least two terms in every run, and a conflicting
/// suffix truncated in some run of the sample. Sabotages S-repl-1..6 run here too.
/// </summary>
public sealed class ReplicationSimulationTests
{
    private const int Runs = 100;
    private const long Duration = 8_000;
    private const long Healed = 3_000;
    private static readonly NodeId N1 = new(1), N2 = new(2), N3 = new(3);

    /// <summary>
    /// Each node in turn cannot send to the others for 700 ticks, then crashes once. A leader that
    /// cannot reach its followers still hears clients and appends entries that cannot commit, and a new
    /// leader is elected: on healing its suffix conflicts and must be truncated. The crashes restart
    /// nodes from disk mid-run.
    /// </summary>
    private static FaultSchedule EachNodeCutOffAndCrashed()
    {
        var faults = new List<Fault>();
        var nodes = new[] { N1, N2, N3 };
        for (var k = 0; k < 3; k++)
        {
            var n = nodes[k];
            var at = 1_000 + (2_000 * k);
            foreach (var other in nodes.Where(o => o != n))
            {
                faults.Add(new Partition(at, n, other));
                faults.Add(new Heal(at + 700, n, other));
            }

            faults.Add(new Crash(at + 1_200, n, DiskLoss.Pending));
            faults.Add(new Restart(at + 1_500, n));
        }

        return new FaultSchedule(faults.OrderBy(f => f.At).ToList());
    }

    [Fact]
    public void EveryInvariantHoldsAndEachRunCommitsInMoreThanOneTerm()
    {
        long committed = 0, truncated = 0, applies = 0, claims = 0, minTerms = long.MaxValue;
        var runsWithTruncation = 0;
        for (var seed = 1UL; seed <= Runs; seed++)
        {
            var (sim, h) = Cluster.Run(seed, Duration, EachNodeCutOffAndCrashed(), clients: 3, workload: new RaftWorkload(400));
            foreach (var r in Cluster.Check(h, stableFrom: 6_500, end: Duration).Concat(Cluster.CheckLog(sim, h)))
            {
                Assert.True(r.Holds, $"seed {seed}, {r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
            }

            var log = Cluster.CheckLog(sim, h)[0];
            Assert.True(log.Count("terms-with-a-commit") >= 2, $"seed {seed}: commits in {log.Count("terms-with-a-commit")} term(s); the run did not exercise a change of leader");
            minTerms = Math.Min(minTerms, log.Count("terms-with-a-commit"));
            committed += log.Count("entries-committed");
            truncated += log.Count("suffixes-truncated");
            applies += log.Count("applies");
            claims += log.Count("claims");
            runsWithTruncation += log.Count("suffixes-truncated") > 0 ? 1 : 0;
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "replication.txt"), string.Join("\n",
            $"{Runs} runs of {Duration} ticks, 3 clients to random nodes, each node in turn cut off from sending for 700 ticks, then crashed",
            $"entries committed {committed} (mean {committed / Runs}), applies {applies}, claims {claims}",
            $"terms with a commit: at least {minTerms} in every run",
            $"conflicting suffixes truncated {truncated}, in {runsWithTruncation} runs") + "\n");
        Assert.True(runsWithTruncation >= 1, "no run truncated a conflicting suffix: the sample never exercised conflict resolution");
    }

    /// <summary>
    /// P4-03's prediction, measured: a follower isolated while the leader commits many entries catches
    /// up, and the ticks it takes are written beside the assembly. From phase 7 the leader has
    /// compacted past the follower's log by then, and the follower catches up by installing its
    /// snapshot (P7-07). Fifteen clients, not twelve, from phase 8: a `Get` is answered without an
    /// entry (P8-05), and twelve left the follower 964 entries behind. Twenty from P11-02: with one
    /// append in flight per follower, a write arriving while one is outstanding waits for its answer,
    /// so fifteen closed-loop clients commit fewer entries in the window (766 behind).
    /// </summary>
    [Fact]
    public void AnIsolatedFollowerCatchesUpAfterTheLeaderCommitsManyEntries()
    {
        const ulong seed = 1;
        var (probe, ph) = Cluster.Run(seed, 1_000);
        var leader = ph.Elections().OrderBy(e => e.Value).Select(e => e.Key.Candidate).First();
        var follower = new[] { N1, N2, N3 }.First(n => n != leader);
        var (sim, h) = Cluster.Run(seed, 5_000, new FaultSchedule([new Isolate(1_000, follower, Healed)]), clients: 20, workload: new RaftWorkload(1_000, target: leader));
        foreach (var r in Cluster.CheckLog(sim, h))
        {
            Assert.True(r.Holds, $"{r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
        }

        // Each node's durable log length after every change, replayed from the completed writes: the
        // snapshot's index and the entries after it, a file renamed onto the log read whole (P7-06:
        // from phase 7 on the follower catches up by installing the leader's snapshot).
        var views = new Dictionary<NodeId, LogHistory.FileView>();
        var others = new Dictionary<(NodeId, string), byte[]>();
        var lengths = new List<(long Time, NodeId Node, long Length)>();
        foreach (var d in sim.Observations.OfType<DurableObservation>())
        {
            var v = views.TryGetValue(d.Node, out var x) ? x : views[d.Node] = new LogHistory.FileView();
            switch (d.Completed)
            {
                case PersistRename r when r.To == EntryLog.FileName:
                    v.Replace(others.Remove((d.Node, r.File), out var renamed) ? renamed : []);
                    break;
                case { } op when op.File == EntryLog.FileName:
                    v.Apply(op);
                    break;
                case PersistAppend a:
                    others[(d.Node, a.File)] = [.. others.GetValueOrDefault((d.Node, a.File), []), .. a.Data.Span];
                    continue;
                case PersistWriteAt w:
                    var old = others.GetValueOrDefault((d.Node, w.File), []);
                    var grown = new byte[Math.Max(old.Length, w.Offset + w.Data.Length)];
                    old.CopyTo(grown, 0);
                    w.Data.Span.CopyTo(grown.AsSpan((int)w.Offset));
                    others[(d.Node, w.File)] = grown;
                    continue;
                case null when d.File == EntryLog.FileName:
                    v.Replace(d.Content?.ToArray() ?? []);
                    break;
                default:
                    continue;
            }

            lengths.Add((d.Time, d.Node, (v.Snapshot?.Index ?? 0) + v.Count));
        }

        long LengthAt(NodeId n, long t) => lengths.Where(l => l.Node == n && l.Time <= t).Select(l => l.Length).DefaultIfEmpty(0).Last();
        var behind = LengthAt(leader, Healed) - LengthAt(follower, Healed);
        var target = LengthAt(leader, Healed);
        var caughtUp = lengths.Where(l => l.Node == follower && l.Time >= Healed && l.Length >= target).Select(l => l.Time).DefaultIfEmpty(long.MaxValue).First();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "catch-up.txt"),
            $"leader {leader}, follower {follower} isolated 1000..{Healed}; behind by {behind} entries when healed; caught up to {target} at {(caughtUp == long.MaxValue ? "never" : caughtUp.ToString(CultureInfo.InvariantCulture))}, {(caughtUp == long.MaxValue ? "-" : (caughtUp - Healed).ToString(CultureInfo.InvariantCulture))} ticks after healing\n");
        Assert.True(behind >= 1_000, $"the follower was only {behind} entries behind: the measurement needs at least 1000");
        Assert.True(caughtUp < 5_000, "the isolated follower never caught up");
    }
}
