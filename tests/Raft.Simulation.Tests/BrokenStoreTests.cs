using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raft.Checker;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P2-09: histories produced in the simulator by deliberately broken non-Raft stores must be
/// rejected by the WGL checker, and a correct store's must be accepted (spec §6: the second source
/// of known-bad histories, one that does not share the author's model of linearizability).
/// Vacuity risk: broken stores whose histories happen to be linearizable in every generated run
/// (a workload too sparse for the anomaly to show) make "rejects broken stores" a claim about a few
/// seeds. Guarded: the rejection rate per store is measured and must clear a floor, and the correct
/// store must be accepted in every run. Sabotages S-store-1..3.
/// </summary>
public sealed class BrokenStoreTests
{
    private const int Executions = 40;
    private const long Duration = 5_000;
    private static readonly NodeId N1 = new(1), N2 = new(2);

    internal static Simulator Run(ulong seed, NodeFactory factory, KvWorkload workload, FaultSchedule schedule)
    {
        var sim = new Simulator(new SimulationConfig { Duration = Duration, Clients = 3 }, factory, seed, schedule) { Workload = workload };
        sim.Run();
        return sim;
    }

    private static FaultSchedule Generated(ulong seed) => FaultGenerator.Generate(seed, new GeneratorConfig { Duration = Duration, Clients = 3 });

    /// <summary>A state-placed crash of n1 while a write is in flight, early in the run (P2-02's fault).</summary>
    private static FaultSchedule CrashInFlight(ulong seed) => new([new CrashWhenInFlight(50 + (long)(seed % 7) * 20, N1, 1, DiskLoss.Pending, 200)]);

    private static (int Rejected, int Undecided, CheckResult? Example) Rate(NodeFactory factory, KvWorkload workload, Func<ulong, FaultSchedule> schedule)
    {
        int rejected = 0, undecided = 0;
        CheckResult? example = null;
        for (var seed = 1UL; seed <= Executions; seed++)
        {
            var r = WglChecker.Check(KvCodec.History(Run(seed, factory, workload, schedule(seed))));
            rejected += r.Verdict == Verdict.NotLinearizable ? 1 : 0;
            undecided += r.Verdict == Verdict.Undecided ? 1 : 0;
            example ??= r.Verdict == Verdict.NotLinearizable ? r : null;
        }

        return (rejected, undecided, example);
    }

    private static KvWorkload ReadFromBackup() => new(N1, N2, perClient: 40);

    private static KvWorkload AllToOne() => new(N1, N1, perClient: 40);

    private static readonly Lazy<Dictionary<string, (int Rejected, int Undecided, CheckResult? Example)>> Rates = new(() => new()
    {
        ["a/fault-free"] = Rate(ctx => new AsyncPrimaryBackupNode(ctx), ReadFromBackup(), _ => FaultSchedule.Empty),
        ["a/default"] = Rate(ctx => new AsyncPrimaryBackupNode(ctx), ReadFromBackup(), Generated),
        ["b/default"] = Rate(ctx => new AckBeforeDurableNode(ctx), AllToOne(), Generated),
        ["b/crash-in-flight"] = Rate(ctx => new AckBeforeDurableNode(ctx), AllToOne(), CrashInFlight),
        ["c/default"] = Rate(ctx => new DurableSingleNode(ctx), AllToOne(), Generated),
        ["c/crash-in-flight"] = Rate(ctx => new DurableSingleNode(ctx), AllToOne(), CrashInFlight),
    });

    private static void Report()
    {
        var lines = Rates.Value.Select(kv => $"{kv.Key,-18} rejected {kv.Value.Rejected,2} / {Executions}, undecided {kv.Value.Undecided}");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "broken-stores.txt"), string.Join("\n", lines) + "\n");
    }

    [Fact]
    public void AsyncPrimaryBackupReadingFromTheBackupIsRejected()
    {
        Report();
        var (rejected, undecided, example) = Rates.Value["a/default"];

        Assert.True(rejected >= Executions / 4, $"rejected in only {rejected} of {Executions}");
        Assert.Equal(0, undecided);
        Assert.NotNull(example!.Key);
        Assert.NotEmpty(example.SubHistory);
    }

    [Fact]
    public void AcknowledgingBeforeTheWriteIsDurableIsRejectedWhenACrashLosesTheWrite()
    {
        Report();
        var (rejected, undecided, _) = Rates.Value["b/crash-in-flight"];
        var withoutTheCrash = Rates.Value["b/default"].Rejected;

        // The acknowledged write is lost in every run, but a later read shows it only if no write to
        // the key comes first: 7 of 40 when measured (P2-09's prediction said more than half). The
        // floor is the coverage floor: rejected often enough that missing it by chance is about 5%.
        Assert.True(rejected >= Coverage.FloorFor(Executions) && rejected > withoutTheCrash, $"rejected in {rejected} of {Executions} ({Coverage.Rate(rejected, Executions)}; without the state-placed crash: {withoutTheCrash})");
        Assert.Equal(0, undecided);
    }

    [Fact]
    public void TheCorrectStoreIsAcceptedInEveryRun()
    {
        Report();

        var (d, c) = (Rates.Value["c/default"], Rates.Value["c/crash-in-flight"]);
        Assert.True((d.Rejected, d.Undecided, c.Rejected, c.Undecided) == (0, 0, 0, 0),
            $"the correct store was rejected or undecided: default {d.Rejected}/{d.Undecided}, crash-in-flight {c.Rejected}/{c.Undecided} of {Executions}");
    }

    [Fact]
    public void TheHistoryIsWellFormedAndKeepsIndeterminateOperations()
    {
        var sim = Run(3, ctx => new DurableSingleNode(ctx), AllToOne(), new FaultSchedule([new Partition(100, N1, Simulator.ClientNode(1)), new Heal(400, N1, Simulator.ClientNode(1))]));
        var history = KvCodec.History(sim);

        Assert.Empty(History.Problems(history));
        Assert.Equal(sim.ClientLog.Count, history.Count);
        Assert.Contains(history, o => o.IsIndeterminate);
        Assert.True(WglChecker.Check(history).IsLinearizable);
    }
}
