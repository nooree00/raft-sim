using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P4-05: committed entries survive every crash schedule, including all nodes crashing at once
/// (spec §11, invariant 6), and recovery runs on logs whose writes were in flight. Each run has
/// clients writing throughout; half the seeds crash every node at once, half a majority, at a time
/// spread over the run, with writes in flight lost; all restart 300 ticks later. Vacuity risk: a crash
/// with nothing committed, or with no log write in flight, proves nothing about durability; guarded
/// by requiring entries committed in fact before the crash in every run, and crashes that caught a
/// log write in flight in at least the floor of runs. Sabotages S-dur-1, S-dur-2; the leader's persist-before-send
/// order (S-dur-3) is out of these runs' reach and is held by a unit test in <see cref="ReplicationTests"/>.
/// </summary>
public sealed class DurabilityTests
{
    private const int Runs = 100;
    private const long Duration = 5_000;

    [Fact]
    public void CommittedEntriesSurviveEveryNodeOrAMajorityCrashingAtOnce()
    {
        var violations = new Dictionary<string, int>(StringComparer.Ordinal);
        int caughtInFlight = 0, committedBefore = 0;
        string? first = null;
        long committed = 0;
        for (var seed = 1UL; seed <= Runs; seed++)
        {
            var at = 1_500 + ((long)seed * 37 % 1_500);
            Fault crash = seed % 2 == 0 ? new CrashAll(at, at + 300) : new CrashMajority(at, at + 300);
            var (sim, h) = Cluster.Run(seed, Duration, new FaultSchedule([crash]), clients: 3, workload: new RaftWorkload(300));
            var results = Cluster.CheckLog(sim, h);
            foreach (var r in results.Where(r => !r.Holds))
            {
                violations[r.Invariant] = violations.GetValueOrDefault(r.Invariant) + 1;
                first ??= $"seed {seed} ({crash}), {r.Invariant}: {string.Join("; ", r.Violations.Take(2))}";
            }

            // Entries committed in fact before the crash: the analysis of the run cut at the crash.
            var cut = sim.Observations.TakeWhile(o => o.Time < at).ToList();
            var before = LogInvariants.All(LogHistory.FromObservations(cut, new ElectionHistory(cut, Cluster.Nodes), Cluster.Nodes))[0].Count("entries-committed");
            Assert.True(before > 0, $"seed {seed}: nothing was committed before the crash at {at}");
            committedBefore++;
            committed += results[0].Count("entries-committed");

            // A log write in flight at the crash: issued, not completed, by a node that crashed.
            var crashedAt = sim.Observations.OfType<CrashObservation>().Where(c => c.Time == at).Select(c => c.Node).ToHashSet();
            var inFlight = crashedAt.Any(n =>
                sim.Observations.Count(o => o.Time < at && o.Node == n && o is IssuedObservation { Op.File: EntryLog.FileName })
                > sim.Observations.Count(o => o.Time <= at && o.Node == n && o is DurableObservation { File: EntryLog.FileName, Completed: not null }));
            caughtInFlight += inFlight ? 1 : 0;
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "durability.txt"), string.Join("\n",
            $"{Runs} runs of {Duration} ticks, 3 clients; even seeds crash every node at once, odd seeds a majority, at 1500..2999, all back 300 ticks later",
            $"entries committed before the crash in {committedBefore} runs; {committed} committed by the end",
            $"crashes that caught a log write in flight: {caughtInFlight} ({Coverage.Rate(caughtInFlight, Runs)})",
            "violations by invariant: " + (violations.Count == 0 ? "none" : string.Join(", ", violations.Select(v => $"{v.Key} {v.Value}")))) + "\n");
        Assert.True(violations.Count == 0, $"runs with a violation, by invariant: {string.Join(", ", violations.Select(v => $"{v.Key} {v.Value}"))}; first: {first}");
        Assert.True(caughtInFlight >= Coverage.FloorFor(Runs), $"only {caughtInFlight} of {Runs} crashes caught a log write in flight: recovery from an interrupted write was barely exercised");
    }

    /// <summary>
    /// The constructed case: a node claims an entry committed, and every node crashes one tick later,
    /// writes in flight lost; all restart. Each seed runs once without faults to find the first claim
    /// (an apply) at or after 1500, then again with the crash placed after it. The rerun must make the
    /// same claim before the crash, or the construction missed.
    /// </summary>
    [Fact]
    public void EveryNodeCrashingRightAfterACommitClaimLosesNothingCommitted()
    {
        const int constructed = 50;
        var violations = new Dictionary<string, int>(StringComparer.Ordinal);
        string? first = null;
        for (var seed = 1UL; seed <= constructed; seed++)
        {
            var (probe, _) = Cluster.Run(seed, Duration, FaultSchedule.Empty, clients: 3, workload: new RaftWorkload(300));
            var claim = FirstClaim(probe.Observations);
            Assert.True(claim is not null, $"seed {seed}: no node applied an entry at or after 1500");
            var at = claim!.Time + 1;
            var (sim, h) = Cluster.Run(seed, Duration, new FaultSchedule([new CrashAll(at, at + 300)]), clients: 3, workload: new RaftWorkload(300));
            var again = FirstClaim(sim.Observations);
            Assert.True(
                again is not null && again.Time == claim.Time && again.Node == claim.Node && again.Event.Fields.SequenceEqual(claim.Event.Fields),
                $"seed {seed}: the run with the crash did not make the probe's claim at {claim.Time} first; the construction missed");
            foreach (var r in Cluster.CheckLog(sim, h).Where(r => !r.Holds))
            {
                violations[r.Invariant] = violations.GetValueOrDefault(r.Invariant) + 1;
                first ??= $"seed {seed} (crash all at {at}), {r.Invariant}: {string.Join("; ", r.Violations.Take(2))}";
            }
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "durability-constructed.txt"),
            $"{constructed} constructed runs, every node crashing one tick after the first commit claim at or after 1500; violations by invariant: "
            + (violations.Count == 0 ? "none" : string.Join(", ", violations.Select(v => $"{v.Key} {v.Value}"))) + "\n");
        Assert.True(violations.Count == 0, $"constructed runs with a violation, by invariant: {string.Join(", ", violations.Select(v => $"{v.Key} {v.Value}"))}; first: {first}");
    }

    private static EmittedObservation? FirstClaim(IEnumerable<Observation> observations) =>
        observations.OfType<EmittedObservation>().FirstOrDefault(o => o.Time >= 1_500 && o.Event.Name == "apply");
}
