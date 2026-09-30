using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Kv;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P4-03: log replication and commitment, as hand-built input sequences with the expected effects.
/// Written first and run against the phase-3 node, which has no log: the red run is in the phase
/// report. Vacuity risk: a test that asserts only the absence of a wrong effect passes on a node that
/// does nothing; each test therefore requires the effect that must happen, and the order of persist
/// and send where spec §8 fixes it. Sabotages S-repl-1..6.
/// </summary>
public sealed class ReplicationTests
{
    private static readonly NodeId N1 = new(1), N2 = new(2), N3 = new(3);
    private static readonly Term T1 = new(1), T2 = new(2);

    private sealed class ZeroRandom : IRandomSource
    {
        public ulong NextUInt64() => 0;
    }

    private static byte[] Cmd(string s) => Encoding.ASCII.GetBytes(s);

    private static LogEntry E(Term t, string command) => new(t, Cmd(command));

    /// <summary>A log file holding the given entries, each chained to the one before.</summary>
    private static byte[] LogFile(params LogEntry[] entries)
    {
        var store = new LogStore(EntryLog.Recover(null));
        return store.Append(entries).Data.ToArray();
    }

    private static RaftNode Node(NodeId id, byte[]? log = null, byte[]? termVote = null)
    {
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        if (log is not null)
        {
            files[EntryLog.FileName] = log;
        }

        if (termVote is not null)
        {
            files[TermVoteLog.FileName] = termVote;
        }

        return new RaftNode(new NodeContext(id, new[] { N1, N2, N3 }.Where(p => p != id).ToList(), new ZeroRandom(), files), RaftOptions.Default, new KvStateMachine());
    }

    private static IReadOnlyList<Effect> Receive(RaftNode n, NodeId from, Message m) => n.Handle(new Receive(from, MessageCodec.Encode(m)));

    private static List<(NodeId To, Message M)> Sends(IEnumerable<Effect> effects) =>
        effects.OfType<Send>().Select(s => (s.To, MessageCodec.Decode(s.Payload.ToArray())!)).ToList();

    private static List<AppendEntries> AppendsTo(IEnumerable<Effect> effects, NodeId to) =>
        Sends(effects).Where(s => s.To == to).Select(s => s.M).OfType<AppendEntries>().ToList();

    private static AppendEntriesResponse Reply(IEnumerable<Effect> effects) =>
        Sends(effects).Select(s => s.M).OfType<AppendEntriesResponse>().Single();

    private static int IndexOf<T>(IReadOnlyList<Effect> effects, Func<T, bool>? match = null)
        where T : Effect => effects.ToList().FindIndex(e => e is T t && (match is null || match(t)));

    private static List<long> Applied(IEnumerable<Effect> effects) =>
        effects.OfType<Emit>().Where(e => e.Name == "apply").Select(e => long.Parse(e.Fields.Single(f => f.Key == "index").Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();

    /// <summary>Makes n1 leader of term 1: ticks to its election timeout, then n2's vote.</summary>
    private static IReadOnlyList<Effect> Lead(RaftNode n, Term term)
    {
        for (var i = 0; i < 400; i++)
        {
            if (Sends(n.Handle(new Tick(1))).Any(s => s.M is RequestVote))
            {
                return Receive(n, N2, new RequestVoteResponse(term, true));
            }
        }

        throw new InvalidOperationException("no election within 400 ticks");
    }

    [Fact]
    public void ANewLeaderAppendsANoOpPersistingItBeforeReplicatingIt()
    {
        var n = Node(N1);
        var won = Lead(n, T1);

        var heartbeat = IndexOf<Send>(won);
        var persist = IndexOf<PersistAppend>(won, p => p.File == EntryLog.FileName);
        var carrying = won.ToList().FindIndex(x => x is Send s && MessageCodec.Decode(s.Payload.ToArray()) is AppendEntries { Entries.Count: > 0 });
        Assert.True(heartbeat >= 0 && heartbeat < persist, "an empty heartbeat leaves before the no-op is persisted, so a slow disk cannot keep a new leader silent");
        Assert.True(persist < carrying, "the leader's no-op must be persisted before it is sent (spec §8)");
        var sent = AppendsTo(won, N2);
        Assert.Equal(2, sent.Count);
        Assert.Empty(sent[0].Entries);
        Assert.Equal(0, sent[1].PrevLogIndex);
        Assert.Equal([E(T1, "")], sent[1].Entries);
    }

    [Fact]
    public void AFollowerPersistsNewEntriesBeforeAcknowledgingThem()
    {
        var n = Node(N2);
        var e = Receive(n, N1, new AppendEntries(T1, N1, 0, Term.Zero, [E(T1, ""), E(T1, "Put|x|1")], 0));

        var persist = IndexOf<PersistAppend>(e, p => p.File == EntryLog.FileName);
        var send = IndexOf<Send>(e);
        Assert.True(persist >= 0 && persist < send, "entries must be durable before the follower acknowledges them");
        var r = Reply(e);
        Assert.True(r.Success);
        Assert.Equal(2, r.MatchIndex);
    }

    [Fact]
    public void AFollowerRejectsEntriesWhosePredecessorItDoesNotHold()
    {
        var n = Node(N2, LogFile(E(T1, "a")));
        var missing = Receive(n, N1, new AppendEntries(T2, N1, 2, T1, [E(T2, "b")], 0));
        var wrongTerm = Receive(n, N1, new AppendEntries(T2, N1, 1, T2, [E(T2, "b")], 0));

        Assert.False(Reply(missing).Success);
        Assert.False(Reply(wrongTerm).Success);
        Assert.DoesNotContain(missing.Concat(wrongTerm), x => x is PersistAppend { File: EntryLog.FileName } or PersistTruncate { File: EntryLog.FileName });
    }

    /// <summary>
    /// A follower truncates only at a real conflict. A stale AppendEntries that carries a prefix of
    /// what it already holds must not cut the entries after it: those may be committed.
    /// </summary>
    [Fact]
    public void AFollowerTruncatesOnlyAConflictingSuffixAndNeverOnAStalePrefix()
    {
        var n = Node(N2, LogFile(E(T1, "a"), E(T1, "b"), E(T1, "c")));
        var stale = Receive(n, N1, new AppendEntries(T1, N1, 0, Term.Zero, [E(T1, "a")], 0));
        Assert.DoesNotContain(stale, x => x is PersistTruncate { File: EntryLog.FileName });
        Assert.True(Reply(stale).Success);
        Assert.Equal(1, Reply(stale).MatchIndex);

        var conflict = Receive(n, N1, new AppendEntries(T2, N1, 1, T1, [E(T2, "B")], 0));
        var cut = IndexOf<PersistTruncate>(conflict, p => p.File == EntryLog.FileName);
        var append = IndexOf<PersistAppend>(conflict, p => p.File == EntryLog.FileName);
        var send = IndexOf<Send>(conflict);
        Assert.True(cut >= 0 && cut < append && append < send, "a conflicting suffix is cut, the new entry appended, then acknowledged");
        Assert.Equal(2, Reply(conflict).MatchIndex);
    }

    /// <summary>Figure 2's rule: a leader commits by majority only an entry of its own term, carrying older ones with it.</summary>
    [Fact]
    public void ALeaderCommitsByMajorityOnlyAnEntryOfItsOwnTerm()
    {
        var n = Node(N1, LogFile(E(T1, "Put|x|1")), TermVoteLog.Record(T1, null));
        Lead(n, T2);

        var old = Receive(n, N2, new AppendEntriesResponse(T2, true, 1));
        Assert.Empty(Applied(old));

        var current = Receive(n, N2, new AppendEntriesResponse(T2, true, 2));
        Assert.Equal([1, 2], Applied(current));
        var next = AppendsTo(n.Handle(new Tick(RaftOptions.Default.HeartbeatInterval)), N3);
        Assert.Equal(2, next.Single().LeaderCommit);
    }

    [Fact]
    public void ALeaderBacksOffOnARejectionAndRetriesAtOnce()
    {
        var n = Node(N1, LogFile(E(T1, "a"), E(T1, "b")), TermVoteLog.Record(T1, null));
        var won = Lead(n, T2);
        Assert.Equal(2, AppendsTo(won, N3)[^1].PrevLogIndex);

        var retry = AppendsTo(Receive(n, N3, new AppendEntriesResponse(T2, false, 1)), N3).Single();
        Assert.Equal(1, retry.PrevLogIndex);
        Assert.Equal(2, retry.Entries.Count);

        // A follower whose log ends far back says so, and the leader jumps there at once.
        var jump = AppendsTo(Receive(n, N2, new AppendEntriesResponse(T2, false, 0)), N2).Single();
        Assert.Equal(0, jump.PrevLogIndex);
        Assert.Equal(3, jump.Entries.Count);
    }

    [Fact]
    public void ARejectionSaysHowFarTheFollowersLogCouldMatch()
    {
        var n = Node(N2, LogFile(E(T1, "a"), E(T1, "b")));
        Assert.Equal(2, Reply(Receive(n, N1, new AppendEntries(T2, N1, 40, T2, [], 0))).MatchIndex);
        Assert.Equal(1, Reply(Receive(n, N1, new AppendEntries(T2, N1, 2, T2, [], 0))).MatchIndex);
    }

    [Fact]
    public void AClientRequestAtTheLeaderIsAnsweredOnlyAfterItCommitsAndIsApplied()
    {
        var n = Node(N1);
        Lead(n, T1);
        Receive(n, N2, new AppendEntriesResponse(T1, true, 1));

        var request = n.Handle(new ClientRequest(7, Cmd("Put|x|1")));
        Assert.True(IndexOf<PersistAppend>(request, p => p.File == EntryLog.FileName) >= 0);
        var ae = AppendsTo(request, N2).Single();
        Assert.Equal(2, ae.PrevLogIndex + ae.Entries.Count);
        Assert.Empty(request.OfType<ClientResponse>());

        var committed = Receive(n, N2, new AppendEntriesResponse(T1, true, 2));
        var answer = Assert.Single(committed.OfType<ClientResponse>());
        Assert.Equal(7, answer.RequestId);
        Assert.Equal("ok", Encoding.ASCII.GetString(answer.Payload.Span));
        Assert.Equal([2], Applied(committed));
    }

    [Fact]
    public void AClientRequestAtAFollowerIsRefusedWithTheLeaderItLastHeardFrom()
    {
        var n = Node(N2);
        var none = Assert.Single(n.Handle(new ClientRequest(1, Cmd("Put|x|1"))).OfType<ClientResponse>());
        Assert.Equal("redirect|-", Encoding.ASCII.GetString(none.Payload.Span));

        Receive(n, N1, new AppendEntries(T1, N1, 0, Term.Zero, [], 0));
        var hinted = Assert.Single(n.Handle(new ClientRequest(2, Cmd("Put|x|1"))).OfType<ClientResponse>());
        Assert.Equal("redirect|n1", Encoding.ASCII.GetString(hinted.Payload.Span));
        Assert.DoesNotContain(n.Handle(new ClientRequest(3, Cmd("Put|x|1"))), x => x is PersistAppend { File: EntryLog.FileName });
    }

    [Fact]
    public void AFollowerCommitsUpToTheLeadersCommitIndexButNoFurtherThanItsLastNewEntry()
    {
        var n = Node(N2, LogFile(E(T1, "a"), E(T1, "b"), E(T1, "c")));
        var e = Receive(n, N1, new AppendEntries(T1, N1, 0, Term.Zero, [E(T1, "a")], 3));
        Assert.Equal([1], Applied(e));
        Assert.Equal([2, 3], Applied(Receive(n, N1, new AppendEntries(T1, N1, 3, T1, [], 3))));
    }

    [Fact]
    public void ARestartedNodeRecoversItsLogAndCutsATornTailFirst()
    {
        var whole = LogFile(E(T1, "a"), E(T1, "b"));
        var n = Node(N2, whole[..^3]);
        var first = n.Handle(new Tick(1));
        Assert.True(first.Count > 0 && first[0] is PersistTruncate { File: EntryLog.FileName }, "the first effect after recovering a torn log tail must cut it");

        var r = Reply(Receive(n, N1, new AppendEntries(T1, N1, 1, T1, [], 0)));
        Assert.True(r.Success, "entry 1 survived the torn write of entry 2");
    }

    /// <summary>A leader deposed by a higher term persists that term before truncating its suffix at the new leader's word.</summary>
    [Fact]
    public void ADeposedLeaderPersistsTheNewTermBeforeTruncatingItsSuffix()
    {
        var n = Node(N1);
        Lead(n, T1);
        n.Handle(new ClientRequest(1, Cmd("Put|x|1")));

        var e = Receive(n, N2, new AppendEntries(T2, N2, 0, Term.Zero, [E(T2, "")], 0));
        var term = IndexOf<PersistAppend>(e, p => p.File == TermVoteLog.FileName);
        var cut = IndexOf<PersistTruncate>(e, p => p.File == EntryLog.FileName);
        Assert.True(term >= 0 && cut >= 0 && term < cut, "the term that deposed the leader must be persisted before the truncation it authorises");
    }

    /// <summary>
    /// P4-05: a leader persists a client's entry before the AppendEntries that carries it, so the barrier
    /// holds the send and the leader never counts a copy of its own that is not yet durable. The crash
    /// tests cannot see a leader that sends first (measured: S-dur-3 left them green): its own write
    /// takes at most 3 ticks, and a follower's acknowledgement at least 3 (two hops and a write), so
    /// the window in which the leader counts a copy it has not got is empty or a single tick.
    /// </summary>
    [Fact]
    public void ALeaderPersistsAClientsEntryBeforeSendingIt()
    {
        var n = Node(N1);
        Lead(n, T1);
        var request = n.Handle(new ClientRequest(1, Cmd("Put|x|1")));

        var persist = IndexOf<PersistAppend>(request, p => p.File == EntryLog.FileName);
        var carrying = request.ToList().FindIndex(x => x is Send s && MessageCodec.Decode(s.Payload.ToArray()) is AppendEntries { Entries.Count: > 0 });
        Assert.True(persist >= 0 && persist < carrying, "a client's entry must be persisted before the AppendEntries that carries it");
    }
}
