using System.Linq;
using Raft.Checker;
using Raft.Core.Tests;
using Raft.Simulation;
using Xunit;

namespace Raft.Scale.Tests;

/// <summary>
/// P5-04: the client histories of the soak's first <see cref="Seeds"/> executions, judged by the
/// linearizability checker alone, verdict first. The soak sample covers the same seeds with every
/// check; this is the harness's cheaper target for the broken variants of `RaftNode` (S-lin-3..5),
/// so that three more entries do not each cost a 300-execution soak, and so that a variant is
/// reported as caught by the checker rather than by whichever soak assertion runs first. Forty
/// seeds include the first in which answering a write at append is caught (21 on six keys; 28 and 37
/// on three); if a change moves it past 40, S-lin-4 survives and says so.
/// </summary>
public sealed class LinearizabilityTests
{
    public const int Seeds = 40;

    [Fact]
    public void TheFirstSoakHistoriesAreLinearizable()
    {
        for (var seed = 1; seed <= Seeds; seed++)
        {
            var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = SoakTests.FaultsUntil });
            var (sim, _) = Cluster.Run((ulong)seed, SoakTests.Duration, schedule, SoakTests.Clients, new RaftWorkload(int.MaxValue, retry: true, think: SoakTests.Think));
            var client = ClientHistory.From(sim.ClientLog);
            var lin = WglChecker.Check(client.History, SoakTests.CheckerBudget);
            Assert.True(lin.Verdict != Verdict.NotLinearizable, $"seed {seed}, linearizability at key {lin.Key}: no linearization past\n  {string.Join("\n  ", lin.LongestPrefix.TakeLast(5))}");
            Assert.True(lin.Verdict != Verdict.Undecided, $"seed {seed}: linearizability undecided at key {lin.Key} (budget exhausted)");
            Assert.Empty(History.Problems(client.History));
            Assert.Empty(client.Unexplained);
        }
    }
}
