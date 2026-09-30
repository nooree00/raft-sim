using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Core;
using Raft.Kv;
using Raft.Simulation;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>A three-node Raft cluster in the simulator, observed, and the four checkers over it (P3-05 on).</summary>
internal static class Cluster
{
    public const int Nodes = 3;
    public static readonly RaftOptions Options = RaftOptions.Default;

    /// <summary>K = 10 election timeouts (decision 5), measured against the longest timeout.</summary>
    public static readonly long Window = 10 * Options.ElectionTimeoutMax;

    public static (Simulator Sim, ElectionHistory History) Run(ulong seed, long duration, FaultSchedule? schedule = null, int clients = 0, IClientWorkload? workload = null)
    {
        var sim = new Simulator(new SimulationConfig { Duration = duration, Nodes = Nodes, Clients = clients }, ctx => new RaftNode(ctx, Options, new KvStateMachine()), seed, schedule) { Observe = true, Workload = workload };
        sim.Run();
        return (sim, new ElectionHistory(sim.Observations, Nodes));
    }

    /// <summary>The log invariants (P4-01) over one observed execution.</summary>
    public static IReadOnlyList<InvariantResult> CheckLog(Simulator sim, ElectionHistory h) =>
        LogInvariants.All(LogHistory.FromObservations(sim.Observations.ToList(), h, Nodes));

    /// <summary>The four invariants over one execution; the stable suffix starts at <paramref name="stableFrom"/>.</summary>
    public static IReadOnlyList<InvariantResult> Check(ElectionHistory h, long stableFrom, long end) =>
    [
        ElectionInvariants.ElectionSafety(h, Options.ElectionTimeoutMin),
        ElectionInvariants.VoteUniqueness(h),
        ElectionInvariants.TermMonotonicity(h),
        ElectionInvariants.Liveness(h, stableFrom, end, Window, 2 * Window),
    ];
}

/// <summary>
/// P3-05: the election in the simulator, fault-free. Every invariant holds in every run, and the
/// runs must show the mechanism: an election in each, a leader acting. The share of terms with a
/// candidate but no winner is written beside the assembly (P3-05's prediction: under 5%).
/// </summary>
public sealed class ElectionSimulationTests
{
    private const int Runs = 200;
    private const long Duration = 7_000;

    [Fact]
    public void FaultFreeClustersElectALeaderAndEveryInvariantHolds()
    {
        long candidateTerms = 0, leaderTerms = 0, checkedLiveness = 0;
        var timeToLeader = new List<long>();
        for (var seed = 1UL; seed <= Runs; seed++)
        {
            var (_, h) = Cluster.Run(seed, Duration);
            var results = Cluster.Check(h, stableFrom: 0, end: Duration);
            foreach (var r in results)
            {
                Assert.True(r.Holds, $"seed {seed}, {r.Invariant}: {string.Join("; ", r.Violations)}");
            }

            var safety = results[0];
            Assert.True(safety.Count("elections") >= 1 && safety.Count("acting-leaders") >= 1, $"seed {seed}: no leader was elected and acted");
            Assert.Equal(0, h.Undecodable);
            candidateTerms += safety.Count("terms-with-a-candidate");
            leaderTerms += safety.Count("terms-with-a-leader");
            checkedLiveness += results[3].Count("checked");
            timeToLeader.Add(results[3].Count("time-to-leader"));
        }

        Assert.Equal(Runs, checkedLiveness);
        var split = candidateTerms - leaderTerms;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "election-fault-free.txt"), string.Join("\n",
            $"{Runs} fault-free runs of {Duration} ticks",
            $"terms with a candidate: {candidateTerms}; with a winner: {leaderTerms}; without: {split} ({(100.0 * split / candidateTerms).ToString("F1", CultureInfo.InvariantCulture)}%)",
            $"time to first acting leader: min {timeToLeader.Min()}, max {timeToLeader.Max()}, mean {timeToLeader.Average().ToString("F0", CultureInfo.InvariantCulture)}") + "\n");
    }
}
