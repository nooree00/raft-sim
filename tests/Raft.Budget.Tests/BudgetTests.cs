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
/// hardest decided history fell from 21,232,723 states (3044, phase 7) to 2,477. Sabotage S-soak-6.
/// </summary>
public sealed class BudgetTests
{
    /// <summary>
    /// At 10,000 executions on six keys, sessions and ReadIndex reads (P8-09), by states explored: 5908
    /// (2,477), 9699 (924), 4105 (797), the three hardest. None is left out for its cost any more: the
    /// three together check in well under a second.
    /// </summary>
    public static readonly int[] HardSeeds = [5908, 9699, 4105];

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
