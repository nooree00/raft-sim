using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P3-04: the four checkers proven on hand-built traces before any Raft exists (spec §5, §12).
/// Every rejecting trace has an accepted near-miss twin, one step away, so a checker that rejects
/// everything fails too. Vacuity risk: a checker whose degenerate executions (no leader, one term,
/// no votes) all pass; guarded by the counts each checker reports, asserted here and in every
/// test that runs Raft. Sabotages S-inv-1..5.
/// </summary>
public sealed class ElectionInvariantTests
{
    private static readonly NodeId N1 = new(1), N2 = new(2), N3 = new(3);

    /// <summary>A hand-built execution of three nodes, all started at 0.</summary>
    private sealed class Trace
    {
        private readonly List<Observation> _o = [new StartObservation(0, N1, 1), new StartObservation(0, N2, 1), new StartObservation(0, N3, 1)];
        private readonly Dictionary<NodeId, List<byte>> _files = [];
        private long _id;

        public Trace Send(long t, NodeId from, NodeId to, Message m, bool deliver = true)
        {
            var id = ++_id;
            _o.Add(new SentObservation(t, from, to, id, MessageCodec.Encode(m)));
            if (deliver)
            {
                _o.Add(new DeliveredObservation(t + 1, to, from, id));
            }

            return this;
        }

        public Trace Grant(long t, NodeId voter, NodeId candidate, long term, bool deliver = true) =>
            Send(t, voter, candidate, new RequestVoteResponse(new Term(term), true), deliver);

        public Trace Heartbeat(long t, NodeId leader, long term) =>
            Send(t, leader, leader == N1 ? N2 : N1, new AppendEntries(new Term(term), leader, 0, Term.Zero, [], 0));

        /// <summary>A durable term-and-vote record appended on the node's disk.</summary>
        public Trace Persist(long t, NodeId node, long term, NodeId? vote)
        {
            if (!_files.TryGetValue(node, out var f))
            {
                _files[node] = f = [];
            }

            f.AddRange(TermVoteLog.Record(new Term(term), vote));
            _o.Add(new DurableObservation(t, node, TermVoteLog.FileName, f.ToArray()));
            return this;
        }

        /// <summary>The disk loses its last completed record (the lose-synced positive control's effect).</summary>
        public Trace LoseLastRecord(long t, NodeId node)
        {
            var f = _files[node];
            f.RemoveRange(f.Count - 20, 20);
            _o.Add(new CrashObservation(t, node));
            _o.Add(new DurableObservation(t, node, TermVoteLog.FileName, f.ToArray()));
            _o.Add(new StartObservation(t + 1, node, 2));
            return this;
        }

        public Trace Crash(long t, NodeId node)
        {
            _o.Add(new CrashObservation(t, node));
            return this;
        }

        public Trace Start(long t, NodeId node)
        {
            _o.Add(new StartObservation(t, node, 2));
            return this;
        }

        public ElectionHistory History() => new(_o, 3);
    }

    /// <summary>n1 wins term 2 with its own vote and n2's.</summary>
    private static Trace N1WinsTerm2() => new Trace()
        .Persist(10, N1, 2, N1).Send(11, N1, N2, new RequestVote(new Term(2), N1, 0, Term.Zero))
        .Persist(13, N2, 2, N1).Grant(14, N2, N1, 2).Heartbeat(16, N1, 2);

    private const long ActWithin = 150;

    [Fact]
    public void AnOrdinaryElectionHoldsAndIsCounted()
    {
        var h = N1WinsTerm2().History();
        var r = ElectionInvariants.ElectionSafety(h, ActWithin);

        Assert.True(r.Holds, string.Join("; ", r.Violations));
        Assert.Equal(1, r.Count("elections"));
        Assert.Equal(1, r.Count("acting-leaders"));
        Assert.True(ElectionInvariants.VoteUniqueness(h).Holds);
        Assert.True(ElectionInvariants.TermMonotonicity(h).Holds);
        Assert.Equal(2, ElectionInvariants.TermMonotonicity(h).Count("highest-term"));
    }

    [Fact]
    public void TwoCandidatesElectedInOneTermAreRejectedAndOneGrantShortIsNot()
    {
        // n3 also gathers a quorum in term 2: its own vote and n2's (n2 votes twice: also a vote-uniqueness failure).
        var bad = N1WinsTerm2().Persist(20, N3, 2, N3).Grant(21, N2, N3, 2).History();
        var twin = N1WinsTerm2().Persist(20, N3, 2, N3).History();

        Assert.Contains(ElectionInvariants.ElectionSafety(bad, ActWithin).Violations, v => v.StartsWith("term 2: elected", StringComparison.Ordinal));
        Assert.True(ElectionInvariants.ElectionSafety(twin, ActWithin).Holds);
    }

    /// <summary>A history property: the first leader of term 2 crashed before the second appeared; "one leader right now" misses it.</summary>
    [Fact]
    public void ALeaderThatCrashedStillCountsAgainstALaterOneInTheSameTerm()
    {
        var bad = N1WinsTerm2().Crash(30, N1).Persist(40, N3, 2, N3).Persist(41, N2, 2, N3).Grant(42, N2, N3, 2).History();

        Assert.Contains(ElectionInvariants.ElectionSafety(bad, ActWithin).Violations, v => v.StartsWith("term 2: elected", StringComparison.Ordinal));
    }

    [Fact]
    public void ActingAsLeaderWithoutAnObservedQuorumIsRejected()
    {
        var bad = new Trace().Persist(10, N1, 2, N1).Heartbeat(12, N1, 2).History();
        var twin = N1WinsTerm2().History();

        Assert.Contains(ElectionInvariants.ElectionSafety(bad, ActWithin).Violations, v => v.Contains("without an observed quorum", StringComparison.Ordinal));
        Assert.True(ElectionInvariants.ElectionSafety(twin, ActWithin).Holds);
    }

    [Fact]
    public void AnElectedNodeThatStaysUpAndNeverActsIsRejectedButOneThatCrashesIsNot()
    {
        Trace Elected() => new Trace().Persist(10, N1, 2, N1).Persist(13, N2, 2, N1).Grant(14, N2, N1, 2);
        var bad = Elected().Crash(500, N3).History();
        var twin = Elected().Crash(100, N1).History();

        Assert.Contains(ElectionInvariants.ElectionSafety(bad, ActWithin).Violations, v => v.Contains("never acts", StringComparison.Ordinal));
        Assert.True(ElectionInvariants.ElectionSafety(twin, ActWithin).Holds);
    }

    [Fact]
    public void TwoGrantsInOneTermAreRejectedAndGrantsInTwoTermsAreNot()
    {
        var bad = new Trace().Grant(10, N2, N1, 3).Grant(20, N2, N3, 3).History();
        var twin = new Trace().Grant(10, N2, N1, 3).Grant(20, N2, N3, 4).History();

        Assert.Contains(ElectionInvariants.VoteUniqueness(bad).Violations, v => v.StartsWith("n2 voted for n1 and n3 in term 3", StringComparison.Ordinal));
        Assert.True(ElectionInvariants.VoteUniqueness(twin).Holds);
    }

    /// <summary>
    /// P3-04's prediction, case 1: a grant whose response is dropped. Counting only delivered grants
    /// misses the second vote; the sent grants show it.
    /// </summary>
    [Fact]
    public void ADroppedGrantIsStillAVote()
    {
        var h = new Trace().Grant(10, N2, N1, 3, deliver: false).Grant(20, N2, N3, 3).History();

        Assert.True(ElectionInvariants.VoteUniqueness(h, deliveredOnly: true).Holds);
        Assert.False(ElectionInvariants.VoteUniqueness(h).Holds);
    }

    /// <summary>
    /// Case 2: a candidate's vote for itself is durable and never sent. It loses the record (the
    /// lose-synced fault), restarts in the same term, and grants another candidate: the wire shows one
    /// vote, only the durable records show two.
    /// </summary>
    [Fact]
    public void ASelfVoteLostFromDiskAndThenGivenAwayIsOnlyVisibleOnDisk()
    {
        var h = new Trace().Persist(10, N1, 5, N1).LoseLastRecord(20, N1).Persist(30, N1, 5, N3).Grant(31, N1, N3, 5).History();

        Assert.True(ElectionInvariants.VoteUniqueness(h, wireOnly: true).Holds);
        Assert.Contains(ElectionInvariants.VoteUniqueness(h).Violations, v => v.StartsWith("n1 voted for n1 and n3 in term 5", StringComparison.Ordinal));
    }

    [Fact]
    public void ADurableTermThatGoesBackIsRejectedAndOneThatStaysIsNot()
    {
        var bad = new Trace().Persist(10, N1, 4, null).Persist(20, N1, 5, N1).LoseLastRecord(30, N1).History();
        var twin = new Trace().Persist(10, N1, 4, null).Persist(20, N1, 5, N1).Crash(30, N1).Start(31, N1).History();

        Assert.Contains(ElectionInvariants.TermMonotonicity(bad).Violations, v => v.Contains("durable term 4 after term 5", StringComparison.Ordinal));
        Assert.True(ElectionInvariants.TermMonotonicity(twin).Holds);
    }

    [Fact]
    public void AMessageSentWithATermBelowTheNodesDurableTermIsRejected()
    {
        var bad = new Trace().Persist(10, N1, 5, null).Send(20, N1, N2, new RequestVote(new Term(4), N1, 0, Term.Zero)).History();
        var twin = new Trace().Persist(10, N1, 5, null).Send(20, N1, N2, new RequestVote(new Term(5), N1, 0, Term.Zero)).History();

        Assert.Contains(ElectionInvariants.TermMonotonicity(bad).Violations, v => v.Contains("sent RequestVote 4 after term 5", StringComparison.Ordinal));
        Assert.True(ElectionInvariants.TermMonotonicity(twin).Holds);
    }

    [Fact]
    public void LivenessNeedsAnElectedLeaderActingInTheWindowAfterTheLastFault()
    {
        var ok = N1WinsTerm2().Heartbeat(1_100, N1, 2).History();
        var late = N1WinsTerm2().Heartbeat(5_000, N1, 2).History();

        var r = ElectionInvariants.Liveness(ok, stableFrom: 1_000, end: 10_000, window: 1_000, minimumSuffix: 2_000);
        Assert.True(r.Holds);
        Assert.Equal(100, r.Count("time-to-leader"));
        Assert.Contains(ElectionInvariants.Liveness(late, 1_000, 10_000, 1_000, 2_000).Violations, v => v.StartsWith("no elected leader acted", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExecutionWithNoStableSuffixIsCountedNotPassed()
    {
        var shortSuffix = ElectionInvariants.Liveness(N1WinsTerm2().History(), 9_000, 10_000, 1_000, 2_000);
        var minority = ElectionInvariants.Liveness(N1WinsTerm2().Crash(500, N2).Crash(600, N3).History(), 1_000, 10_000, 1_000, 2_000);

        Assert.Equal((1, 0), (shortSuffix.Count("no-stable-suffix"), shortSuffix.Count("checked")));
        Assert.Equal((1, 0), (minority.Count("no-stable-suffix"), minority.Count("checked")));
    }
}
