using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Checker;
using Raft.Core;
using Raft.Core.Tests;
using Raft.Kv;
using Raft.Simulation;
using Xunit;

namespace Raft.Scale.Tests;

/// <summary>
/// P7-08, the done criterion over generated executions: n1 never compacts, n2 and n3 compact every
/// 20 applied entries, under the soak's faults and clients. Every invariant, every client history
/// linearizable, and every node's state agreeing with the committed entries replayed from the empty
/// state (<see cref="AgreementProbe"/>). Vacuity risk: a sample in which no snapshot is ever restored
/// compares only states built by applying entries; guarded by floors on executions with an install
/// and with a restart from a snapshot. Sabotage S-agree-1.
/// </summary>
public sealed class AgreementSimulationTests
{
    private const int Executions = 100;
    private static readonly RaftOptions Compacting = RaftOptions.Default with { SnapshotThreshold = 20 };

    [Fact]
    public void CompactingAndNonCompactingNodesAgreeWithTheCommittedEntries()
    {
        int compared = 0, withInstall = 0, withRestart = 0, linearizable = 0, undecided = 0;
        var failures = new List<string>();
        for (var seed = 1; seed <= Executions; seed++)
        {
            var probe = new AgreementProbe();
            var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = SoakConfig.FaultsUntil });
            var (sim, h) = Cluster.Run((ulong)seed, SoakConfig.Duration, schedule, SoakConfig.Clients, new RaftWorkload(int.MaxValue, retry: true, think: SoakConfig.Think),
                node: ctx => new RaftNode(ctx, ctx.Id.Value == 1 ? AgreementProbe.Uncompacted : Compacting, probe.For(ctx.Id)));
            var observations = sim.Observations.ToList();
            var log = new LogAnalysis(LogHistory.FromObservations(observations, h, Cluster.Nodes));
            foreach (var r in LogAnalysis.Names.Select(log.Result))
            {
                Assert.True(r.Holds, $"seed {seed}, {r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
            }

            var (bad, n) = probe.Check(observations, log);
            failures.AddRange(bad.Select(b => $"seed {seed}: {b}"));
            compared += n;
            var restores = observations.OfType<EmittedObservation>().Where(o => o.Event.Name == "restore").ToList();
            withInstall += restores.Any(r => observations.Any(o => o is IssuedObservation { Op: PersistWriteAt } w && w.Node == r.Node)) ? 1 : 0;
            withRestart += restores.Any(r => observations.OfType<StartObservation>().Any(s => s.Node == r.Node && s.Incarnation > 1)) ? 1 : 0;

            var lin = WglChecker.Check(ClientHistory.From(sim.ClientLog).History, SoakConfig.CheckerBudget);
            Assert.True(lin.Verdict != Verdict.NotLinearizable, $"seed {seed}: not linearizable at key {lin.Key}");
            linearizable += lin.Verdict == Verdict.Linearizable ? 1 : 0;
            undecided += lin.Verdict == Verdict.Undecided ? 1 : 0;
        }

        File.WriteAllText(Path.Combine(System.AppContext.BaseDirectory, "agreement-report.txt"), string.Join("\n",
            $"{Executions} executions, n1 never compacting, n2 and n3 every 20 applied entries",
            $"states compared with the replay: {compared}; disagreements: {failures.Count}",
            $"executions with an install: {withInstall}; with a restart from a snapshot: {withRestart}",
            $"linearizable: {linearizable}; undecided: {undecided}") + "\n");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(5)));
        Assert.True(withInstall >= 3, $"only {withInstall} executions installed a snapshot");
        Assert.True(withRestart >= 3, $"only {withRestart} executions restarted from a snapshot");
    }
}
