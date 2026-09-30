using System.Linq;
using Raft.Core;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P4-06: invariant 11's commit clause on hand-built executions, each rejected one step away from a
/// twin that holds. Vacuity risk: a clause that counts a command by its bytes, or counts the new
/// leader's no-op, holds in a cluster that commits nothing a client asked for after the fault;
/// guarded by the two rejected executions below, one for each. Sabotages S-live-1, S-live-2.
/// </summary>
public sealed class CommitLivenessTests
{
    private const long Window = 1_000;

    /// <summary>n1 leads term 1, and its no-op and "Put|x|1" are committed. Returns the time after that: where the suffix starts.</summary>
    private static long Elected(ManualCluster c)
    {
        c.Stand(N1);
        c.Settle(All);
        c.Tick(N1, 50);
        c.Settle(All);
        c.Client(N1, "Put|x|1");
        c.Settle(All);
        c.Tick(N1, 50);
        c.Settle(All);
        return c.Now;
    }

    private static InvariantResult Clause(ManualCluster c, long stableFrom)
    {
        var (h, log) = c.Histories();
        return CommitLiveness.Clause(h, c.Observations, new LogAnalysis(log), stableFrom, c.Now, Window, minimumSuffix: 0);
    }

    [Fact]
    public void AResubmittedCommandThatNeverCommitsFailsAlthoughTheSameBytesCommittedBefore()
    {
        var lost = new ManualCluster(RaftOptions.Default);
        var from = Elected(lost);
        lost.Client(N1, "Put|x|1");
        lost.Drop(N1);
        var failed = Clause(lost, from);
        Assert.True(failed.Count("checked") == 1, "the clause was not checked");
        Assert.False(failed.Holds, "a command created after the suffix began was never committed; the same bytes committed before it must not count");

        var twin = new ManualCluster(RaftOptions.Default);
        from = Elected(twin);
        twin.Client(N1, "Put|x|1");
        twin.Settle(All);
        var held = Clause(twin, from);
        Assert.True(held.Holds, string.Join("; ", held.Violations));
        Assert.Equal(1, held.Count("commands-committed"));
    }

    /// <summary>n1 crashes after the suffix begins; n3 leads term 2 with n2, and its no-op at 3 commits.</summary>
    [Fact]
    public void ANewLeadersNoOpAloneDoesNotSatisfyTheClause()
    {
        var lost = new ManualCluster(RaftOptions.Default);
        var from = Elected(lost);
        lost.Crash(N1);
        lost.Tick(N2, 200);
        lost.Stand(N3, above: 1);
        lost.Settle(N2, N3);
        Assert.Equal(Role.Leader, lost.RoleOf(N3));
        lost.Client(N3, "Put|y|2");
        lost.Drop(N3);
        var (_, log) = lost.Histories();
        Assert.True(new LogAnalysis(log).Commits.Any(c => !c.Command && c.Index == 3), "n3's no-op at 3 was not committed in fact: the execution did not take the intended shape");
        Assert.False(Clause(lost, from).Holds, "only the new leader's no-op was committed after the suffix began; it is no client's command");

        var twin = new ManualCluster(RaftOptions.Default);
        from = Elected(twin);
        twin.Crash(N1);
        twin.Tick(N2, 200);
        twin.Stand(N3, above: 1);
        twin.Settle(N2, N3);
        twin.Client(N3, "Put|y|2");
        twin.Settle(N2, N3);
        Assert.True(Clause(twin, from).Holds);
    }

    [Fact]
    public void ContinuityFailsOnAStretchLongerThanTheWindowWithoutACommittedCommand()
    {
        var idle = new ManualCluster(RaftOptions.Default);
        var from = Elected(idle);
        for (var i = 0; i < 20; i++)
        {
            idle.Tick(N1, 50);
            idle.Settle(All);
        }

        idle.Client(N1, "Put|x|2");
        idle.Settle(All);
        var (_, log) = idle.Histories();
        var analysis = new LogAnalysis(log);
        var result = CommitLiveness.Continuity(idle.Observations, analysis, from, idle.Now, window: 30);
        Assert.False(result.Holds, "about 60 units passed with no command committed, against a window of 30");
        Assert.True(CommitLiveness.Continuity(idle.Observations, analysis, from, idle.Now, window: 200).Holds);
    }
}
