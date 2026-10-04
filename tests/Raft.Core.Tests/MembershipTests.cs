using System;
using System.Linq;
using Raft.Core;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P6-05: joint consensus in <see cref="RaftNode"/>, as exact constructions. A membership request
/// appends `C_old,new`; while it is in effect elections and commitment need a majority of both
/// configurations; once it commits the leader appends `C_new`, and once that commits it answers the
/// request and, if `C_new` leaves it out, steps down. Vacuity risk: a change that completes in a
/// cluster with no failure proves the happy path only; so each construction has a step in which
/// only one configuration's majority is reachable, and asserts that nothing commits then and that
/// the change completes once both are. Every construction is judged by the checkers (P6-04).
/// Sabotages S-member-1..3.
/// </summary>
public sealed class MembershipTests
{
    private const long Interval = 50;

    private static ManualCluster Led(NodeId[]? members = null, NodeId[]? spares = null)
    {
        var c = new ManualCluster(RaftOptions.Default, members: members, spares: spares);
        var all = c.Nodes.ToArray();
        c.Stand(N1);
        c.Settle(all);
        c.Tick(N1, Interval);
        c.Settle(all);
        Assert.Equal(Role.Leader, c.RoleOf(N1));
        return c;
    }

    /// <summary>Heartbeat rounds among <paramref name="among"/>: each tick of the leader, then everything delivered.</summary>
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

    /// <summary>
    /// Three servers replaced by a different three but one: `{n1,n2,n3}` to `{n1,n4,n5}`. While only the
    /// new servers are reachable, neither the change nor a write commits: `C_old,new` needs a majority
    /// of both (the old majority alone is the next test's cut; cuts in one construction accumulate
    /// copies, so each starts from a fresh change). Sabotages S-member-1 (commit by `C_new`'s majority alone)
    /// and S-member-3 (`C_new` appended before `C_old,new` commits).
    /// </summary>
    [Fact]
    public void ThreeServersAreReplacedAndNothingCommitsOnTheOldMajorityAlone()
    {
        var c = Led(spares: [N4, N5]);
        var all = c.Nodes.ToArray();
        var before = c.Client(N1, "Put|a|1");
        Rounds(c, N1, 2, all);
        Assert.Equal("ok", c.ReplyTo(before));

        var change = c.Client(N1, "Member|1,4,5");
        var during = c.Client(N1, "Put|b|2");
        Rounds(c, N1, 4, N1, N4, N5);
        Assert.True(c.ConfigurationOf(N1).IsJoint);
        Assert.Null(c.ReplyTo(during));
        Assert.Null(c.ReplyTo(change));

        Rounds(c, N1, 8, all);
        Assert.Equal("ok", c.ReplyTo(change));
        Assert.Equal("ok", c.ReplyTo(during));
        Assert.Equal(new Configuration([N1, N4, N5]), c.ConfigurationOf(N1));
        Assert.Equal(new Configuration([N1, N4, N5]), c.ConfigurationOf(N4));

        var after = c.Client(N1, "Put|c|3");
        Rounds(c, N1, 2, N1, N4, N5);
        Assert.Equal("ok", c.ReplyTo(after));
        Judged(c);
    }

    /// <summary>Three to five: while only n1 and n2 are reachable, a majority of the old configuration and not of the new, nothing commits.</summary>
    /// <remarks>The old-majority-only cut, the replace construction's twin.</remarks>
    [Fact]
    public void ThreeServersBecomeFive()
    {
        var c = Led(spares: [N4, N5]);
        var all = c.Nodes.ToArray();
        var change = c.Client(N1, "Member|1,2,3,4,5");
        var during = c.Client(N1, "Put|b|2");
        Rounds(c, N1, 4, N1, N2);
        Assert.Null(c.ReplyTo(during));
        Assert.Null(c.ReplyTo(change));

        Rounds(c, N1, 8, all);
        Assert.Equal("ok", c.ReplyTo(change));
        Assert.Equal("ok", c.ReplyTo(during));
        Assert.Equal(new Configuration([N1, N2, N3, N4, N5]), c.ConfigurationOf(N5));
        Judged(c);
    }

    /// <summary>
    /// Five to three with the leader removed: `{n1..n5}` to `{n2,n3,n4}`. n1 manages the change, cannot
    /// count itself toward `C_new`'s majority, and steps down once `C_new` commits; a member of
    /// `C_new` is then elected and commits a write.
    /// </summary>
    [Fact]
    public void FiveServersBecomeThreeAndTheLeaderIsRemoved()
    {
        NodeId[] five = [N1, N2, N3, N4, N5];
        var c = Led(members: five);
        var change = c.Client(N1, "Member|2,3,4");
        Rounds(c, N1, 4, N1, N4, N5);
        Assert.Null(c.ReplyTo(change));

        Rounds(c, N1, 8, five);
        Assert.Equal("ok", c.ReplyTo(change));
        Assert.Equal(Role.Follower, c.RoleOf(N1));
        Assert.Equal(new Configuration([N2, N3, N4]), c.ConfigurationOf(N2));

        // n3 and n4 wait out the minimum election timeout since they last heard n1 (the disruption
        // rule, P3-06: until then they ignore a vote request), as time would pass for them too.
        c.Tick(N3, 200);
        c.Tick(N4, 200);
        c.Stand(N2, above: 1);
        c.Settle(N2, N3, N4);
        Rounds(c, N2, 2, N2, N3, N4);
        Assert.Equal(Role.Leader, c.RoleOf(N2));
        var after = c.Client(N2, "Put|c|3");
        Rounds(c, N2, 2, N2, N3, N4);
        Assert.Equal("ok", c.ReplyTo(after));
        Judged(c);
    }

    /// <summary>One change at a time (decision 4): a second request while the first is in flight is refused with `busy|`, a definite failure.</summary>
    [Fact]
    public void ASecondChangeWhileOneIsInFlightIsBusy()
    {
        var c = Led(spares: [N4, N5]);
        c.Client(N1, "Member|1,2,3,4");
        var second = c.Client(N1, "Member|1,2,3,5");

        Assert.Equal("busy|", c.ReplyTo(second));
    }

    /// <summary>
    /// An election during joint consensus: n1 crashes with `C_old,new` on n1, n2 and n3 only; n3 stands
    /// holding it, and n2's grant, a majority of the old configuration, does not elect it. Once n4
    /// and n5 grant too, a majority of both, it is elected, and finishes the change n1 started. Sabotage S-member-2 (a candidate wins with
    /// `C_old`'s majority alone).
    /// </summary>
    [Fact]
    public void ACandidateDuringJointConsensusNeedsAMajorityOfBoth()
    {
        var c = Led(spares: [N4, N5]);
        c.Client(N1, "Member|1,4,5");
        Rounds(c, N1, 2, N1, N2, N3);
        Assert.True(c.ConfigurationOf(N2).IsJoint);
        c.Crash(N1);

        // n2 waits out the disruption rule's window (it ignores a vote request within the minimum
        // election timeout of hearing n1) without reaching its own timeout (minimum plus 149).
        c.Tick(N2, 160);
        c.Stand(N3, above: 1);
        c.Settle(N2, N3);
        Assert.NotEqual(Role.Leader, c.RoleOf(N3));

        c.Settle(N2, N3, N4, N5);
        Assert.Contains((new Term(2), N3), c.Histories().Elections.Elections().Keys);

        // The new leader finishes the change its predecessor started: `C_old,new` commits with its
        // no-op, it appends `C_new`, and `C_new` leaves it out, so it steps down once that commits.
        Assert.Equal(new Configuration([N1, N4, N5]), c.ConfigurationOf(N4));
        Assert.Equal(Role.Follower, c.RoleOf(N3));
        Judged(c);
    }

    /// <summary>
    /// P6-06: two empty servers replace two members of a cluster with a long log, `{n1,n2,n3}` to
    /// `{n1,n4,n5}`. They catch up as non-voting members first: until then the configuration stays
    /// `C_old`, and a write commits on the old servers alone within one round; `C_old,new` is appended
    /// only once both hold all but at most one batch of the log. Sabotage S-member-4 (`C_old,new`
    /// appended at once, without waiting: the write during the change then needs n4 or n5).
    /// </summary>
    [Fact]
    public void NewServersCatchUpBeforeTheyCount()
    {
        var c = Led(spares: [N4, N5]);
        var all = c.Nodes.ToArray();
        for (var i = 0; i < 100; i++)
        {
            c.Client(N1, "Put|k" + i + "|v");
        }

        Rounds(c, N1, 4, N1, N2, N3);
        var length = c.EntriesOf(N1).Count;
        Assert.True(length > 100, "a long log: " + length);

        var change = c.Client(N1, "Member|1,4,5");
        Assert.False(c.ConfigurationOf(N1).IsJoint);
        var during = c.Client(N1, "Put|x|1");
        Rounds(c, N1, 1, N1, N2, N3);
        Assert.Equal("ok", c.ReplyTo(during));

        Rounds(c, N1, 10, all);
        Assert.Equal("ok", c.ReplyTo(change));
        Assert.Equal(new Configuration([N1, N4, N5]), c.ConfigurationOf(N1));

        // The leader waited: when it appended `C_old,new`, n4's disk already held the log but at most one batch.
        var appended = c.Observations.FindIndex(o => o is Raft.Simulation.EmittedObservation { Event.Name: "configuration" } e && e.Node == N1);
        var n4 = new LogHistory.FileView();
        foreach (var o in c.Observations.Take(appended))
        {
            if (o is Raft.Simulation.DurableObservation { File: EntryLog.FileName, Completed: { } done } d && d.Node == N4)
            {
                n4.Apply(done);
            }
        }

        Assert.True(n4.Count >= length - RaftOptions.Default.MaxEntriesPerAppend, $"n4 held {n4.Count} of {length} entries when C_old,new was appended");
        Judged(c);
    }

    /// <summary>
    /// P6-09, the positive control: the paper's Figure 10. Three servers become five while a partition
    /// separates a majority of `C_old` (n2, n3) from a majority of `C_new` (n1, n4, n5), with the
    /// change on the new side only. Each side elects in term 2. A node that switched directly to
    /// `C_new` would hold two leaders in that term; with `C_old,new` in between, n4's candidacy also
    /// needs a majority of `C_old`, which its side lacks, and only n3 is elected. The construction
    /// asserts that both majorities did grant in term 2 before it asserts the verdict, so a partition
    /// that failed to separate them cannot pass for safety. Sabotage S-member-6 (the change appends
    /// `C_new` directly).
    /// </summary>
    [Fact]
    public void ADirectChangeWouldElectTwoLeadersAndTheJointConfigurationElectsOne()
    {
        var c = Led(spares: [N4, N5]);
        c.Client(N1, "Member|1,2,3,4,5");
        Rounds(c, N1, 4, N1, N4, N5);
        Assert.Equal(new[] { N1, N2, N3, N4, N5 }, c.ConfigurationOf(N4).Members.OrderBy(m => m.Value).ToArray());
        Assert.Equal(new Configuration([N1, N2, N3]), c.ConfigurationOf(N2));

        // The partition: n1's messages to n2 and n3 are lost with it. n1 and n5 restart, so neither
        // still believes in n1's leadership (the disruption rule would have them ignore n4 within the
        // minimum election timeout, and a timeout of exactly that length leaves no tick in between).
        c.Crash(N1);
        c.Crash(N5);
        c.Restart(N1);
        c.Restart(N5);

        // The old side: n2 waits out the rule's window below its own timeout, and n3 stands.
        c.Tick(N2, 160);
        c.Stand(N3, above: 1);
        c.Settle(N2, N3);

        // The new side: n4 stands in the same term.
        Assert.Equal(2, c.Stand(N4, above: 1));
        c.Settle(N1, N4, N5);

        var (elections, _) = c.Histories();
        bool Granted(NodeId from, NodeId to) => elections.Deliveries.Any(d => d.From == from && d.To == to && d.Message is RequestVoteResponse { VoteGranted: true } r && r.Term == new Term(2));
        Assert.True(Granted(N2, N3), "the old majority did not grant n3 in term 2: the construction does not separate the majorities");
        Assert.True(Granted(N1, N4) && Granted(N5, N4), "the new majority did not grant n4 in term 2: the construction does not separate the majorities");

        Judged(c);
        Assert.Equal(Role.Leader, c.RoleOf(N3));
        Assert.NotEqual(Role.Leader, c.RoleOf(N4));
    }
}
