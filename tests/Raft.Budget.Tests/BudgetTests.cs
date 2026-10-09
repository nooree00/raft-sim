using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Checker;
using Raft.Core.Tests;
using Raft.Scale.Tests;
using Raft.Simulation;
using Xunit;

namespace Raft.Budget.Tests;

/// <summary>
/// P5-05: the soak's hardest decided histories, met by the suite on every run, each decided within the
/// soak's budget (<see cref="SoakConfig.CheckerBudget"/>). Vacuity risk: a soak whose checker never
/// meets a hard history says little about the budget; these are the top of the soak's distribution
/// of states explored among the histories it decides. Re-picked at P8-09 from the soak with sessions
/// and ReadIndex reads, whose histories all changed: a retry is one operation now (P8-00), and the
/// hardest decided history fell from 21,232,723 states (3044, phase 7) to 2,477. Re-picked again at
/// P11-03, after one append in flight per follower (P11-02) changed every history: phase 8's hardest
/// seed, 5908, became easy enough that a 1,000-state budget decided it, and S-soak-6 survived on
/// GitHub. Sabotage S-soak-6.
/// </summary>
public sealed class BudgetTests
{
    /// <summary>
    /// The seeds, from ci/budget-seeds.txt (copied beside the assembly): at P11-03, at 10,000
    /// executions on six keys, sessions and ReadIndex reads, after the resend fix, by states explored:
    /// 9532 (3,702), 6673 (2,371), 9824 (890), the three hardest (phase 8's were 5908, 9699 and 4105).
    /// P13-01 moved them out of this file: the soak job compares them with its own ranking on every
    /// run, so a change that makes one easy fails there rather than waiting for a sabotage to survive.
    /// </summary>
    public static readonly int[] HardSeeds = [.. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "budget-seeds.txt"))
        .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => int.Parse(l, CultureInfo.InvariantCulture))];

    public static TheoryData<int> Seeds => new(HardSeeds);

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheSoaksHardestDecidedHistoriesAreDecidedWithinTheBudget(int seed)
    {
        var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = SoakConfig.FaultsUntil });
        var (sim, _) = Cluster.Run((ulong)seed, SoakConfig.Duration, schedule, SoakConfig.Clients, new SessionWorkload(new RaftWorkload(int.MaxValue, retry: true, think: SoakConfig.Think), Cluster.Nodes), options: SoakConfig.Options);
        var lin = WglChecker.Check(ClientHistory.From(sim.ClientLog).History, SoakConfig.CheckerBudget);
        Assert.True(lin.IsLinearizable, $"seed {seed}: {lin.Verdict} at key {lin.Key} after {lin.StatesExplored} states (budget {SoakConfig.CheckerBudget})");
    }
}
