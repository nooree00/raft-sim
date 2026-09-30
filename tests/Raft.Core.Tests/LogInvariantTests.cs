using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Core;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P4-01: the log invariants proven on hand-built traces before any log exists in Core (spec §5,
/// §12). Each rejecting trace has an accepted twin one step away. Vacuity risk: every one of these
/// checkers is satisfied by empty logs, one term or no commits; guarded by the healthy trace, which
/// must commit entries in two terms, and by the counts each checker reports. Sabotages
/// S-loginv-1..7, S-ghost-1.
/// </summary>
public sealed class LogInvariantTests
{
    private static readonly NodeId N1 = new(1), N2 = new(2), N3 = new(3);

    /// <summary>
    /// A hand-built execution of three nodes. It records raw events only, as the observation adapter
    /// will: writes, disks, messages, deliveries. It never assigns ghost ids; it tracks each node's
    /// log only to build the messages a node would send.
    /// </summary>
    private sealed class Trace
    {
        private readonly List<LogHistory.Event> _e = [];
        private readonly Dictionary<NodeId, List<LogEntryAt>> _logs = new() { [N1] = [], [N2] = [], [N3] = [] };
        private readonly Dictionary<NodeId, List<LogEntryAt>> _disks = new() { [N1] = [], [N2] = [], [N3] = [] };
        private long _seq, _step, _id;

        private long Seq => ++_seq;

        public Trace Elect(NodeId n, long term)
        {
            _e.Add(new LogHistory.Elected(Seq, n, new Term(term)));
            return this;
        }

        public Trace Leave(NodeId n, long term)
        {
            _e.Add(new LogHistory.LeftTerm(Seq, n, new Term(term)));
            return this;
        }

        /// <summary>The node appends a new entry at the end of its log, in a step of its own (a creation).</summary>
        public Trace Create(NodeId n, long term, string command = "put x 1", long? at = null)
        {
            var log = _logs[n];
            var index = at ?? (log.Count + 1);
            var e = new LogEntryAt(index, new Term(term), System.Text.Encoding.ASCII.GetBytes(command));
            log.RemoveAll(x => x.Index >= index);
            log.Add(e);
            _e.Add(new LogHistory.Issued(Seq, n, ++_step, 0, [e]));
            return this;
        }

        /// <summary>
        /// `from` sends `to` an AppendEntries of term <paramref name="term"/> carrying its entries after
        /// <paramref name="prev"/>; `to` truncates any conflicting suffix and appends them in the step
        /// the delivery starts.
        /// </summary>
        public Trace Replicate(NodeId from, NodeId to, long term, long prev = 0, long commit = 0)
        {
            var entries = _logs[from].Where(x => x.Index > prev).OrderBy(x => x.Index).ToList();
            var prevTerm = _logs[from].FirstOrDefault(x => x.Index == prev)?.Term ?? Term.Zero;
            var id = ++_id;
            _e.Add(new LogHistory.Sent(Seq, from, id, new AppendEntries(new Term(term), from, prev, prevTerm, entries.Select(x => new LogEntry(x.Term, x.Command)).ToList(), commit)));
            var step = ++_step;
            _e.Add(new LogHistory.Delivered(Seq, to, id, step));
            var log = _logs[to];
            var conflict = entries.Select(x => x.Index).Where(i => log.Any(y => y.Index == i && y.Term != entries.First(z => z.Index == i).Term)).DefaultIfEmpty(0).Min();
            log.RemoveAll(x => x.Index > prev);
            log.AddRange(entries);
            _e.Add(new LogHistory.Issued(Seq, to, step, conflict, entries));
            return this;
        }

        /// <summary>A leader's claim of commitment, on a heartbeat.</summary>
        public Trace Claim(NodeId leader, long term, long commit)
        {
            _e.Add(new LogHistory.Sent(Seq, leader, ++_id, new AppendEntries(new Term(term), leader, 0, Term.Zero, [], commit)));
            return this;
        }

        /// <summary>Everything the node has issued becomes durable.</summary>
        public Trace Durable(NodeId n) => Disk(n, _logs[n].OrderBy(x => x.Index).ToList());

        /// <summary>The node's disk now holds <paramref name="log"/>: recorded as the change from what it held.</summary>
        private Trace Disk(NodeId n, List<LogEntryAt> log)
        {
            var (cut, added) = LogHistory.Change(_disks[n], log);
            _disks[n] = log;
            _e.Add(new LogHistory.Durable(Seq, n, cut, added));
            return this;
        }

        /// <summary>The node crashes, its disk keeping the first <paramref name="keep"/> entries (all, when null).</summary>
        public Trace Crash(NodeId n, int? keep = null)
        {
            _e.Add(new LogHistory.Crashed(Seq, n));
            if (keep is { } k)
            {
                _logs[n] = _logs[n].OrderBy(x => x.Index).Take(k).ToList();
                Disk(n, _logs[n].ToList());
            }

            return this;
        }

        /// <summary>A write the node issues with no delivery behind it: truncate from an index.</summary>
        public Trace Truncate(NodeId n, long from)
        {
            _logs[n].RemoveAll(x => x.Index >= from);
            _e.Add(new LogHistory.Issued(Seq, n, ++_step, from, []));
            return this;
        }

        public Trace Apply(NodeId n, long index)
        {
            _e.Add(new LogHistory.Applied(Seq, n, index));
            return this;
        }

        public LogHistory History() => new(3, _e);
    }

    /// <summary>n1 leads term 1 and commits two entries on n1 and n2; n2 leads term 2 and commits a third on all three.</summary>
    private static Trace Healthy() => new Trace()
        .Elect(N1, 1).Create(N1, 1).Create(N1, 1, "append x a").Durable(N1)
        .Replicate(N1, N2, 1).Durable(N2).Claim(N1, 1, 2).Apply(N1, 1).Apply(N1, 2)
        .Replicate(N1, N2, 1, prev: 2, commit: 2).Apply(N2, 1).Apply(N2, 2)
        .Leave(N1, 1).Elect(N2, 2).Create(N2, 2, "put y 2").Durable(N2)
        .Replicate(N2, N1, 2).Durable(N1).Replicate(N2, N3, 2).Durable(N3)
        .Claim(N2, 2, 3).Apply(N2, 3);

    private static void Holds(LogHistory h, params string[] names)
    {
        foreach (var name in names.Length == 0 ? LogAnalysis.Names : names)
        {
            var r = LogInvariants.Check(h, name);
            Assert.True(r.Holds, $"{name}: {string.Join("; ", r.Violations)}");
        }
    }

    private static void Rejects(LogHistory h, string name, string startsWith)
    {
        var r = LogInvariants.Check(h, name);
        Assert.True(r.Violations.Any(v => v.StartsWith(startsWith, StringComparison.Ordinal)), $"{name}: expected a violation starting '{startsWith}', got [{string.Join("; ", r.Violations)}]");
    }

    [Fact]
    public void AHealthyExecutionHoldsEveryInvariantAndCommitsInTwoTerms()
    {
        var h = Healthy().History();
        Holds(h);
        var r = LogInvariants.Check(h, "no-spurious-commit");
        Assert.Equal(3, r.Count("entries-committed"));
        Assert.Equal(2, r.Count("terms-with-a-commit"));
        Assert.Equal(3, r.Count("entries-created"));
        Assert.Equal(1, r.Count("leaders-with-entries"));
        Assert.True(r.Count("applies") >= 5 && r.Count("claims") >= 2, "the healthy trace must claim and apply");
    }

    /// <summary>Ghost ids by provenance: a replicated entry is the entry its leader created, not a new one.</summary>
    [Fact]
    public void AReplicatedEntryCarriesItsCreatorsGhostId()
    {
        var a = new LogAnalysis(Healthy().History());
        Assert.NotNull(a.GhostAt(N1, 3));
        Assert.Equal(a.GhostAt(N2, 3), a.GhostAt(N1, 3));
        Assert.Equal(a.GhostAt(N2, 3), a.GhostAt(N3, 3));
        Assert.Equal(a.GhostAt(N1, 1), a.GhostAt(N3, 1));
    }

    [Fact]
    public void ALeaderThatTruncatesItsOwnLogIsRejectedAndAFollowerThatDoesIsNot()
    {
        Rejects(new Trace().Elect(N1, 1).Create(N1, 1).Create(N1, 1).Truncate(N1, 2).History(), "leader-append-only", "n1, leader of term 1, truncated its log from 2");
        Holds(new Trace().Elect(N1, 1).Create(N1, 1).Create(N1, 1).Replicate(N1, N2, 1).Truncate(N2, 2).History(), "leader-append-only");
    }

    [Fact]
    public void LogsSharingAnEntryButDifferingBeforeItAreRejected()
    {
        // n3 leads term 2 without n1's term-1 entry at 1, creates its own there, and gives n2 a
        // term-2 entry at 2 on top of n2's term-1 entry at 1: a skipped consistency check.
        var bad = new Trace().Elect(N1, 1).Create(N1, 1).Replicate(N1, N2, 1).Durable(N2).Leave(N1, 1)
            .Elect(N3, 2).Create(N3, 2).Create(N3, 2).Durable(N3).Replicate(N3, N2, 2, prev: 1).Durable(N2).History();
        Rejects(bad, "log-matching", "n2 and n3 share an entry at 2 but differ at 1");

        var twin = new Trace().Elect(N1, 1).Create(N1, 1).Replicate(N1, N2, 1).Durable(N2).Leave(N1, 1)
            .Elect(N3, 2).Create(N3, 2).Create(N3, 2).Durable(N3).Replicate(N3, N2, 2).Durable(N2).History();
        Holds(twin, "log-matching");
    }

    [Fact]
    public void ALeaderOfALaterTermWithoutACommittedEntryIsRejected()
    {
        var committed = new Trace().Elect(N1, 1).Create(N1, 1).Durable(N1).Replicate(N1, N2, 1).Durable(N2).Leave(N1, 1);
        Rejects(committed.Elect(N3, 2).History(), "leader-completeness", "n3, leader of term 2 (elected), lacks the entry committed in fact at 1");

        var twin = new Trace().Elect(N1, 1).Create(N1, 1).Durable(N1).Replicate(N1, N2, 1).Durable(N2).Leave(N1, 1).Elect(N2, 2).History();
        Holds(twin, "leader-completeness");
    }

    /// <summary>
    /// P4-01's prediction: two leaders of different terms each create `put x 1` at index 1, and two
    /// nodes apply one each. By bytes it is the same entry; by ghost id it is not.
    /// </summary>
    [Fact]
    public void TwoByteEqualEntriesAppliedAtOneIndexAreRejectedByGhostIdAndMissedByBytes()
    {
        Trace Build(bool second) => new Trace()
            .Elect(N1, 2).Create(N1, 2).Replicate(N1, N2, 2).Apply(N2, 1).Leave(N1, 2)
            .Elect(N3, 3).Create(N3, 3).Apply(second ? N3 : N2, 1);
        var bad = Build(second: true).History();
        Rejects(bad, "state-machine-safety", "n3 applied a different entry at 1 than n2 did");
        Assert.DoesNotContain(LogInvariants.Check(bad, "state-machine-safety", byBytes: true).Violations, v => v.Contains("different entry", StringComparison.Ordinal));

        var twin = Build(second: false).History();
        Assert.DoesNotContain(LogInvariants.Check(twin, "state-machine-safety").Violations, v => v.Contains("different entry", StringComparison.Ordinal));
    }

    [Fact]
    public void ACommittedEntryLostFromAQuorumIsRejectedAndALossElsewhereIsNot()
    {
        Trace Committed() => new Trace().Elect(N1, 1).Create(N1, 1).Durable(N1).Replicate(N1, N2, 1).Durable(N2);
        Rejects(Committed().Crash(N2, keep: 0).History(), "committed-durable", "entry 1, committed in fact, is durable on only 1 node(s) after n2's disk changed");
        Holds(Committed().Crash(N3, keep: 0).History(), "committed-durable");
    }

    [Fact]
    public void AClaimAheadOfCommitmentInFactIsRejected()
    {
        var early = new Trace().Elect(N1, 1).Create(N1, 1).Durable(N1).Claim(N1, 1, 1).History();
        Rejects(early, "no-spurious-commit", "n1 claimed commit index 1, but index 1 is not committed in fact");
        var twin = new Trace().Elect(N1, 1).Create(N1, 1).Durable(N1).Replicate(N1, N2, 1).Durable(N2).Claim(N1, 1, 1).History();
        Holds(twin, "no-spurious-commit");
    }

    /// <summary>
    /// Figure 8(c) on three nodes: n1's term-2 entry reaches a majority only through n1's term-4
    /// AppendEntries. Counting copies commits it; commitment in fact does not, until a term-4 entry
    /// above it is on a majority too.
    /// </summary>
    [Fact]
    public void AnOldTermsEntryOnAMajorityThroughALaterLeaderIsNotCommittedByCountingCopies()
    {
        Trace Figure8() => new Trace()
            .Elect(N1, 2).Create(N1, 2).Durable(N1).Leave(N1, 2)
            .Elect(N3, 3).Create(N3, 3).Durable(N3).Leave(N3, 3)
            .Elect(N1, 4).Replicate(N1, N2, 4).Durable(N2);
        Rejects(Figure8().Claim(N1, 4, 1).History(), "no-spurious-commit", "n1 claimed commit index 1, but index 1 is not committed in fact");
        Holds(Figure8().Create(N1, 4).Durable(N1).Replicate(N1, N2, 4, prev: 1).Durable(N2).Claim(N1, 4, 2).History(), "no-spurious-commit");
    }

    [Fact]
    public void TwoCreationsAtOneTermAndIndexAreRejectedAndAtTwoIndicesAreNot()
    {
        Rejects(new Trace().Elect(N1, 1).Create(N1, 1).Create(N1, 1, "put x 2", at: 1).History(), "entry-uniqueness", "(1, 1) was created 2 times");
        Rejects(new Trace().Create(N2, 1).History(), "entry-uniqueness", "n2 created an entry of term 1 at 1 without being that term's leader");
        Holds(new Trace().Elect(N1, 1).Create(N1, 1).Create(N1, 1, "put x 2").History(), "entry-uniqueness");
    }
}
