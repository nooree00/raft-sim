namespace Raft.Scale.Tests;

/// <summary>
/// The soak's settings (P3-08, P4-07, P5-05), in one file linked by the soak and by the budget tests
/// (tests/Raft.Budget.Tests), so that both run the same executions.
/// </summary>
internal static class SoakConfig
{
    /// <summary>
    /// Faults are generated until 12,000. The run goes to 22,000 (20,000 until P4-07): with log writes on
    /// every replicated batch, a state-placed crash armed near 12,000 now fires, and its restart (up to
    /// 3,000 later) left too short a stable suffix in 27% of runs.
    /// </summary>
    public const long FaultsUntil = 12_000, Duration = 22_000;
    public const int Clients = 3;

    /// <summary>
    /// Each client pauses this long before a new operation (P4-07). Without it the three clients commit
    /// about 900 entries a run and an execution costs 7.6 times a client-free one (410 ms against 54,
    /// locally), which would put the 10,000-execution soak near an hour. At 100 an execution commits
    /// about 145 entries and costs 63 ms; the commit clause is checked as often.
    /// </summary>
    public const long Think = 100;

    /// <summary>
    /// The linearizability search's budget in states, per key (P5-05): what the enforcing runner holds
    /// in usable memory, measured on the GitHub runner (15.1 GB available). At 32,000,000 states the
    /// search peaks at 12.5 GB with flat time per state; at 40,000,000 it peaks at 14.9 GB and is
    /// already slower; at 48,000,000 it swaps (three times slower per state); at 56,000,000 it is
    /// killed. The search memoises every state it reaches, so memory binds, and no per-key count of
    /// indeterminate operations predicts the cost (60,000 keys measured): a history the budget cannot
    /// decide fails the soak unless it is a recorded known limit (ci/known-limits.txt).
    /// </summary>
    public const long CheckerBudget = 32_000_000;

    /// <summary>
    /// Phase 7 decision 6: the soaks run the system as it runs, compaction on. Every 20 applied
    /// entries, so that most executions compact (an execution commits about 150, the membership
    /// workload about 100) and followers left behind by a partition or a crash install snapshots.
    /// </summary>
    public const int SnapshotThreshold = 20;

    public static Raft.Core.RaftOptions Options => Raft.Core.Tests.Cluster.Options with { SnapshotThreshold = SnapshotThreshold };
}
