using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P3-06: the §6 disruption rule under a constructed one-way partition, n3 able to send to its
/// peers and receive from neither. Vacuity risk: a run where no disruptive RequestVote ever arrives
/// inside the window passes with the rule deleted; guarded by counting the RequestVotes the rule
/// ignored, and by requiring that no leader is deposed after the first window: invariant 11 in its
/// phase-3 form (a leader within the window) holds with the rule off too, because each deposed
/// leader is replaced in time. The same seeds with the rule off are measured for the record.
/// Sabotages S-disrupt-1, S-disrupt-2.
/// </summary>
public sealed class DisruptionSimulationTests
{
    private const int Runs = 200;
    private const long PartitionAt = 1_000, Duration = 20_000;
    private static readonly NodeId N1 = new(1), N2 = new(2), N3 = new(3);

    private static FaultSchedule OneWay() => new([new Partition(PartitionAt, N1, N3), new Partition(PartitionAt, N2, N3)]);

    private static (InvariantResult Continuity, long Ignored, bool SafetyHolds) Run(ulong seed, bool rule)
    {
        var options = Cluster.Options with { DisruptionRule = rule };
        var sim = new Simulator(new SimulationConfig { Duration = Duration, Nodes = Cluster.Nodes }, ctx => new RaftNode(ctx, options), seed, OneWay()) { Observe = true };
        sim.Run();
        var h = new ElectionHistory(sim.Observations, Cluster.Nodes);
        var ignored = sim.Trace.Lines.Count(l => l.Contains(" EVENT name=requestvote-ignored", StringComparison.Ordinal));
        var safe = ElectionInvariants.ElectionSafety(h, Cluster.Options.ElectionTimeoutMin).Holds && ElectionInvariants.VoteUniqueness(h).Holds && ElectionInvariants.TermMonotonicity(h).Holds;
        return (ElectionInvariants.LeaderContinuity(h, PartitionAt + Cluster.Window, Duration, Cluster.Window), ignored, safe);
    }

    [Fact]
    public void WithTheRuleANodeThatCannotHearTheLeaderDoesNotDeposeIt()
    {
        long ignored = 0, leadersOn = 0, leadersOff = 0, failOn = 0, failOff = 0;
        for (var seed = 1UL; seed <= Runs; seed++)
        {
            var on = Run(seed, rule: true);
            Assert.True(on.SafetyHolds, $"seed {seed}: a safety invariant failed under the one-way partition");
            Assert.True(on.Continuity.Holds, $"seed {seed}: {string.Join("; ", on.Continuity.Violations)}");
            // The rule's own effect: once a leader exists, n3's candidacy never deposes it. Leader
            // continuity alone does not see disruption, since a leader deposed every few hundred
            // ticks is replaced in time (measured below with the rule off).
            Assert.True(on.Continuity.Count("leaders-elected") == 0, $"seed {seed}: the leader was deposed {on.Continuity.Count("leaders-elected")} time(s) by a node that cannot hear it");
            ignored += on.Ignored;
            leadersOn += on.Continuity.Count("leaders-elected");
            failOn += on.Continuity.Holds ? 0 : 1;

            var off = Run(seed, rule: false);
            leadersOff += off.Continuity.Count("leaders-elected");
            failOff += off.Continuity.Holds ? 0 : 1;
        }

        Assert.True(ignored > Runs, $"the rule ignored only {ignored} RequestVotes over {Runs} runs: n3's requests rarely reached a follower inside the window");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "disruption.txt"), string.Join("\n",
            $"{Runs} runs, n3 hears nothing from {PartitionAt} to {Duration}",
            $"rule on:  leader-continuity failures {failOn}, leaders elected after the first window {leadersOn}, RequestVotes ignored {ignored}",
            $"rule off: leader-continuity failures {failOff}, leaders elected after the first window {leadersOff}",
            $"leaders elected per run: on {(leadersOn / (double)Runs).ToString("F1", CultureInfo.InvariantCulture)}, off {(leadersOff / (double)Runs).ToString("F1", CultureInfo.InvariantCulture)}") + "\n");
    }
}
