using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Checker;
using Raft.Core;
using Raft.Core.Tests;
using Raft.Simulation;
using Xunit;

namespace Raft.Scale.Tests;

/// <summary>
/// P3-08: invariants 1, 8, 9 and 11 over generated executions with partitions (asymmetric
/// included), crashes and every other generated fault, and the distribution of what happened,
/// measured as effects (P2-02's rules: a floor, a near-always declaration, identical sets). The
/// suite runs a fixed sample of 300; the `soak` CI job runs the same test over 10,000
/// (RAFT_SOAK_COUNT), and a harness entry's verdict is a claim about the sample only (spec §12).
/// Vacuity risk: executions green because nothing happened. Guarded: every run must elect a leader
/// that acts; liveness must be checked in most runs, not skipped for want of a stable suffix; and
/// every effect below must clear the floor. Sabotages S-soak-1, S-soak-2, S-cov-7.
/// P4-07: with three clients retrying on timeout, the log invariants (2-7, 10) and invariant 11's
/// commit clause run too, and replication's effects join the distribution. Every run must commit
/// entries in fact, and the commit clause must be checked in most runs. Sabotages S-soak-4, S-soak-5,
/// S-cov-10.
/// P5-03: every execution's client history (P5-01's adapter) must be well formed and accepted by the
/// WGL checker; a search that exhausts its budget fails the run (P5 decision 3, without its declaration path). What
/// the histories contain is measured like effects: each mechanism a weak checker would also accept
/// without (indeterminate operations, concurrency, reads, each operation kind) must clear the floor
/// (P5 decision 5). Sabotages S-lin-1, S-lin-2.
/// </summary>
public sealed class SoakTests
{
    /// <summary>The soak's settings, shared with the budget tests (<see cref="SoakConfig"/>).</summary>
    public const long FaultsUntil = SoakConfig.FaultsUntil, Duration = SoakConfig.Duration, Think = SoakConfig.Think, CheckerBudget = SoakConfig.CheckerBudget;

    /// <summary>Clients per execution (<see cref="SoakConfig"/>).</summary>
    public const int Clients = SoakConfig.Clients;

    /// <summary>The baseline soak (P3-08 to P5-05): three nodes, no spares, the key-value workload retrying on timeout, KL-1.</summary>
    internal static readonly SoakProfile Baseline = new("soak-report.txt", " (membership aside)", 0, () => new SessionWorkload(new RaftWorkload(int.MaxValue, retry: true, think: Think), Cluster.Nodes), KnownLimits.FileName, []);

    /// <summary>When the last fault's effect ends (<see cref="Stability.StableFrom"/>, shared with the membership sample, P6-08).</summary>
    internal static long StableFrom(FaultSchedule schedule, IEnumerable<Observation> observations) => Stability.StableFrom(schedule, observations);

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    [Fact]
    public void TheGeneratedSampleHoldsEveryInvariant() =>
        Soak.Run(Baseline, Env("RAFT_SOAK_FIRST", 1), Env("RAFT_SOAK_COUNT", 300));

    /// <summary>
    /// The soak's floor at its own scale, tested here because the harness only ever runs the
    /// 300-execution sample (spec §12): at 10,000 an effect must reach 1%, not 3 executions.
    /// </summary>
    [Fact]
    public void AtSoakScaleTheFloorHoldsTheRateNotTheCount()
    {
        Dictionary<string, int> Counts(int total) => Soak.ElectionEffects.Concat(Coverage.Dimensions).ToDictionary(d => d, _ => total / 2, StringComparer.Ordinal);

        var soak = Counts(Soak.SoakExecutions);
        soak["split-vote"] = 50;
        Assert.True(Soak.RateFailures(soak, Soak.SoakExecutions).Any(f => f.StartsWith("split-vote: 50 of 10000 (0.5%), below the floor of 100", StringComparison.Ordinal)),
            "rate floor: 50 of 10,000 clears the absolute 3 and must still fail");

        soak["split-vote"] = 100;
        Assert.Empty(Soak.RateFailures(soak, Soak.SoakExecutions));

        var sample = Counts(300);
        sample["split-vote"] = 3;
        Assert.Empty(Soak.RateFailures(sample, 300));
        sample["split-vote"] = 2;
        Assert.Contains(Soak.RateFailures(sample, 300), f => f.StartsWith("split-vote: 2 of 300", StringComparison.Ordinal));

        const string Declared = "writes-completed-out-of-order-at-crash";
        var declaration = new Dictionary<string, string>(StringComparer.Ordinal) { [Declared] = "declared for this test" };
        soak["split-vote"] = 100;
        soak[Declared] = 8;
        Assert.Empty(Soak.RateFailures(soak, Soak.SoakExecutions, declaration));
        soak[Declared] = 2;
        Assert.True(Soak.RateFailures(soak, Soak.SoakExecutions, declaration).Any(f => f.StartsWith(Declared + ": 2 of 10000", StringComparison.Ordinal)),
            "absolute minimum: a declared effect may sit below the rate, never below 3");
    }

    /// <summary>P5 decision 3: one undecided search fails the run, however rare, at any scale.</summary>
    [Fact]
    public void AnExhaustedSearchAlwaysFailsTheRun()
    {
        Assert.Empty(Soak.BudgetFailures([], 300));
        Assert.Contains("undecided in 1 of 300", Assert.Single(Soak.BudgetFailures(["seed 7 (key k1, 80 operations)"], 300)), StringComparison.Ordinal);
        Assert.Contains("undecided in 1 of 10000", Assert.Single(Soak.BudgetFailures(["seed 7"], Soak.SoakExecutions)), StringComparison.Ordinal);
    }
}
