using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P8-03: spec §5 item 6's sentence as constructions: "the session table is part of the snapshot;
/// otherwise a retry after compaction is applied again". A client's `Append` commits, its reply is
/// lost, the cluster compacts past it, and the retry reaches a node whose state came from a snapshot
/// covering the original: by an install, then by a restart. Vacuity risk: a retry answered by the
/// node that applied the original, whose table never left memory, says nothing about the snapshot;
/// guarded by asserting the answering node restored a snapshot covering the original entry. Sabotages
/// S-sess-4 (the unit test of restore's contract), S-sess-1.
/// </summary>
public sealed class SessionCompactionTests
{
    private const long Interval = 50;
    private static readonly RaftOptions Options = RaftOptions.Default with { SnapshotThreshold = 3 };

    private static void Rounds(ManualCluster c, NodeId leader, int rounds, params NodeId[] among)
    {
        for (var i = 0; i < rounds; i++)
        {
            c.Tick(leader, Interval);
            c.Settle(among);
        }
    }

    /// <summary>n1 leads n2 with n3 cut off; a session registers, its Append commits (index returned), and more writes compact past it.</summary>
    private static (ManualCluster C, AgreementProbe Probe, long Session, long Appended) Committed()
    {
        var probe = new AgreementProbe();
        var c = new ManualCluster(Options, stateMachine: probe.For);
        c.Stand(N1);
        c.Drop(N1, N3);
        c.Settle(N1, N2);
        var register = c.Client(N1, "Register|");
        c.Drop(N1, N3);
        Rounds(c, N1, 2, N1, N2);
        c.Drop(N1, N3);
        var session = long.Parse(c.ReplyTo(register)![3..], System.Globalization.CultureInfo.InvariantCulture);
        var append = c.Client(N1, $"Session|{session}|1|Append|x|a");
        c.Drop(N1, N3);
        Rounds(c, N1, 2, N1, N2);
        c.Drop(N1, N3);
        Assert.Equal("ok", c.ReplyTo(append));
        var appended = c.Observations.OfType<EmittedObservation>().Where(o => o.Node == N1 && o.Event.Name == "apply").Select(o => long.Parse(o.Event.Fields[0].Value, System.Globalization.CultureInfo.InvariantCulture)).Max();
        // Another client's writes compact the log past the Append; this session's latest stays 1.
        var other = c.Client(N1, "Register|");
        c.Drop(N1, N3);
        Rounds(c, N1, 2, N1, N2);
        c.Drop(N1, N3);
        var second = long.Parse(c.ReplyTo(other)![3..], System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < 4; i++)
        {
            c.Client(N1, $"Session|{second}|{1 + i}|Put|y{i}|v");
            c.Drop(N1, N3);
            Rounds(c, N1, 1, N1, N2);
            c.Drop(N1, N3);
        }

        Assert.True(c.SnapshotOf(N1)?.Index > appended, "n1 did not compact past the Append");
        return (c, probe, session, appended);
    }

    private static void Agreed(ManualCluster c, AgreementProbe probe)
    {
        var (_, log) = c.Histories();
        var analysis = new LogAnalysis(log);
        foreach (var r in LogAnalysis.Names.Select(analysis.Result))
        {
            Assert.True(r.Holds, $"{r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
        }

        var (failures, _) = probe.Check(c.Observations, analysis);
        Assert.True(failures.Count == 0, string.Join("; ", failures.Take(3)));
    }

    /// <summary>n3 installs the snapshot, leads, and answers the retry from the session table it restored. Sabotage S-sess-1.</summary>
    [Fact]
    public void ARetryAfterAnInstallIsAnsweredFromTheRestoredTable()
    {
        var (c, probe, session, appended) = Committed();
        Rounds(c, N1, 6, All);
        Assert.True(c.SnapshotOf(N3)?.Index >= appended, "n3 did not install a snapshot covering the Append");
        Assert.Contains(c.Observations.OfType<EmittedObservation>(), o => o.Node == N3 && o.Event.Name == "restore");

        c.Crash(N1);
        c.Tick(N2, Options.ElectionTimeoutMin);
        c.Stand(N3, above: 1);
        c.Settle(N2, N3);
        Assert.Equal(Role.Leader, c.RoleOf(N3));
        var retry = c.Client(N3, $"Session|{session}|1|Append|x|a");
        Rounds(c, N3, 2, N2, N3);
        var read = c.Client(N3, "Get|x");
        Rounds(c, N3, 2, N2, N3);

        Assert.Equal("ok", c.ReplyTo(retry));
        Assert.Equal("ok|a", c.ReplyTo(read));
        Agreed(c, probe);
    }

    /// <summary>n2 restarts from its snapshot, leads, and answers the retry from the table it restored at start.</summary>
    [Fact]
    public void ARetryAfterARestartFromASnapshotIsAnsweredFromTheRestoredTable()
    {
        var (c, probe, session, appended) = Committed();
        Assert.True(c.SnapshotOf(N2)?.Index >= appended, "n2 did not compact past the Append");
        c.Crash(N2);
        c.Restart(N2);
        c.Crash(N1);
        c.Restart(N1);
        c.Stand(N2, above: 1);
        c.Settle(All);
        Assert.Equal(Role.Leader, c.RoleOf(N2));
        var retry = c.Client(N2, $"Session|{session}|1|Append|x|a");
        Rounds(c, N2, 3, All);
        var read = c.Client(N2, "Get|x");
        Rounds(c, N2, 3, All);

        Assert.Equal("ok", c.ReplyTo(retry));
        Assert.Equal("ok|a", c.ReplyTo(read));
        Agreed(c, probe);
    }
}
