using System.Linq;
using Raft.Checker;
using Raft.Simulation;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P8-05: ReadIndex in <see cref="RaftNode"/> (phase 8 decision 4). A `Get` is answered without a
/// log entry: the leader takes its commit index as the read's index once an entry of its own term
/// has committed, confirms with a heartbeat round sent after the read arrived that a quorum still
/// follows it, and answers once it has applied up to the read's index. Each construction is judged
/// by the WGL checker over its client log, and each step it depends on is asserted. Vacuity risk: a
/// construction in which no other leader exists answers correctly with or without the round;
/// guarded by the partitioned old leader, which a second leader has overtaken with a write the
/// first has not seen. Sabotages S-read-1 (no round), S-read-2 (no wait for the term's commit),
/// S-read-4 (an acknowledgement of an earlier round counted).
/// </summary>
public sealed class ReadIndexTests
{
    private static void Linearizable(ManualCluster c)
    {
        var r = ClientHistory.From(c.ClientLog());
        Assert.True(WglChecker.Check(r.History).IsLinearizable, string.Join("\n", r.History));
    }

    /// <summary>
    /// n3 stops hearing n1 before it can vote (the disruption rule): its timer runs out and its own
    /// candidacy, in term 2, is lost; n2 then stands above it.
    /// </summary>
    private static void N3StandsAndLoses(ManualCluster c)
    {
        c.Tick(N3, RaftOptions.Default.ElectionTimeoutMin);
        c.Drop(N3);
    }

    /// <summary>n1 leads term 1, and `Put|k|a` is committed and applied everywhere.</summary>
    private static ManualCluster Written()
    {
        var c = new ManualCluster(RaftOptions.Default);
        c.Stand(N1);
        c.Settle(All);
        var put = c.Client(N1, "Put|k|a");
        c.Settle(All);
        c.Tick(N1, 50);
        c.Settle(All);
        Assert.Equal("ok", c.ReplyTo(put));
        return c;
    }

    /// <summary>
    /// A partitioned old leader cannot answer. n1 is cut off; n2 leads term 2 with n3 and commits
    /// `Put|k|b`, answered. A read sent to n1 afterwards would see `a`: n1 still believes it leads.
    /// Its round reaches no one, so it never answers, and once the partition heals it steps down.
    /// </summary>
    [Fact]
    public void APartitionedOldLeaderCannotAnswerARead()
    {
        var c = Written();
        c.Drop(N1);
        N3StandsAndLoses(c);
        c.Stand(N2, above: 2);
        c.Settle(N2, N3);
        Assert.Equal(Role.Leader, c.RoleOf(N2));
        var put = c.Client(N2, "Put|k|b");
        c.Settle(N2, N3);
        c.Tick(N2, 50);
        c.Settle(N2, N3);
        Assert.Equal("ok", c.ReplyTo(put));
        c.Drop(N2, N1);
        c.Drop(N3, N1);

        Assert.Equal(Role.Leader, c.RoleOf(N1));
        var read = c.Client(N1, "Get|k");
        for (var i = 0; i < 3; i++)
        {
            c.Tick(N1, 50);
            c.Drop(N1);
        }

        Assert.True(c.ReplyTo(read) is null, "the partitioned old leader answered the read: " + c.ReplyTo(read));
        c.Tick(N1, 50);
        c.Deliver(N1, N2);
        c.Deliver(N2, N1);
        Assert.NotEqual(Role.Leader, c.RoleOf(N1));
        Assert.Null(c.ReplyTo(read));
        Linearizable(c);
    }

    /// <summary>
    /// A new leader's read waits for its no-op. `Put|k|a` reaches n2 but not n3; n1 commits it with
    /// n2 and answers, then crashes before n2 learns that it committed. n2 leads term 2 with n3, its
    /// commit index still 1. A read sent to n2 at once has a round n3 acknowledges (by refusing an
    /// entry it cannot place, in n2's term) while n2's commit index is 1, where `k` is absent; the read
    /// must wait until n2's no-op commits, and then sees `a`.
    /// </summary>
    [Fact]
    public void ANewLeadersReadWaitsForItsNoOp()
    {
        var c = new ManualCluster(RaftOptions.Default);
        c.Stand(N1);
        c.Settle(All);
        c.Tick(N1, 50);
        c.Settle(All);
        var put = c.Client(N1, "Put|k|a");
        c.Drop(N1, N3);
        c.Deliver(N1, N2);
        c.Deliver(N2, N1);
        Assert.Equal("ok", c.ReplyTo(put));
        c.Crash(N1);

        N3StandsAndLoses(c);
        c.Stand(N2, above: 2);
        c.Deliver(N2, N3);
        c.Deliver(N3, N2);
        Assert.Equal(Role.Leader, c.RoleOf(N2));
        var read = c.Client(N2, "Get|k");
        c.Deliver(N2, N3);
        c.Deliver(N3, N2);
        Assert.True(c.ReplyTo(read) != "ok|-", "the new leader answered from its commit index before its no-op committed");

        c.Settle(N2, N3);
        c.Tick(N2, 50);
        c.Settle(N2, N3);
        Assert.Equal("ok|a", c.ReplyTo(read));
        Linearizable(c);
    }

    /// <summary>
    /// A read with a slow follower in its quorum waits for that follower's acknowledgement of a round
    /// sent after the read arrived. n3 is down; n2 has answered a heartbeat sent before the read, and
    /// that answer, delivered after the read arrived, does not confirm it. The next round's does.
    /// </summary>
    [Fact]
    public void AReadWaitsForTheSlowFollowerInItsQuorum()
    {
        var c = Written();
        c.Crash(N3);
        c.Tick(N1, 50);
        c.Deliver(N1, N2);
        var read = c.Client(N1, "Get|k");
        c.Deliver(N2, N1);
        Assert.True(c.ReplyTo(read) is null, "an acknowledgement of a heartbeat sent before the read confirmed it");

        c.Deliver(N1, N2);
        Assert.True(c.ReplyTo(read) is null, "the read was answered before its quorum acknowledged its round");
        c.Deliver(N2, N1);
        Assert.Equal("ok|a", c.ReplyTo(read));
        Assert.DoesNotContain(c.EntriesOf(N1), e => System.Text.Encoding.ASCII.GetString(e.Command) == "Get|k");
        Linearizable(c);
    }

    /// <summary>Followers and candidates redirect a read, as they do a write.</summary>
    [Fact]
    public void AFollowerRedirectsARead()
    {
        var c = Written();
        var read = c.Client(N2, "Get|k");
        Assert.Equal("redirect|n1", c.ReplyTo(read));
    }
}
