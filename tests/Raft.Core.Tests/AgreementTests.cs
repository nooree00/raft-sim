using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P7-08, the done criterion, as a construction: in one cluster a node that compacts (n1) and two
/// that never do (n2, n3), across an install, a restart from a snapshot and a leader that does not
/// compact. Every node's state agrees with the committed entries replayed from the empty state
/// (<see cref="AgreementProbe"/>), and every applied ghost id agrees (State Machine Safety).
/// Vacuity risk (spec §10): comparing one node with another proves nothing when one's state came
/// from the other, and n3's comes from n1's snapshot; guarded: the comparison is with the replay of
/// the recorded commands, never with a node. Sabotage S-agree-1.
/// </summary>
public sealed class AgreementTests
{
    private const long Interval = 50;

    /// <summary>Compacting every three applied entries.</summary>
    public static readonly RaftOptions Compacting = RaftOptions.Default with { SnapshotThreshold = 3 };


    private static void Rounds(ManualCluster c, NodeId leader, int rounds, params NodeId[] among)
    {
        c.Settle(among);
        for (var i = 0; i < rounds; i++)
        {
            c.Tick(leader, Interval);
            c.Settle(among);
        }
    }

    [Fact]
    public void ACompactingNodeAndTwoThatNeverCompactAgreeWithTheCommittedEntries()
    {
        var probe = new AgreementProbe();
        var c = new ManualCluster(RaftOptions.Default, optionsFor: n => n == N1 ? Compacting : AgreementProbe.Uncompacted, stateMachine: probe.For);
        c.Stand(N1);
        c.Drop(N1, N3);
        c.Settle(N1, N2);
        foreach (var (k, v) in new[] { ("a", "1"), ("b", "2"), ("a", "3"), ("c", "4"), ("b", "5"), ("d", "6") })
        {
            c.Client(N1, $"Put|{k}|{v}");
            c.Drop(N1, N3);
            Rounds(c, N1, 1, N1, N2);
            c.Drop(N1, N3);
        }

        Assert.True(c.SnapshotOf(N1)?.Index >= 6, "n1 did not compact twice");
        Assert.Null(c.SnapshotOf(N2));
        Rounds(c, N1, 6, All);
        Assert.NotNull(c.SnapshotOf(N3));

        c.Crash(N1);
        c.Restart(N1);
        c.Stand(N2, above: 1);
        c.Settle(All);
        Assert.Equal(Role.Leader, c.RoleOf(N2));
        c.Client(N2, "Append|a|7");
        c.Client(N2, "Delete|c");
        Rounds(c, N2, 3, All);

        var (elections, log) = c.Histories();
        var analysis = new LogAnalysis(log);
        foreach (var r in LogAnalysis.Names.Select(analysis.Result))
        {
            Assert.True(r.Holds, $"{r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
        }

        Assert.True(ElectionInvariants.ElectionSafety(elections, 10_000).Holds);
        var (failures, compared) = probe.Check(c.Observations, analysis);
        Assert.True(failures.Count == 0, string.Join("; ", failures.Take(3)));
        Assert.True(compared >= 6, $"only {compared} states compared");
        Assert.Equal(2, c.Observations.OfType<EmittedObservation>().Count(o => o.Event.Name == "restore"));
    }
}
