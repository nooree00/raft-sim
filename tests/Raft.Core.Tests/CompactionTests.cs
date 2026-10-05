using System;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P7-06: compaction in <see cref="RaftNode"/> (phase 7 decision 3), as exact constructions with a
/// small threshold. Each compacts at a leader and at a follower, and each is judged by the checkers
/// over the logical log (P7-04). Vacuity risks: a cluster that never reaches the threshold never
/// compacts (guarded: every construction asserts the snapshot on disk first); a compaction at the
/// leader alone misses the follower's paths (guarded: the follower's snapshot is asserted and its
/// paths, the election restriction and the consistency check at the boundary, are the
/// constructions). Sabotages S-compact-3, S-compact-4.
/// </summary>
public sealed class CompactionTests
{
    private const long Interval = 50;
    private static readonly RaftOptions Options = RaftOptions.Default with { SnapshotThreshold = 3 };

    private static void Rounds(ManualCluster c, NodeId leader, int rounds, params NodeId[] among)
    {
        c.Settle(among);
        for (var i = 0; i < rounds; i++)
        {
            c.Tick(leader, Interval);
            c.Settle(among);
        }
    }

    private static void Judged(ManualCluster c)
    {
        var (elections, log) = c.Histories();
        Assert.True(ElectionInvariants.ElectionSafety(elections, 10_000).Holds, string.Join("; ", ElectionInvariants.ElectionSafety(elections, 10_000).Violations.Take(3)));
        foreach (var r in LogInvariants.All(log))
        {
            Assert.True(r.Holds, $"{r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
        }
    }

    /// <summary>n1 leads n2 with n3 cut off, and two writes commit: three entries, compacted by both at 3, n2's retained log empty.</summary>
    private static ManualCluster CompactedWithoutN3()
    {
        var c = new ManualCluster(Options);
        c.Stand(N1);
        c.Drop(N1, N3);
        c.Settle(N1, N2);
        Assert.Equal(Role.Leader, c.RoleOf(N1));
        c.Client(N1, "Put|a|1");
        c.Client(N1, "Put|b|2");
        for (var i = 0; i < 3; i++)
        {
            c.Drop(N1, N3);
            Rounds(c, N1, 1, N1, N2);
        }

        c.Drop(N1, N3);
        Assert.Equal(3, c.SnapshotOf(N1)?.Index);
        Assert.Equal(3, c.SnapshotOf(N2)?.Index);
        Assert.Empty(c.EntriesOf(N2));
        return c;
    }

    /// <summary>
    /// The prediction's construction: a follower whose retained log is empty after compacting still
    /// answers the election restriction by its snapshot's index and term. n3, which holds nothing,
    /// stands, and n2 refuses it.
    /// </summary>
    [Fact]
    public void AFollowerWhoseRetainedLogIsEmptyRefusesACandidateWithAStaleLog()
    {
        var c = CompactedWithoutN3();
        c.Tick(N2, Options.ElectionTimeoutMin);
        c.Stand(N3);
        c.Deliver(N3, N2);
        c.Deliver(N2, N3);
        Assert.NotEqual(Role.Leader, c.RoleOf(N3));
        Judged(c);
    }

    /// <summary>
    /// The consistency check at the boundary: the leader's next entry follows the follower's snapshot,
    /// and the follower must accept it with nothing retained to check it against but the snapshot's
    /// term. Sabotage S-compact-4.
    /// </summary>
    [Fact]
    public void AFollowerAtItsSnapshotsBoundaryAcceptsTheLeadersNextEntries()
    {
        var c = CompactedWithoutN3();
        var request = c.Client(N1, "Put|c|3");
        c.Drop(N1, N3);
        Rounds(c, N1, 2, N1, N2);
        Assert.Equal("ok", c.ReplyTo(request));
        Assert.Equal([4L], c.EntriesOf(N2).Select(e => e.Index));
        Judged(c);
    }

    /// <summary>
    /// A compaction covers applied entries only: with an entry the followers have not acknowledged
    /// at the end of its log, the leader compacts up to what committed, and a restart restores no
    /// more than that. Sabotage S-compact-3.
    /// </summary>
    [Fact]
    public void ACompactionNeverPassesTheAppliedIndex()
    {
        var c = new ManualCluster(Options);
        c.Stand(N1);
        c.Settle(All);
        c.Client(N1, "Put|a|1");
        c.Client(N1, "Put|b|2");
        Rounds(c, N1, 2, All);
        Assert.Equal(3, c.SnapshotOf(N1)?.Index);

        c.Client(N1, "Put|c|3");
        c.Client(N1, "Put|d|4");
        c.Client(N1, "Put|e|5");
        c.Deliver(N1, N2);
        c.Client(N1, "Put|f|6");
        c.Deliver(N2, N1);
        Assert.Equal(6, c.SnapshotOf(N1)?.Index);
        Assert.Equal(7, c.EntriesOf(N1).Single().Index);

        c.Crash(N1);
        c.Restart(N1);
        c.Tick(N1, 1);
        Assert.Contains(c.Observations.OfType<EmittedObservation>(), o => o.Node == N1 && o.Event.Name == "restore" && o.Event.Fields.Single().Value == "6");
        Judged(c);
    }

    /// <summary>
    /// Every node restarts from its snapshot: the state machine restored from it (a read after the
    /// restart sees writes the snapshot covers) and the log continuing after it.
    /// </summary>
    [Fact]
    public void EveryNodeRestartsFromItsSnapshotWithItsState()
    {
        var c = new ManualCluster(Options);
        c.Stand(N1);
        c.Settle(All);
        c.Client(N1, "Put|a|1");
        c.Client(N1, "Put|b|2");
        c.Client(N1, "Put|a|3");
        Rounds(c, N1, 2, All);
        foreach (var n in All)
        {
            Assert.True(c.SnapshotOf(n)?.Index >= 3, n + " did not compact");
            c.Crash(n);
            c.Restart(n);
        }

        c.Stand(N2, above: 1);
        c.Settle(All);
        Assert.Equal(Role.Leader, c.RoleOf(N2));
        var read = c.Client(N2, "Get|a");
        Rounds(c, N2, 2, All);
        Assert.Equal("ok|3", c.ReplyTo(read));
        Judged(c);
    }

    /// <summary>
    /// The register's row: the snapshot carries the configuration in effect at its index. A
    /// membership change to four servers, compacted past both of its entries, survives a restart of
    /// a node whose retained log holds no configuration entry.
    /// </summary>
    [Fact]
    public void TheConfigurationInTheSnapshotSurvivesARestart()
    {
        var c = new ManualCluster(Options, spares: [N4]);
        var all = c.Nodes.ToArray();
        c.Stand(N1);
        c.Settle(all);
        var change = c.Client(N1, "Member|1,2,3,4");
        Rounds(c, N1, 4, all);
        Assert.Equal("ok", c.ReplyTo(change));
        c.Client(N1, "Put|a|1");
        c.Client(N1, "Put|b|2");
        Rounds(c, N1, 3, all);

        var snapshot = c.SnapshotOf(N2);
        Assert.NotNull(snapshot?.Configuration);
        Assert.DoesNotContain(c.EntriesOf(N2), e => Configuration.IsInternal(e.Command));
        c.Crash(N2);
        c.Restart(N2);
        Assert.Equal([N1, N2, N3, N4], c.ConfigurationOf(N2).Members.OrderBy(m => m.Value));
        Judged(c);
    }
}
