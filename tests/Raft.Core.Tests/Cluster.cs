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

    /// <param name="spares">Nodes beyond the initial three, outside the configuration until a membership change adds them (P6-08).</param>
    public static (Simulator Sim, ElectionHistory History) Run(ulong seed, long duration, FaultSchedule? schedule = null, int clients = 0, IClientWorkload? workload = null, int spares = 0)
    {
        var sim = new Simulator(new SimulationConfig { Duration = duration, Nodes = Nodes + spares, Members = Nodes, Clients = clients }, ctx => new RaftNode(ctx, Options, new KvStateMachine()), seed, schedule) { Observe = true, Workload = workload };
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
