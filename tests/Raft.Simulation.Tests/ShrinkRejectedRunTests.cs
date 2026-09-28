using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Checker;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P2-10: a checker rejection is a failure signature ("linearizability@key"), so the P1-11 shrinker
/// reduces a broken store's failing schedule end to end. Phase 1's finding applies: removal shows
/// the kept events are sufficient, not that each is a cause. Vacuity risk: a signature keyed on
/// "some key rejected" lets the shrinker converge on another anomaly at another key; guarded by
/// shrinking toward each key's own signature. Sabotage S-shrink-4.
/// </summary>
public sealed class ShrinkRejectedRunTests
{
    private const long Duration = 5_000;
    private static readonly NodeId N1 = new(1), N2 = new(2);

    private static IReadOnlySet<string> Signatures(NodeFactory factory, KvWorkload workload, ulong seed, FaultSchedule schedule) =>
        WglChecker.Signatures(KvCodec.History(BrokenStoreTests.Run(seed, factory, workload, schedule)));

    private static FaultSchedule Generated(ulong seed) => FaultGenerator.Generate(seed, new GeneratorConfig { Duration = Duration, Clients = 3 });

    [Fact]
    public void AStaleReadFromTheBackupShrinksToNoFaultsAtAll()
    {
        NodeFactory store = ctx => new AsyncPrimaryBackupNode(ctx);
        var workload = new KvWorkload(N1, N2, perClient: 40);
        var seed = Enumerable.Range(1, 40).Select(s => (ulong)s).First(s =>
            Signatures(store, workload, s, Generated(s)).Contains("linearizability@k0") && Signatures(store, workload, s, FaultSchedule.Empty).Contains("linearizability@k0"));

        var r = Shrinker.Shrink(Generated(seed), "linearizability@k0", s => Signatures(store, workload, seed, s));

        Assert.Empty(r.Schedule.Faults);
    }

    [Fact]
    public void ALostAcknowledgedWriteShrinksToAScheduleThatKeepsTheCrash()
    {
        NodeFactory store = ctx => new AckBeforeDurableNode(ctx);
        var workload = new KvWorkload(N1, N1, perClient: 40);
        FaultSchedule Planted(ulong s) => new([.. Generated(s).Faults, new CrashWhenInFlight(50 + ((long)(s % 7) * 20), N1, 1, DiskLoss.Pending, 200)]);
        var (seed, signature) = Enumerable.Range(1, 60).Select(s => (ulong)s)
            .Select(s => (s, Signatures(store, workload, s, Planted(s)).FirstOrDefault(x => x.StartsWith("linearizability@", StringComparison.Ordinal))))
            .First(x => x.Item2 is not null);

        var r = Shrinker.Shrink(Planted(seed), signature!, s => Signatures(store, workload, seed, s));

        Assert.Contains(r.Schedule.Faults, f => f is CrashWhenInFlight { Node.Value: 1 } or Crash { Node.Value: 1 } or CrashAll or CrashMajority);
        Assert.True(r.OneMinimal);
        Assert.Contains(signature!, Signatures(store, workload, seed, r.Schedule));
        ShrunkForTheRecord = $"seed {seed}, {signature}: {r.Schedule.Faults.Count} events in {r.Runs} runs: " + string.Join("; ", r.Schedule.Faults)
            + "; order-sensitive pairs: " + string.Join(",", r.OrderSensitive);
        System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "shrunk-store-b.txt"), ShrunkForTheRecord + "\n");
    }

    private static string ShrunkForTheRecord { get; set; } = "";

    [Fact]
    public void EachKeysAnomalyShrinksTowardItsOwnSignature()
    {
        NodeFactory store = ctx => new AsyncPrimaryBackupNode(ctx);
        var workload = new KvWorkload(N1, N2, perClient: 40);
        var seed = Enumerable.Range(1, 40).Select(s => (ulong)s).First(s =>
        {
            var sig = Signatures(store, workload, s, Generated(s));
            return sig.Contains("linearizability@k0") && sig.Contains("linearizability@k1");
        });

        foreach (var key in new[] { "k0", "k1" })
        {
            var r = Shrinker.Shrink(Generated(seed), "linearizability@" + key, s => Signatures(store, workload, seed, s));
            var history = KvCodec.History(BrokenStoreTests.Run(seed, store, workload, r.Schedule));

            // Checked directly on the key's sub-history, not through the signature.
            Assert.Equal(Verdict.NotLinearizable, WglChecker.Check(history.Where(o => o.Key == key).ToList()).Verdict);
        }
    }
}
