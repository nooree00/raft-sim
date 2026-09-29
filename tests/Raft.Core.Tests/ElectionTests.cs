using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Core;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P3-05: leader election, as hand-built input sequences with the expected effects. Written first
/// and run against a stub node that returns no effects: every test failed there (the red run is in
/// docs/findings.md). Vacuity risk: a test that asserts only the absence of a wrong effect passes on
/// a node that does nothing, which is the stub; each test therefore requires an effect that happened.
/// Sabotages S-elect-1..4.
/// </summary>
public sealed class ElectionTests
{
    private static readonly NodeId N1 = new(1), N2 = new(2), N3 = new(3);

    /// <summary>Returns the given values in turn, then zeros: an election timeout of min + value.</summary>
    private sealed class FixedRandom(params ulong[] values) : IRandomSource
    {
        private int _i;

        public ulong NextUInt64() => _i < values.Length ? values[_i++] : 0;
    }

    private static RaftNode Node(byte[]? termVote = null, params ulong[] random)
    {
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        if (termVote is not null)
        {
            files[TermVoteLog.FileName] = termVote;
        }

        return new RaftNode(new NodeContext(N1, [N2, N3], new FixedRandom(random), files));
    }

    private static IReadOnlyList<Effect> Tick(RaftNode n, long elapsed) => n.Handle(new Tick(elapsed));

    private static IReadOnlyList<Effect> Receive(RaftNode n, NodeId from, Message m) => n.Handle(new Receive(from, MessageCodec.Encode(m)));

    private static List<(NodeId To, Message M)> Sends(IEnumerable<Effect> effects) =>
        effects.OfType<Send>().Select(s => (s.To, MessageCodec.Decode(s.Payload.ToArray())!)).ToList();

    private static List<TermVoteState> Persisted(IEnumerable<Effect> effects) =>
        effects.OfType<PersistAppend>().Where(p => p.File == TermVoteLog.FileName).Select(p => TermVoteLog.Recover(p.Data.ToArray()).State).ToList();

    /// <summary>Ticks until the node sends its first RequestVote; returns the effects of that tick.</summary>
    private static IReadOnlyList<Effect> Elect(RaftNode n)
    {
        for (var i = 0; i < 400; i++)
        {
            var e = Tick(n, 1);
            if (Sends(e).Any(s => s.M is RequestVote))
            {
                return e;
            }
        }

        throw new InvalidOperationException("no election within 400 ticks");
    }

    [Fact]
    public void AFollowerThatHearsNothingStandsPersistingItsVoteBeforeAskingForVotes()
    {
        var n = Node(random: 10);
        Assert.Empty(Sends(Tick(n, 159)));

        var e = Tick(n, 1);

        var persistAt = e.ToList().FindIndex(x => x is PersistAppend);
        var firstSend = e.ToList().FindIndex(x => x is Send);
        Assert.Equal([new TermVoteState(new Term(1), N1)], Persisted(e));
        Assert.True(persistAt >= 0 && persistAt < firstSend, "the vote for itself must be persisted before any RequestVote leaves (spec §8)");
        Assert.Equal([N2, N3], Sends(e).Where(s => s.M is RequestVote rv && rv.Term == new Term(1) && rv.Candidate == N1).Select(s => s.To).OrderBy(n => n.Value).ToList());
    }

    [Fact]
    public void TheElectionTimeoutIsDrawnFromItsRangeAndRedrawnEachElection()
    {
        var n = Node(random: [149, 0]);
        Assert.Empty(Sends(Tick(n, 298)));
        Assert.Contains(Sends(Tick(n, 1)), s => s.M is RequestVote);

        Assert.DoesNotContain(Sends(Tick(n, 149)), s => s.M is RequestVote);
        Assert.Equal(new Term(2), Assert.IsType<RequestVote>(Sends(Tick(n, 1)).First().M).Term);
    }

    [Fact]
    public void ACandidateWithAQuorumBecomesLeaderAndSendsHeartbeatsAtOnceAndThenEveryInterval()
    {
        var n = Node();
        Elect(n);

        var won = Receive(n, N2, new RequestVoteResponse(new Term(1), true));

        Assert.Equal([N2, N3], Sends(won).Where(s => s.M is AppendEntries a && a.Leader == N1 && a.Term == new Term(1) && a.Entries.Count == 0).Select(s => s.To).OrderBy(n => n.Value).ToList());
        Assert.Empty(Sends(Tick(n, 49)));
        Assert.Equal(2, Sends(Tick(n, 1)).Count(s => s.M is AppendEntries));
    }

    [Fact]
    public void ADeniedOrStaleVoteDoesNotElect()
    {
        var n = Node();
        Elect(n);

        Assert.DoesNotContain(Sends(Receive(n, N2, new RequestVoteResponse(new Term(1), false))), s => s.M is AppendEntries);
        Assert.DoesNotContain(Sends(Receive(n, N3, new RequestVoteResponse(Term.Zero, true))), s => s.M is AppendEntries);
        Assert.Equal(2, Sends(Receive(n, N3, new RequestVoteResponse(new Term(1), true))).Count(s => s.M is AppendEntries));
    }

    [Fact]
    public void AFollowerGrantsItsVotePersistingItBeforeReplying()
    {
        var n = Node();
        var e = Receive(n, N2, new RequestVote(new Term(1), N2, 0, Term.Zero));

        Assert.Equal([new TermVoteState(new Term(1), N2)], Persisted(e));
        Assert.True(e.ToList().FindIndex(x => x is PersistAppend) < e.ToList().FindIndex(x => x is Send), "persisted before the reply");
        Assert.Equal([(N2, (Message)new RequestVoteResponse(new Term(1), true))], Sends(e));
    }

    [Fact]
    public void ASecondCandidateInTheSameTermIsDeniedAndNothingIsPersisted()
    {
        var n = Node();
        Receive(n, N2, new RequestVote(new Term(1), N2, 0, Term.Zero));

        var e = Receive(n, N3, new RequestVote(new Term(1), N3, 0, Term.Zero));

        Assert.Empty(Persisted(e));
        Assert.Equal([(N3, (Message)new RequestVoteResponse(new Term(1), false))], Sends(e));
    }

    [Fact]
    public void AStaleRequestVoteIsDeniedWithTheCurrentTerm()
    {
        var n = Node(TermVoteLog.Record(new Term(5), null));

        Assert.Equal([(N2, (Message)new RequestVoteResponse(new Term(5), false))], Sends(Receive(n, N2, new RequestVote(new Term(4), N2, 0, Term.Zero))));
    }

    [Fact]
    public void AVoteSurvivesARestart()
    {
        var n = Node(TermVoteLog.Record(new Term(3), N2));

        Assert.Equal([(N3, (Message)new RequestVoteResponse(new Term(3), false))], Sends(Receive(n, N3, new RequestVote(new Term(3), N3, 0, Term.Zero))));
        Assert.Equal([(N2, (Message)new RequestVoteResponse(new Term(3), true))], Sends(Receive(n, N2, new RequestVote(new Term(3), N2, 0, Term.Zero))));
    }

    [Fact]
    public void ALeaderSeeingAHigherTermStepsDownPersistsTheTermAndStopsHeartbeats()
    {
        var n = Node();
        Elect(n);
        Receive(n, N2, new RequestVoteResponse(new Term(1), true));

        var e = Receive(n, N3, new AppendEntries(new Term(4), N3, 0, Term.Zero, [], 0));

        Assert.Equal([new TermVoteState(new Term(4), null)], Persisted(e));
        Assert.Equal([(N3, (Message)new AppendEntriesResponse(new Term(4), true, 0))], Sends(e));
        Assert.DoesNotContain(Sends(Tick(n, 100)), s => s.M is AppendEntries);
    }

    [Fact]
    public void HeartbeatsFromTheLeaderKeepAFollowerFromStanding()
    {
        var n = Node(random: 0);
        for (var t = 0; t < 10; t++)
        {
            Assert.DoesNotContain(Sends(Tick(n, 100)), s => s.M is RequestVote);
            Assert.Equal([(N2, (Message)new AppendEntriesResponse(new Term(1), true, 0))], Sends(Receive(n, N2, new AppendEntries(new Term(1), N2, 0, Term.Zero, [], 0))));
        }

        Assert.Contains(Sends(Tick(n, 150)), s => s.M is RequestVote);
    }

    [Fact]
    public void ACandidateHearingALeaderOfItsTermStepsDown()
    {
        var n = Node();
        Elect(n);

        Assert.Equal([(N2, (Message)new AppendEntriesResponse(new Term(1), true, 0))], Sends(Receive(n, N2, new AppendEntries(new Term(1), N2, 0, Term.Zero, [], 0))));
        Assert.DoesNotContain(Sends(Receive(n, N3, new RequestVoteResponse(new Term(1), true))), s => s.M is AppendEntries);
    }

    [Fact]
    public void AnAppendEntriesFromAStaleTermIsRejectedWithTheCurrentTerm()
    {
        var n = Node(TermVoteLog.Record(new Term(5), null));

        Assert.Equal([(N2, (Message)new AppendEntriesResponse(new Term(5), false, 0))], Sends(Receive(n, N2, new AppendEntries(new Term(3), N2, 0, Term.Zero, [], 0))));
    }

    [Fact]
    public void ACorruptTermVoteFileRefusesToStart()
    {
        var bad = TermVoteLog.Record(new Term(1), null);
        bad[10] ^= 1;

        Assert.Throws<InvalidOperationException>(() => Node([.. bad, .. TermVoteLog.Record(new Term(2), null)]));
    }

    /// <summary>P3-06, the §6 disruption rule: a follower that heard its leader within the minimum election timeout disregards RequestVote.</summary>
    [Fact]
    public void AFollowerThatJustHeardItsLeaderIgnoresARequestVoteEntirely()
    {
        var n = Node();
        Receive(n, N2, new AppendEntries(new Term(1), N2, 0, Term.Zero, [], 0));
        Tick(n, 149);

        var e = Receive(n, N3, new RequestVote(new Term(2), N3, 0, Term.Zero));

        Assert.Empty(Sends(e));
        Assert.Empty(Persisted(e));
        Assert.Contains(e, x => x is Emit { Name: "requestvote-ignored" });
        Assert.Equal([(N2, (Message)new AppendEntriesResponse(new Term(1), true, 0))], Sends(Receive(n, N2, new AppendEntries(new Term(1), N2, 0, Term.Zero, [], 0))));
    }

    [Fact]
    public void OnceTheMinimumTimeoutPassesWithoutTheLeaderARequestVoteIsAnswered()
    {
        var n = Node(random: [100, 100]); // an election timeout of 250 before and after the heartbeat, so it does not stand first
        Receive(n, N2, new AppendEntries(new Term(1), N2, 0, Term.Zero, [], 0));
        Tick(n, 150);

        var e = Receive(n, N3, new RequestVote(new Term(2), N3, 0, Term.Zero));

        Assert.Equal([new TermVoteState(new Term(2), N3)], Persisted(e));
        Assert.Equal([(N3, (Message)new RequestVoteResponse(new Term(2), true))], Sends(e));
    }

    [Fact]
    public void ALeaderIgnoresRequestVote()
    {
        var n = Node();
        Elect(n);
        Receive(n, N2, new RequestVoteResponse(new Term(1), true));

        var e = Receive(n, N3, new RequestVote(new Term(7), N3, 0, Term.Zero));

        Assert.Empty(Sends(e));
        Assert.Equal(2, Sends(Tick(n, 50)).Count(s => s.M is AppendEntries a && a.Term == new Term(1)));
    }

    /// <summary>
    /// Found by the soak's first run (P3-08): recovery skipped a torn tail without cutting it off, the
    /// node appended after it, and the next crash met the torn record in the middle of the file,
    /// corruption, and refused to start. The node must truncate the tail before it appends.
    /// </summary>
    [Fact]
    public void ATornTailIsCutFromTheFileBeforeTheNodeAppendsAfterIt()
    {
        var whole = TermVoteLog.Record(new Term(2), N3);
        var n = Node([.. whole, .. TermVoteLog.Record(new Term(3), null)[..7]]);

        var e = Tick(n, 1);

        Assert.True(e.Count > 0 && e[0] == new PersistTruncate(TermVoteLog.FileName, whole.Length),
            "the first effect after recovering a torn tail must cut it off; got: " + string.Join(", ", e));
        Assert.Empty(Tick(n, 1).OfType<PersistTruncate>());
    }

    /// <summary>The whole sequence on the simulated disk: a torn crash, a restart, a new record, a second crash, a clean recovery.</summary>
    [Fact]
    public void AfterATornCrashTheNextRecordIsReadableThroughTheNextRestart()
    {
        var disk = new Raft.Simulation.SimDisk();
        disk.Issue(new PersistAppend(TermVoteLog.FileName, TermVoteLog.Record(new Term(1), N1)), 0);
        disk.CompleteNext();
        disk.Issue(new PersistAppend(TermVoteLog.FileName, TermVoteLog.Record(new Term(2), N1)), 0);
        disk.Crash(Raft.Simulation.DiskLoss.Torn, () => 3);

        var files = disk.Snapshot();
        var node = new RaftNode(new NodeContext(N1, [N2, N3], new FixedRandom(), files));
        foreach (var p in node.Handle(new Receive(N2, MessageCodec.Encode(new RequestVote(new Term(5), N2, 0, Term.Zero)))).OfType<Persist>())
        {
            disk.Issue(p, 0);
            disk.CompleteNext();
        }

        var r = TermVoteLog.Recover(disk.Snapshot()[TermVoteLog.FileName].ToArray());
        Assert.True(r.Path == RecoveryPath.Clean, $"after torn crash, restart and a new record: {r.Path} ({r.Detail})");
        Assert.Equal(new TermVoteState(new Term(5), N2), r.State);
    }
}
