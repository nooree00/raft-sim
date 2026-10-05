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
/// of states explored among the histories it decides. Re-picked at P7-11 from the soak with
/// compaction on, whose histories all changed. 3044, the hardest decided both before and after
/// (21,232,606 states before, 21,232,723 with compaction; four minutes and 5 GB), is not named here: five harness entries run the whole suite, and each paid
/// its four minutes (P5-05 full local run: shard 1 at 825 s). It is met by every soak, at the same
/// budget, where an undecided search fails the run. Sabotage S-soak-6.
/// </summary>
public sealed class BudgetTests
{
    /// <summary>At 10,000 executions on six keys, compaction on (P7-11), by states explored: 8260 (2,450,195), 4681 (3,111,131), 7248 (7,607,718).</summary>
    public static readonly int[] HardSeeds = [8260, 4681, 7248];

    public static TheoryData<int> Seeds => new(HardSeeds);

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheSoaksHardestDecidedHistoriesAreDecidedWithinTheBudget(int seed)
    {
        var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = SoakConfig.FaultsUntil });
        var (sim, _) = Cluster.Run((ulong)seed, SoakConfig.Duration, schedule, SoakConfig.Clients, new RaftWorkload(int.MaxValue, retry: true, think: SoakConfig.Think), options: SoakConfig.Options);
        var lin = WglChecker.Check(ClientHistory.From(sim.ClientLog).History, SoakConfig.CheckerBudget);
        Assert.True(lin.IsLinearizable, $"seed {seed}: {lin.Verdict} at key {lin.Key} after {lin.StatesExplored} states (budget {SoakConfig.CheckerBudget})");
    }
}
