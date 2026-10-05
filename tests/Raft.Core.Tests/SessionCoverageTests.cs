using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Raft.Simulation;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P8-07: each session and read dimension of <see cref="SessionCoverage"/>, absent where its effect
/// did not happen though the party that would decide it acted, and present where it did. Sabotage
/// S-cov-14 (deduplication counted when a retry is sent).
/// </summary>
public sealed class SessionCoverageTests
{
    private static HashSet<string> Of(ManualCluster c, IReadOnlyList<string>? trace = null)
    {
        var (elections, log) = c.Histories();
        return SessionCoverage.Of(c.Observations, new LogAnalysis(log), elections, c.ClientLog(), trace ?? [], RaftOptions.Default.HeartbeatInterval);
    }

    private static ManualCluster Elected()
    {
        var c = new ManualCluster(RaftOptions.Default);
        c.Stand(N1);
        c.Settle(All);
        c.Tick(N1, 50);
        c.Settle(All);
        return c;
    }

    private static long Register(ManualCluster c, NodeId leader, params NodeId[] among)
    {
        var r = c.Client(leader, "Register|");
        c.Settle(among);
        c.Tick(leader, 50);
        c.Settle(among);
        return long.Parse(c.ReplyTo(r)![3..], CultureInfo.InvariantCulture);
    }

    /// <summary>n2 wins term 2 with n3 (n1 is down or cut off), and its no-op commits.</summary>
    private static void N2Leads(ManualCluster c)
    {
        c.Tick(N3, RaftOptions.Default.ElectionTimeoutMin);
        c.Drop(N3);
        c.Stand(N2, above: 2);
        c.Settle(N2, N3);
        c.Tick(N2, 50);
        c.Settle(N2, N3);
        Assert.Equal(Role.Leader, c.RoleOf(N2));
    }

    private static int Copies(ManualCluster c, NodeId n, string command) => c.EntriesOf(n).Count(e => Encoding.ASCII.GetString(e.Command) == command);

    /// <summary>
    /// The original reaches no one: n1 is cut off when it arrives, then crashes. The client resends it
    /// to n2, the next leader, where it commits once. A retry was sent, and nothing was deduplicated.
    /// Sabotage S-cov-14.
    /// </summary>
    [Fact]
    public void ARetryWhoseOriginalNeverCommittedDeduplicatesNothing()
    {
        var c = Elected();
        var session = Register(c, N1, All);
        var command = $"Session|{session}|1|Append|k|x";
        c.Client(N1, command);
        c.Drop(N1);
        c.Crash(N1);
        N2Leads(c);
        var retry = c.Client(N2, command);
        c.Settle(N2, N3);
        c.Tick(N2, 50);
        c.Settle(N2, N3);
        Assert.Equal("ok", c.ReplyTo(retry));
        Assert.Equal(1, Copies(c, N2, command));
        Assert.Equal(2, c.ClientLog().Count(o => Encoding.ASCII.GetString(o.Request.Span) == command));

        Assert.DoesNotContain("retry-committed-twice-applied-once", Of(c));
    }

    /// <summary>The original commits at n2 before n1 can answer; n1 crashes; n2 leads, and the retry commits a second copy, answered from the table.</summary>
    [Fact]
    public void ARetryWhoseOriginalCommittedIsCommittedTwice()
    {
        var c = Elected();
        var session = Register(c, N1, All);
        var command = $"Session|{session}|1|Append|k|x";
        c.Client(N1, command);
        c.Drop(N1, N3);
        c.Deliver(N1, N2);
        c.Crash(N1);
        N2Leads(c);
        var retry = c.Client(N2, command);
        c.Settle(N2, N3);
        c.Tick(N2, 50);
        c.Settle(N2, N3);
        Assert.Equal("ok", c.ReplyTo(retry));
        Assert.Equal(2, Copies(c, N2, command));

        var hit = Of(c);
        Assert.Contains("retry-committed-twice-applied-once", hit);
        Assert.DoesNotContain("retry-deduplicated-by-a-restored-table", hit);
    }

    /// <summary>P8-03's two constructions: the retry reaches a node whose table came from a snapshot, by an install and by a restart.</summary>
    [Fact]
    public void ARetryAnsweredFromARestoredTableIsCountedByInstallAndByRestart()
    {
        Assert.Contains("retry-deduplicated-by-a-restored-table", Of(SessionCompactionTests.AfterAnInstall().C));
        Assert.Contains("retry-deduplicated-by-a-restored-table", Of(SessionCompactionTests.AfterARestart().C));
    }

    /// <summary>A copy of sequence 1 commits after sequence 2: refused as stale, and counted from the log.</summary>
    [Fact]
    public void ALowerSequenceNumberCommittedAfterAHigherOneIsStale()
    {
        var c = Elected();
        var session = Register(c, N1, All);
        Assert.DoesNotContain("stale-sequence-refused", Of(c));
        c.Client(N1, $"Session|{session}|1|Put|k|a");
        c.Client(N1, $"Session|{session}|2|Put|k|b");
        var late = c.Client(N1, $"Session|{session}|1|Put|k|a");
        c.Settle(All);
        c.Tick(N1, 50);
        c.Settle(All);
        Assert.Equal("stale|", c.ReplyTo(late));
        Assert.Contains("stale-sequence-refused", Of(c));
    }

    private static string Line(long time, NodeId node, string kind, long request) =>
        time.ToString("D10", CultureInfo.InvariantCulture) + " " + node + " " + kind + " request=" + request.ToString(CultureInfo.InvariantCulture) + " len=5";

    /// <summary>
    /// A read's wait is measured at the leader, from the request's arrival to the answer's release, as
    /// the simulator traces them: past one heartbeat interval it counts, within it not. Here the trace
    /// is written by hand around a read n1 answered.
    /// </summary>
    [Fact]
    public void AReadsWaitIsMeasuredFromItsArrivalToItsAnswerAtTheLeader()
    {
        var c = Elected();
        var read = c.Client(N1, "Get|k");
        c.Settle(All);
        Assert.Equal("ok|-", c.ReplyTo(read));

        Assert.Contains("read-waited-past-a-heartbeat-interval", Of(c, [Line(100, N1, "REQUEST", read), Line(151, N1, "RESPONSE", read)]));
        Assert.DoesNotContain("read-waited-past-a-heartbeat-interval", Of(c, [Line(100, N1, "REQUEST", read), Line(150, N1, "RESPONSE", read)]));
    }

    /// <summary>
    /// n1, deposed without knowing it, is traced answering a read after n2's election in term 2: the
    /// later term is read from the election history at the answer's time, and not before it began.
    /// </summary>
    [Fact]
    public void AReadAnsweredAfterALaterTermBeganIsCountedFromTheElectionHistory()
    {
        var c = Elected();
        var read = c.Client(N1, "Get|k");
        c.Settle(All);
        Assert.Equal("ok|-", c.ReplyTo(read));
        var before = c.Now;
        c.Drop(N1);
        N2Leads(c);
        var (elections, _) = c.Histories();
        var at = elections.Elections().Single(e => e.Key.Candidate == N2).Value;
        Assert.True(at > before);

        Assert.Contains("read-answered-after-a-later-term-began", Of(c, [Line(at - 1, N1, "REQUEST", read), Line(at, N1, "RESPONSE", read)]));
        Assert.DoesNotContain("read-answered-after-a-later-term-began", Of(c, [Line(before - 1, N1, "REQUEST", read), Line(before, N1, "RESPONSE", read)]));
    }
}
