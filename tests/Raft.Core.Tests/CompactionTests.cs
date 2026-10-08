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
        // One append in flight per follower (P11-02): n2 gets entry 4, its answer sends 5 and 6.
        c.Deliver(N1, N2);
        c.Deliver(N2, N1);
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

    /// <summary>
    /// P7-10's construction: n1 leads, commits three entries and compacts, then appends four more, of
    /// which three reach n2 and commit while the fourth (index 7) is on n1 alone. n1 crashes; n2 leads
    /// a later term and commits its no-op at 7; n1 comes back. Returns the verdicts, and n2's entry at 7 and
    /// n1's snapshot, for the guard that the entry was overwritten.
    /// </summary>
    private static (System.Collections.Generic.Dictionary<string, InvariantResult> Verdicts, Term Overwriting, LogSnapshot? Compacted) PastCommit(RaftOptions options)
    {
        var c = new ManualCluster(options);
        c.Stand(N1);
        c.Settle(All);
        c.Client(N1, "Put|a|1");
        c.Client(N1, "Put|b|2");
        Rounds(c, N1, 2, All);
        c.Client(N1, "Put|c|3");
        c.Client(N1, "Put|d|4");
        c.Client(N1, "Put|e|5");
        // One append in flight per follower (P11-02): n2 gets entry 4, its answer sends 5 and 6.
        c.Deliver(N1, N2);
        c.Drop(N1, N3);
        c.Deliver(N2, N1);
        c.Deliver(N1, N2);
        c.Drop(N1, N3);
        c.Client(N1, "Put|f|6");
        c.Drop(N1);
        c.Deliver(N2, N1);
        var compacted = c.SnapshotOf(N1);
        c.Crash(N1);

        // n3 must stop hearing n1 before it can vote (the disruption rule): its timer runs out, its
        // own candidacy is lost, and n2, more up to date, wins the term after.
        c.Tick(N3, Options.ElectionTimeoutMin);
        c.Drop(N3);
        c.Stand(N2, above: 2);
        c.Settle(N2, N3);
        Rounds(c, N2, 3, N2, N3);
        Assert.Equal(Role.Leader, c.RoleOf(N2));
        // Bounded rounds, not settling: the control can never reconcile with the new leader, and the
        // two would exchange rejections forever.
        c.Restart(N1);
        for (var i = 0; i < 4; i++)
        {
            c.Tick(N2, Interval);
            foreach (var (from, to) in new[] { (N2, N1), (N1, N2), (N2, N3), (N3, N2) })
            {
                c.Deliver(from, to);
            }
        }
        var (_, log) = c.Histories();
        var analysis = new LogAnalysis(log);
        // n2's term at 7, from its log or, once it has compacted there, its snapshot.
        var overwriting = c.EntriesOf(N2).FirstOrDefault(e => e.Index == 7)?.Term ?? (c.SnapshotOf(N2) is { Index: 7 } s ? s.Term : Term.Zero);
        return (LogAnalysis.Names.ToDictionary(n => n, analysis.Result), overwriting, compacted);
    }

    /// <summary>
    /// The positive control (P7-10): a node that compacts past its commit index turns State Machine
    /// Safety red in the construction, and the real node in the same construction does not. Guarded:
    /// the entry the control compacted at 7 is overwritten by the next leader (term 3) before the
    /// verdict is read. Sabotage S-compact-5 (the control's compaction in the real node).
    /// </summary>
    [Fact]
    public void ACompactionPastTheCommitIndexTurnsStateMachineSafetyRedAndTheRealNodeDoesNot()
    {
        var (control, overwriting, compacted) = PastCommit(Options with { CompactPastCommit = true });
        Assert.Equal(7, compacted?.Index);
        Assert.Equal(new Term(3), overwriting);
        Assert.False(control["state-machine-safety"].Holds, "the control compacted an entry the next leader overwrote, and State Machine Safety held");

        var (real, _, realCompacted) = PastCommit(Options);
        Assert.True(realCompacted?.Index == 6, $"the real node compacted to {realCompacted?.Index}, past its applied index 6");
        foreach (var (name, r) in real)
        {
            Assert.True(r.Holds, $"the real node, {name}: {string.Join("; ", r.Violations.Take(3))}");
        }
    }
}

