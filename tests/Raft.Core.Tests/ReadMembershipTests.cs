using System.Linq;
using Raft.Checker;
using Raft.Simulation;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P8-06: reads during membership changes. A read's heartbeat round counts a quorum of every
/// configuration in effect, both while joint, as commitment does (P6-05). Vacuity risk: a
/// construction whose two majorities overlap answers whichever quorum is counted; guarded by the
/// replacement shape (`{n1,n2,n3}` to `{n1,n4,n5}`), where the new majority holds no old server but
/// the leader. Sabotage S-read-3 (the round counted in the new configuration alone).
/// </summary>
public sealed class ReadMembershipTests
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

    private static void Rounds(ManualCluster c, NodeId leader, int rounds, params NodeId[] among)
    {
        c.Settle(among);
        for (var i = 0; i < rounds; i++)
        {
            c.Tick(leader, Interval);
            c.Settle(among);
        }
    }

    private static void Linearizable(ManualCluster c)
    {
        var r = ClientHistory.From(c.ClientLog());
        Assert.True(WglChecker.Check(r.History).IsLinearizable, string.Join("\n", r.History));
    }

    /// <summary>
    /// `{n1,n2,n3}` to `{n1,n4,n5}`, with only the new servers reachable: the leader is joint, and a
    /// read it receives has its round acknowledged by n4 and n5, a majority of `C_new`, and by no old
    /// server. It waits; once n2 and n3 are reachable again it is answered.
    /// </summary>
    [Fact]
    public void AReadWhileJointWaitsForAMajorityOfBothConfigurations()
    {
        var c = Led(spares: [N4, N5]);
        var all = c.Nodes.ToArray();
        var put = c.Client(N1, "Put|k|a");
        Rounds(c, N1, 2, all);
        Assert.Equal("ok", c.ReplyTo(put));

        var change = c.Client(N1, "Member|1,4,5");
        Rounds(c, N1, 4, N1, N4, N5);
        Assert.True(c.ConfigurationOf(N1).IsJoint);
        Assert.Null(c.ReplyTo(change));

        var read = c.Client(N1, "Get|k");
        Rounds(c, N1, 4, N1, N4, N5);
        Assert.True(c.ConfigurationOf(N4).IsJoint, "n4 did not take the joint configuration: it never acknowledged in it");
        Assert.True(c.ReplyTo(read) is null, "a read was answered by a majority of the new configuration alone while joint");

        Rounds(c, N1, 8, all);
        Assert.Equal("ok|a", c.ReplyTo(read));
        Assert.Equal("ok", c.ReplyTo(change));
        Linearizable(c);
    }

    /// <summary>
    /// Five servers to `{n2,n3,n4}`, the leader removed: once `C_new` commits n1 steps down, and a read
    /// sent to it then is redirected, never answered from its state.
    /// </summary>
    [Fact]
    public void ALeaderRemovedByTheNewConfigurationStopsAnsweringReads()
    {
        NodeId[] five = [N1, N2, N3, N4, N5];
        var c = Led(members: five);
        var put = c.Client(N1, "Put|k|a");
        Rounds(c, N1, 2, five);
        Assert.Equal("ok", c.ReplyTo(put));
        var before = c.Client(N1, "Get|k");
        Rounds(c, N1, 1, five);
        Assert.Equal("ok|a", c.ReplyTo(before));

        var change = c.Client(N1, "Member|2,3,4");
        Rounds(c, N1, 8, five);
        Assert.Equal("ok", c.ReplyTo(change));
        Assert.Equal(Role.Follower, c.RoleOf(N1));

        var after = c.Client(N1, "Get|k");
        Assert.Equal("redirect|-", c.ReplyTo(after));
        Linearizable(c);
    }
}
