using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Core;
using Raft.Kv;
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
/// P4-06 adds clients, the log checkers and invariant 11's commit clause, over 100 of P3-06's
/// 200 seeds. Sabotages S-disrupt-1, S-disrupt-2, S-disrupt-3.
/// </summary>
public sealed class DisruptionSimulationTests
{
    private const int Runs = 100;
    private const long PartitionAt = 1_000, Duration = 20_000;
    private static readonly NodeId N1 = new(1), N2 = new(2), N3 = new(3);

    private static FaultSchedule OneWay() => new([new Partition(PartitionAt, N1, N3), new Partition(PartitionAt, N2, N3)]);

    private static (InvariantResult Continuity, InvariantResult Commit, long Ignored, string? Unsafe, bool N3Led) Run(ulong seed, bool rule)
    {
        var options = Cluster.Options with { DisruptionRule = rule };
        var sim = new Simulator(new SimulationConfig { Duration = Duration, Nodes = Cluster.Nodes, Clients = 3 }, ctx => new RaftNode(ctx, options, new KvStateMachine()), seed, OneWay())
        {
            Observe = true,
            Workload = new RaftWorkload((int)Duration),
        };
        sim.Run();
        var observations = sim.Observations.ToList();
        var h = new ElectionHistory(observations, Cluster.Nodes);
        var ignored = sim.Trace.Lines.Count(l => l.Contains(" EVENT name=requestvote-ignored", StringComparison.Ordinal));
        var log = new LogAnalysis(LogHistory.FromObservations(observations, h, Cluster.Nodes));
        var failed = new[] { ElectionInvariants.ElectionSafety(h, Cluster.Options.ElectionTimeoutMin), ElectionInvariants.VoteUniqueness(h), ElectionInvariants.TermMonotonicity(h) }
            .Concat(LogAnalysis.Names.Select(log.Result)).FirstOrDefault(r => !r.Holds);
        return (ElectionInvariants.LeaderContinuity(h, PartitionAt + Cluster.Window, Duration, Cluster.Window),
            CommitLiveness.Continuity(observations, log, PartitionAt + Cluster.Window, Duration, Cluster.Window),
            ignored,
            failed is null ? null : $"{failed.Invariant}: {string.Join("; ", failed.Violations.Take(2))}",
            h.Elections().Where(e => e.Value < PartitionAt).MaxBy(e => e.Key.Term.Value).Key.Candidate == N3);
    }

    [Fact]
    public void WithTheRuleANodeThatCannotHearTheLeaderDoesNotDeposeIt()
    {
        long ignored = 0, leadersOn = 0, leadersOff = 0, failOn = 0, failOff = 0, commitFailOn = 0, commitFailOff = 0, committedOn = 0, committedOff = 0, n3Led = 0, commitFailN3Led = 0, commitFailOffN3Led = 0;
        for (var seed = 1UL; seed <= Runs; seed++)
        {
            var on = Run(seed, rule: true);
            Assert.True(on.Unsafe is null, $"seed {seed}: a safety invariant failed under the one-way partition: {on.Unsafe}");
            Assert.True(on.Continuity.Holds, $"seed {seed}: {string.Join("; ", on.Continuity.Violations)}");
            // A leader cut off this way (n3 led when the partition began) still sends: its followers
            // stay loyal and never hear its acknowledgements reach it, so nothing commits while the
            // partition lasts. Only CheckQuorum ends that, and spec §2 leaves it out; invariant 11
            // requires a commit only after faults heal. Those seeds are counted, not asserted.
            Assert.True(on.N3Led || on.Commit.Holds, $"seed {seed}: {string.Join("; ", on.Commit.Violations)}");
            // The rule's own effect: once a leader exists, n3's candidacy never deposes it. Neither
            // leader continuity nor the commit clause (P4-06) sees the disruption: a leader deposed
            // every few hundred ticks is replaced, and commits resumed, in time (measured below with
            // the rule off; S-disrupt-3 holds that the commit clause stays blind to it).
            Assert.True(on.Continuity.Count("leaders-elected") == 0, $"seed {seed}: the leader was deposed {on.Continuity.Count("leaders-elected")} time(s) by a node that cannot hear it");
            n3Led += on.N3Led ? 1 : 0;
            commitFailN3Led += on.N3Led && !on.Commit.Holds ? 1 : 0;
            ignored += on.Ignored;
            leadersOn += on.Continuity.Count("leaders-elected");
            failOn += on.Continuity.Holds ? 0 : 1;
            commitFailOn += on.Commit.Holds ? 0 : 1;
            committedOn += on.Commit.Count("commands-committed");

            var off = Run(seed, rule: false);
            leadersOff += off.Continuity.Count("leaders-elected");
            failOff += off.Continuity.Holds ? 0 : 1;
            commitFailOff += off.Commit.Holds ? 0 : 1;
            commitFailOffN3Led += off.N3Led && !off.Commit.Holds ? 1 : 0;
            committedOff += off.Commit.Count("commands-committed");
        }

        Assert.True(n3Led <= Runs / 2, $"n3 led when the partition began in {n3Led} of {Runs} runs: the commit clause was asserted in too few");
        Assert.True(ignored > Runs, $"the rule ignored only {ignored} RequestVotes over {Runs} runs: n3's requests rarely reached a follower inside the window");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "disruption.txt"), string.Join("\n",
            $"{Runs} runs, n3 hears nothing from {PartitionAt} to {Duration}; 3 clients submitting throughout",
            $"n3 led when the partition began in {n3Led} runs",
            $"rule on:  leader-continuity failures {failOn}, commit-continuity failures {commitFailOn} ({commitFailN3Led} where n3 led), leaders elected after the first window {leadersOn}, RequestVotes ignored {ignored}, commands committed {committedOn}",
            $"rule off: leader-continuity failures {failOff}, commit-continuity failures {commitFailOff} ({commitFailOffN3Led} where n3 led), leaders elected after the first window {leadersOff}, commands committed {committedOff}",
            $"leaders elected per run: on {(leadersOn / (double)Runs).ToString("F1", CultureInfo.InvariantCulture)}, off {(leadersOff / (double)Runs).ToString("F1", CultureInfo.InvariantCulture)}") + "\n");
    }
}
