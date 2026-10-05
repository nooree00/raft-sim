using System.Linq;
using System.Text;
using Raft.Checker;
using Raft.Kv;
using Raft.Simulation;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P8-08: the two positive controls as constructions. A state machine that applies every session
/// command (<see cref="KvStateMachine"/> without deduplication) and a leader that answers reads from
/// its own state at once (<see cref="RaftOptions.ReadsWithoutQuorum"/>) must each turn the WGL checker
/// red where the real node and state machine, in the same construction, keep it green. Vacuity risk:
/// a construction where the retried command's original never committed shows no duplicate; guarded by
/// asserting two committed copies before the verdict is read. Sabotage S-ctl-1 (both controls on by
/// default).
/// </summary>
public sealed class ControlConstructionTests
{
    private static bool Linearizable(ManualCluster c) => WglChecker.Check(ClientHistory.From(c.ClientLog()).History).IsLinearizable;

    /// <summary>
    /// A session's `Append|k|x` commits at n2 before n1 can answer; n1 crashes; n2 leads, the retry
    /// commits a second copy, and a read of k follows. Returns the cluster and the read's answer.
    /// </summary>
    private static (ManualCluster C, string? Read) RetriedAppend(bool deduplicate)
    {
        // The real case builds the state machine as the system does, by its defaults.
        var c = new ManualCluster(RaftOptions.Default, stateMachine: _ => deduplicate ? new KvStateMachine() : new KvStateMachine(deduplicate: false));
        c.Stand(N1);
        c.Settle(All);
        c.Tick(N1, 50);
        c.Settle(All);
        var register = c.Client(N1, "Register|");
        c.Settle(All);
        c.Tick(N1, 50);
        c.Settle(All);
        var session = c.ReplyTo(register)![3..];
        var command = $"Session|{session}|1|Append|k|x";
        c.Client(N1, command);
        c.Drop(N1, N3);
        c.Deliver(N1, N2);
        c.Crash(N1);
        c.Tick(N3, RaftOptions.Default.ElectionTimeoutMin);
        c.Drop(N3);
        c.Stand(N2, above: 2);
        c.Settle(N2, N3);
        c.Tick(N2, 50);
        c.Settle(N2, N3);
        Assert.Equal(Role.Leader, c.RoleOf(N2));
        c.Client(N2, command);
        c.Settle(N2, N3);
        c.Tick(N2, 50);
        c.Settle(N2, N3);
        Assert.Equal(2, c.EntriesOf(N2).Count(e => Encoding.ASCII.GetString(e.Command) == command));
        var read = c.Client(N2, "Get|k");
        c.Settle(N2, N3);
        c.Tick(N2, 50);
        c.Settle(N2, N3);
        return (c, c.ReplyTo(read));
    }

    /// <summary>
    /// n1 leads and `Put|k|a` commits; n1 is cut off; n2 leads term 2 and commits `Put|k|b`, answered;
    /// a read is then sent to n1, which still believes it leads. Returns the cluster and the read's answer.
    /// </summary>
    private static (ManualCluster C, string? Read) DeposedLeaderRead(RaftOptions options)
    {
        var c = new ManualCluster(options);
        c.Stand(N1);
        c.Settle(All);
        var first = c.Client(N1, "Put|k|a");
        c.Settle(All);
        c.Tick(N1, 50);
        c.Settle(All);
        Assert.Equal("ok", c.ReplyTo(first));
        c.Drop(N1);
        c.Tick(N3, options.ElectionTimeoutMin);
        c.Drop(N3);
        c.Stand(N2, above: 2);
        c.Settle(N2, N3);
        var second = c.Client(N2, "Put|k|b");
        c.Settle(N2, N3);
        c.Tick(N2, 50);
        c.Settle(N2, N3);
        Assert.Equal("ok", c.ReplyTo(second));
        c.Drop(N2, N1);
        c.Drop(N3, N1);
        Assert.Equal(Role.Leader, c.RoleOf(N1));
        var read = c.Client(N1, "Get|k");
        c.Tick(N1, 50);
        c.Drop(N1);
        return (c, c.ReplyTo(read));
    }

    /// <summary>The deduplication control applies the retry again, the read sees `xx`, and the checker rejects the history.</summary>
    [Fact]
    public void AStateMachineWithoutDeduplicationTurnsTheCheckerRed()
    {
        var (c, read) = RetriedAppend(deduplicate: false);
        Assert.Equal("ok|xx", read);
        Assert.False(Linearizable(c), "an Append applied twice was accepted");
    }

    /// <summary>The read control answers the deposed leader's read with `a`, overwritten by `b` before it was sent, and the checker rejects the history.</summary>
    [Fact]
    public void ALeaderAnsweringReadsWithoutAQuorumTurnsTheCheckerRed()
    {
        var (c, read) = DeposedLeaderRead(RaftOptions.Default with { ReadsWithoutQuorum = true });
        Assert.Equal("ok|a", read);
        Assert.False(Linearizable(c), "a deposed leader's stale read was accepted");
    }

    /// <summary>The real state machine and node, in both constructions: the retry is answered from the table, the deposed leader never answers, and both histories are accepted. Sabotage S-ctl-1.</summary>
    [Fact]
    public void TheRealNodeIsGreenInBothControlConstructions()
    {
        var (dedup, retried) = RetriedAppend(deduplicate: true);
        Assert.True(retried == "ok|x", $"the real state machine answered the read after the retry with {retried}");
        Assert.True(Linearizable(dedup), "the real state machine's history was rejected");

        var (reads, stale) = DeposedLeaderRead(RaftOptions.Default);
        Assert.True(stale is null, $"the real node's deposed leader answered the read with {stale}");
        Assert.True(Linearizable(reads), "the real node's history was rejected");
    }
}
