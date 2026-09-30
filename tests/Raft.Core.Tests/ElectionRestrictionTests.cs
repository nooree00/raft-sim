using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Kv;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P4-04: the election restriction (§5.4.1) and Figure 8's commit rule, now that logs are non-empty.
/// Unit tests for the vote; then exact executions in a <see cref="ManualCluster"/>, checked by the
/// P4-01 checkers: Figure 8 on three nodes, and a stale candidate. Vacuity risk: a construction
/// whose steps do not happen as intended passes without testing the rule; guarded by asserting each
/// step by what the disks hold. Sabotages S-commit-1, S-commit-2, S-restrict-1.
/// </summary>
public sealed class ElectionRestrictionTests
{
    private static readonly Term T1 = new(1), T2 = new(2), T3 = new(3);

    private sealed class ZeroRandom : IRandomSource
    {
        public ulong NextUInt64() => 0;
    }

    private static byte[] Log(params Term[] terms)
    {
        var store = new LogStore(EntryLog.Recover(null));
        return store.Append(terms.Select(t => new LogEntry(t, Encoding.ASCII.GetBytes("Put|x|" + t.Value))).ToList()).Data.ToArray();
    }

    /// <summary>A voter at term 2 whose log holds entries of terms 1 and 2; true when it grants a term-3 candidate with the given last entry.</summary>
    private static bool Grants(long lastIndex, Term lastTerm)
    {
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
        {
            [EntryLog.FileName] = Log(T1, T2),
            [TermVoteLog.FileName] = TermVoteLog.Record(T2, null),
        };
        var voter = new RaftNode(new NodeContext(N2, [N1, N3], new ZeroRandom(), files), RaftOptions.Default, new KvStateMachine());
        var reply = voter.Handle(new Receive(N1, MessageCodec.Encode(new RequestVote(T3, N1, lastIndex, lastTerm))))
            .OfType<Send>().Select(s => MessageCodec.Decode(s.Payload.ToArray())).OfType<RequestVoteResponse>().Single();
        return reply.VoteGranted;
    }

    [Fact]
    public void AVoterDeniesACandidateWhoseLogIsLessUpToDateAndGrantsOneAtLeastAsUpToDate()
    {
        Assert.False(Grants(5, T1));
        Assert.False(Grants(1, T2));
        Assert.True(Grants(2, T2));
        Assert.True(Grants(1, T3));
    }

    /// <summary>
    /// Figure 8 on three nodes, with one entry per AppendEntries. n1 leads term 1; its entry e at 2
    /// reaches no one before it crashes. n3 leads term 2; its no-op f at 2 reaches no one before it
    /// crashes. n1 returns and leads term 3, and e reaches n2 alone, on a majority but only through a
    /// term-3 copy; n1 crashes before its term-3 no-op follows. n3 returns and leads term 4, and f
    /// overwrites e on n2. e must never have been claimed committed.
    /// </summary>
    private static ManualCluster Figure8(RaftOptions options, out IReadOnlyList<StoredEntry> n2AfterTerm3)
    {
        var c = new ManualCluster(options);
        c.Stand(N1);
        c.Deliver(N1, N2);
        c.Deliver(N1, N3);
        c.Deliver(N2, N1);
        c.Deliver(N3, N1);
        c.Settle(All);
        c.Tick(N1, 50);
        c.Settle(All);

        c.Client(N1, "Put|x|e");
        c.Drop(N1);
        c.Crash(N1);

        c.Tick(N2, 200);
        c.Stand(N3);
        c.Deliver(N3, N2);
        c.Deliver(N2, N3);
        c.Drop(N3);
        c.Crash(N3);

        c.Restart(N1);
        var t = c.Stand(N1);
        c.Deliver(N1, N2);
        c.Deliver(N2, N1);
        c.Stand(N1, above: t);
        c.Deliver(N1, N2);
        c.Deliver(N2, N1);
        c.Deliver(N1, N2);
        c.Deliver(N2, N1);
        c.Deliver(N1, N2);
        c.Deliver(N2, N1);
        n2AfterTerm3 = c.EntriesOf(N2);
        c.Drop(N1);
        c.Crash(N1);

        c.Restart(N3);
        var u = c.Stand(N3);
        c.Tick(N2, 200);
        c.Deliver(N3, N2);
        c.Deliver(N2, N3);
        c.Stand(N3, above: u);
        c.Deliver(N3, N2);
        c.Deliver(N2, N3);
        c.Settle(N2, N3);
        c.Tick(N3, 50);
        c.Settle(N2, N3);
        return c;
    }

    private static string Command(StoredEntry e) => Encoding.ASCII.GetString(e.Command);

    [Fact]
    public void Figure8OnThreeNodesNeverCommitsTheOldTermsEntryByCountingCopies()
    {
        var c = Figure8(RaftOptions.Default with { MaxEntriesPerAppend = 1 }, out var n2AfterTerm3);
        var (_, log) = c.Histories();
        foreach (var r in LogInvariants.All(log).OrderBy(r => r.Invariant == "no-spurious-commit" ? 0 : 1))
        {
            Assert.True(r.Holds, $"{r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
        }

        // The shape happened: e on n2 after term 3 (without the term-3 no-op), then f in its place.
        Assert.Equal("Put|x|e", Command(n2AfterTerm3[1]));
        Assert.Equal(2, n2AfterTerm3.Count);
        Assert.Equal(Role.Leader, c.RoleOf(N3));
        var n2 = c.EntriesOf(N2);
        Assert.Equal(new Term(2), n2[1].Term);
        Assert.Equal(3, LogInvariants.Check(log, "no-spurious-commit").Count("entries-committed"));
    }

    /// <summary>
    /// With the default batch the old entry travels with the new leader's no-op, in one write, so it is
    /// committed as soon as it is on a majority, legitimately, and the later candidate is refused: the
    /// Figure 8 shape cannot occur here, and a commit-by-counting bug is masked (the finding in the
    /// phase report). Recorded as a test so that the masking stays visible.
    /// </summary>
    [Fact]
    public void WithTheDefaultBatchTheOldEntryNeverReachesAFollowerWithoutTheNoOp()
    {
        var c = Figure8(RaftOptions.Default, out var n2AfterTerm3);
        Assert.Equal(3, n2AfterTerm3.Count);
        Assert.Equal("Put|x|e", Command(n2AfterTerm3[1]));
        Assert.Equal(T3, n2AfterTerm3[2].Term);
        Assert.NotEqual(Role.Leader, c.RoleOf(N3));
        var (_, log) = c.Histories();
        foreach (var r in LogInvariants.All(log))
        {
            Assert.True(r.Holds, $"{r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
        }
    }

    /// <summary>n1 commits two entries on n1 and n2 and crashes; n3, which has none of them, stands and must be refused.</summary>
    [Fact]
    public void ACandidateMissingACommittedEntryIsRefused()
    {
        var c = new ManualCluster(RaftOptions.Default);
        c.Stand(N1);
        c.Deliver(N1, N2);
        c.Drop(N1, N3);
        c.Deliver(N2, N1);
        c.Settle(N1, N2);
        c.Client(N1, "Put|x|1");
        c.Settle(N1, N2);
        c.Drop(N1, N3);
        c.Crash(N1);

        var t = c.Stand(N3);
        c.Tick(N2, 200);
        c.Deliver(N3, N2);
        c.Deliver(N2, N3);
        c.Stand(N3, above: t);
        c.Deliver(N3, N2);
        c.Deliver(N2, N3);

        var (_, log) = c.Histories();
        foreach (var r in LogInvariants.All(log).OrderBy(r => r.Invariant == "leader-completeness" ? 0 : 1))
        {
            Assert.True(r.Holds, $"{r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
        }

        // The shape: n2 holds both committed entries, n3 none of them.
        Assert.Equal(2, c.EntriesOf(N2).Count);
        Assert.Empty(c.EntriesOf(N3));

        Assert.Equal(2, LogInvariants.Check(log, "leader-completeness").Count("entries-committed"));
        Assert.NotEqual(Role.Leader, c.RoleOf(N3));
    }
}
