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
    private static readonly NodeId N1 = new(1), N2 = new(2), N3 = new(3), N4 = new(4), N5 = new(5);

    /// <summary>
    /// A hand-built execution of three nodes. It records raw events only, as the observation adapter
    /// will: writes, disks, messages, deliveries. It never assigns ghost ids; it tracks each node's
    /// log only to build the messages a node would send.
    /// </summary>
    private sealed class Trace
    {
        private readonly List<LogHistory.Event> _e = [];
        private readonly Dictionary<NodeId, List<LogEntryAt>> _logs = new() { [N1] = [], [N2] = [], [N3] = [], [N4] = [], [N5] = [] };
        private readonly Dictionary<NodeId, List<LogEntryAt>> _disks = new() { [N1] = [], [N2] = [], [N3] = [], [N4] = [], [N5] = [] };
        private readonly Dictionary<NodeId, LogHistory.SnapshotAt> _snapshots = [];
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
        public Trace Create(NodeId n, long term, string command = "put x 1", long? at = null) => Create(n, term, System.Text.Encoding.ASCII.GetBytes(command), at);

        /// <summary>The node appends a configuration entry (P6-04).</summary>
        public Trace Configure(NodeId n, long term, Configuration c) => Create(n, term, c.Encode(), null);

        private Trace Create(NodeId n, long term, byte[] command, long? at)
        {
            var log = _logs[n];
            var index = at ?? (log.Count + 1);
            var e = new LogEntryAt(index, new Term(term), command);
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

        public Trace Restore(NodeId n, long index)
        {
            _e.Add(new LogHistory.Restored(Seq, n, index));
            return this;
        }

        public Trace Apply(NodeId n, long index)
        {
            _e.Add(new LogHistory.Applied(Seq, n, index));
            return this;
        }

        /// <summary>
        /// P7-04: the node compacts its log up to <paramref name="upTo"/> and the new file becomes
        /// durable: a snapshot of its entry there, then every entry after it. The trace keeps the
        /// logical log for the messages it builds.
        /// </summary>
        public Trace Compact(NodeId n, long upTo)
        {
            var log = _logs[n].OrderBy(x => x.Index).ToList();
            var snap = new LogHistory.SnapshotAt(upTo, log.First(x => x.Index == upTo).Term);
            var retained = log.Where(x => x.Index > upTo).ToList();
            _snapshots[n] = snap;
            _e.Add(new LogHistory.Issued(Seq, n, ++_step, upTo + 1, retained, snap));
            _e.Add(new LogHistory.Durable(Seq, n, upTo + 1, retained, snap));
            _disks[n] = log;
            return this;
        }

        /// <summary>
        /// P7-04: <paramref name="to"/> installs <paramref name="from"/>'s latest snapshot, keeping the
        /// entries after it if its own entry at the snapshot's index has the snapshot's term (Figure 13),
        /// durably, and restores its state machine from it.
        /// </summary>
        public Trace Install(NodeId from, NodeId to) => InstallSnapshot(to, _snapshots[from], _logs[from]);

        /// <summary>P7-04: <paramref name="to"/> installs a snapshot of (<paramref name="term"/>, <paramref name="index"/>) whatever any log held.</summary>
        public Trace InstallForged(NodeId to, long index, long term) => InstallSnapshot(to, new LogHistory.SnapshotAt(index, new Term(term)), []);

        private Trace InstallSnapshot(NodeId to, LogHistory.SnapshotAt snap, List<LogEntryAt> source)
        {
            var own = _logs[to].OrderBy(x => x.Index).ToList();
            var keep = own.Any(x => x.Index == snap.Index && x.Term == snap.Term) ? own.Where(x => x.Index > snap.Index).ToList() : [];
            _logs[to] = [.. source.Where(x => x.Index <= snap.Index), .. keep];
            _snapshots[to] = snap;
            var step = ++_step;
            _e.Add(new LogHistory.Issued(Seq, to, step, snap.Index + 1, keep, snap));
            _e.Add(new LogHistory.Durable(Seq, to, snap.Index + 1, keep, snap));
            _disks[to] = _logs[to].OrderBy(x => x.Index).ToList();
            _e.Add(new LogHistory.Restored(Seq, to, snap.Index));
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

    private static readonly Configuration Joint = new([N1, N2, N3], [N1, N4, N5]);
    private static readonly Configuration NewOnly = new([N1, N4, N5]);

    private static long Committed(LogHistory h) => LogInvariants.Check(h, "no-spurious-commit").Count("entries-committed");

    /// <summary>
    /// P6-04 trace (b): during joint consensus an entry on a majority of the new configuration but
    /// not the old is not committed in fact, and stays uncommitted when `C_new` sits above it on the
    /// disk being examined: the configuration that counts is the one in effect at the entry's index,
    /// not the latest on a disk (the prediction). The twin, one more copy in the old configuration, commits both.
    /// Sabotage S-joint-1 (the new configuration only).
    /// </summary>
    [Fact]
    public void DuringJointConsensusACommitNeedsAMajorityOfBothConfigurations()
    {
        Trace Joined() => new Trace().Elect(N1, 1).Configure(N1, 1, Joint).Create(N1, 1).Durable(N1)
            .Replicate(N1, N4, 1).Durable(N4).Replicate(N1, N5, 1).Durable(N5);

        var newOnly = Joined().Configure(N1, 1, NewOnly).Durable(N1).History();
        Assert.Equal(0, Committed(Joined().History()));
        Assert.Equal(0, Committed(newOnly));
        Holds(newOnly);
        Assert.Equal(2, Committed(Joined().Replicate(N1, N2, 1).Durable(N2).History()));

        // The case that separates the configuration at an index from the latest on a disk: n4 receives
        // `[C_old,new, put, C_new]` in one write, before `C_new` is durable anywhere else. The put is on
        // n1, n4 and n5, a majority of `C_new` (the latest configuration on n4's disk) and not of
        // `C_old,new`, the configuration in effect at its index.
        var aboveIt = new Trace().Elect(N1, 1).Configure(N1, 1, Joint).Create(N1, 1).Durable(N1).Replicate(N1, N5, 1).Durable(N5)
            .Configure(N1, 1, NewOnly).Replicate(N1, N4, 1).Durable(N4).History();
        Assert.Equal(0, Committed(aboveIt));
    }

    /// <summary>P6-04 trace (c): once `C_new` is in effect, a majority of it suffices; the twin still in joint consensus does not commit.</summary>
    [Fact]
    public void AfterTheNewConfigurationAMajorityOfItSuffices()
    {
        Trace Joined() => new Trace().Elect(N1, 1).Configure(N1, 1, Joint).Durable(N1)
            .Replicate(N1, N2, 1).Durable(N2).Replicate(N1, N4, 1).Durable(N4);

        var after = Joined().Configure(N1, 1, NewOnly).Create(N1, 1).Durable(N1).Replicate(N1, N4, 1, prev: 1).Durable(N4).History();
        Assert.Equal(1, Committed(Joined().History()));
        Assert.Equal(3, Committed(after));
        Holds(after);
        Assert.Equal(1, Committed(Joined().Create(N1, 1).Durable(N1).Replicate(N1, N4, 1, prev: 1).Durable(N4).History()));
    }

    /// <summary>
    /// Invariant 6 under a new configuration: an entry committed under `C_new`, left on one member
    /// and a removed server, fails, though two copies are a majority of the cluster's size (its twin
    /// below, durable on a majority of `C_new` that no old member holds, holds). Sabotage S-joint-3
    /// (invariant 6 counts holders against the cluster's size).
    /// </summary>
    [Fact]
    public void ACommittedEntryLeftOnlyOnOneMemberAndARemovedServerIsRejected()
    {
        var h = new Trace().Elect(N1, 1).Configure(N1, 1, NewOnly).Create(N1, 1).Durable(N1)
            .Replicate(N1, N4, 1).Durable(N4).Replicate(N1, N2, 1).Durable(N2).Crash(N4, keep: 0).History();

        Assert.Equal(2, Committed(h));
        Rejects(h, "committed-durable", "entry 1, committed in fact, is durable on only 2 node(s)");
    }

    [Fact]
    public void ACommittedEntryDurableOnAMajorityOfTheNewConfigurationHolds()
    {
        var h = new Trace().Elect(N1, 1).Configure(N1, 1, NewOnly).Create(N1, 1).Durable(N1)
            .Replicate(N1, N4, 1).Durable(N4).Replicate(N1, N5, 1).Durable(N5).History();

        Assert.Equal(2, Committed(h));
        Holds(h, "committed-durable");
    }

    /// <summary>
    /// P6-08, found by the membership sample (seed 152): invariant 6 asks a quorum of the
    /// configuration in effect at the latest committed index, the one any future leader is elected
    /// by. An entry committed under `C_old,new`, after `C_new` has committed, need not stay on the old
    /// servers `C_new` removed. Sabotage S-joint-4 (the entry's own configuration, as P6-04 had it).
    /// </summary>
    [Fact]
    public void ACommittedEntryNeedNotStayOnServersALaterConfigurationRemoved()
    {
        var h = new Trace().Elect(N1, 1).Configure(N1, 1, Joint).Create(N1, 1).Durable(N1)
            .Replicate(N1, N2, 1).Durable(N2).Replicate(N1, N4, 1).Durable(N4)
            .Configure(N1, 1, NewOnly).Durable(N1).Replicate(N1, N4, 1, prev: 2).Durable(N4)
            .Crash(N2, keep: 0).History();

        Assert.Equal(3, Committed(h));
        Holds(h);
    }

    /// <summary>
    /// P7-04: a correct compaction and install accepted. n2 and then n1 compact committed, applied
    /// entries; n3 installs n2's snapshot and keeps its own entries after it. Every invariant holds over
    /// the logical log, and the commitment in fact is unchanged. Sabotage S-compact-1.
    /// </summary>
    [Fact]
    public void ACorrectCompactionAndInstallHoldEveryInvariant()
    {
        var h = Healthy().Compact(N2, 3).Compact(N1, 2).Install(N2, N3).History();

        Holds(h);
        Assert.Equal(3, new LogAnalysis(h).Counts["entries-committed"]);
    }

    /// <summary>P7-04: committed entries held only in snapshots, on every node, are still committed and durable.</summary>
    [Fact]
    public void CommittedEntriesHeldOnlyInSnapshotsStayDurable()
    {
        var h = Healthy().Compact(N1, 3).Compact(N2, 3).Compact(N3, 3).History();

        Holds(h);
        Assert.Equal(3, new LogAnalysis(h).Counts["entries-committed"]);
    }

    /// <summary>
    /// P7-04: a leader compacts past its commit index (its third entry is on its disk alone), crashes,
    /// and a new leader commits a different third entry; the old leader then restores its snapshot,
    /// which counts as applying the overwritten entry. State Machine Safety rejects it.
    /// </summary>
    [Fact]
    public void ACompactionPastTheCommitIndexIsRejectedWhenTheSnapshotIsRestored()
    {
        var h = new Trace()
            .Elect(N1, 1).Create(N1, 1).Create(N1, 1, "append x a").Durable(N1)
            .Replicate(N1, N2, 1).Durable(N2).Claim(N1, 1, 2).Apply(N1, 1).Apply(N1, 2)
            .Create(N1, 1, "put z 9").Durable(N1).Compact(N1, 3)
            .Crash(N1).Leave(N1, 1).Elect(N2, 2).Create(N2, 2, "put y 2").Durable(N2)
            .Replicate(N2, N3, 2).Durable(N3).Claim(N2, 2, 3).Apply(N2, 1).Apply(N2, 2).Apply(N2, 3)
            .Restore(N1, 3)
            .History();

        Rejects(h, "state-machine-safety", "n1 applied a different entry at 3");
    }

    /// <summary>
    /// P7-04: a node applies an entry at an index, then installs a snapshot covering another entry
    /// there: the install counts as applying it, and State Machine Safety rejects it. Sabotage
    /// S-compact-2 (an install not counted as applying).
    /// </summary>
    [Fact]
    public void AnInstallCoveringAnotherEntryAtAnAppliedIndexIsRejected()
    {
        var h = new Trace()
            .Elect(N1, 1).Create(N1, 1).Create(N1, 1, "append x a").Durable(N1)
            .Replicate(N1, N3, 1).Durable(N3).Apply(N3, 2)
            .Crash(N1, keep: 1).Leave(N1, 1).Elect(N2, 2).Create(N2, 2, "put x 1", at: 1).Create(N2, 2, "put y 2").Durable(N2)
            .Compact(N2, 2).Install(N2, N3)
            .History();

        Rejects(h, "state-machine-safety", "n3 applied a different entry at 2");
    }

    /// <summary>P7-04: a snapshot no log held (a term at that index no leader created) is rejected, by its index and term, not its index alone. Sabotage S-compact-1.</summary>
    [Fact]
    public void AnInstalledSnapshotNoLogHeldIsRejected()
    {
        var h = Healthy().Compact(N2, 2).InstallForged(N3, 2, 7).History();

        Rejects(h, "log-matching", "n3 holds a snapshot of (7, 2) that no log held when it was made");
    }

    /// <summary>
    /// P7-04: the observation adapter reads a compaction as the node's disk shows it: a file written under
    /// another name and renamed onto the entry log is read whole, its snapshot reported with it, for the
    /// intended log when the rename is issued and the durable one when it completes. Until P7-04 the
    /// adapter ignored any operation on the entry log but an append or a truncate, and a rename is
    /// observed under its source's name (P7-02, property 10).
    /// </summary>
    [Fact]
    public void ARenameOntoTheLogIsReadWholeWithItsSnapshot()
    {
        var e = new[] { EntryLog.Record(1, new Term(1), Term.Zero, [1]), EntryLog.Record(2, new Term(1), new Term(1), [2]), EntryLog.Record(3, new Term(1), new Term(1), [3]) };
        var compacted = EntryLog.SnapshotRecord(2, new Term(1), null, [9]).Concat(e[2]).ToArray();
        var write = new PersistAppend(EntryLog.FileName, e.SelectMany(x => x).ToArray());
        var temp = new PersistAppend("entries.2", compacted);
        var rename = new PersistRename("entries.2", EntryLog.FileName);
        var observations = new List<Raft.Simulation.Observation>
        {
            new Raft.Simulation.IssuedObservation(1, N1, write, 1), new Raft.Simulation.DurableObservation(2, N1, EntryLog.FileName, null, write),
            new Raft.Simulation.IssuedObservation(3, N1, temp, 2), new Raft.Simulation.IssuedObservation(3, N1, rename, 2),
            new Raft.Simulation.DurableObservation(4, N1, temp.File, null, temp), new Raft.Simulation.DurableObservation(5, N1, rename.File, null, rename),
        };

        var h = LogHistory.FromObservations(observations, new ElectionHistory(observations, 3), 3);

        var issued = h.Events.OfType<LogHistory.Issued>().Last();
        var durable = h.Events.OfType<LogHistory.Durable>().Last();
        Assert.Equal(new LogHistory.SnapshotAt(2, new Term(1)), issued.Snapshot);
        Assert.Equal(new LogHistory.SnapshotAt(2, new Term(1)), durable.Snapshot);
        Assert.Equal([3L], durable.Append.Select(x => x.Index));
    }

    /// <summary>
    /// The membership soak's seed 434 (P7-11): a crash keeps an install's rename, so the file the
    /// chunks were written to is gone when the node restarts, and the node's next compaction may take
    /// the same name. The checker must read that compaction's file as the node wrote it, not after the
    /// bytes of the file the crash removed. Sabotage S-compact-6.
    /// </summary>
    [Fact]
    public void AFileACrashRemovedIsGoneWhenTheNodeReusesItsName()
    {
        var e = new[] { EntryLog.Record(1, new Term(1), Term.Zero, [1]), EntryLog.Record(2, new Term(1), new Term(1), [2]), EntryLog.Record(3, new Term(1), new Term(1), [3]) };
        var write = new PersistAppend(EntryLog.FileName, e.SelectMany(x => x).ToArray());
        var installed = EntryLog.SnapshotRecord(2, new Term(1), null, [8]);
        var chunk = new PersistWriteAt("entries.0", 0, installed);
        var rename = new PersistRename("entries.0", EntryLog.FileName);
        var compacted = new PersistAppend("entries.0", EntryLog.SnapshotRecord(3, new Term(1), null, [9]));
        var observations = new List<Raft.Simulation.Observation>
        {
            new Raft.Simulation.StartObservation(0, N1, 1),
            new Raft.Simulation.IssuedObservation(1, N1, write, 1), new Raft.Simulation.DurableObservation(2, N1, EntryLog.FileName, null, write),
            new Raft.Simulation.IssuedObservation(3, N1, chunk, 2), new Raft.Simulation.IssuedObservation(3, N1, rename, 2),
            new Raft.Simulation.DurableObservation(4, N1, chunk.File, null, chunk),
            new Raft.Simulation.CrashObservation(5, N1),
            new Raft.Simulation.DurableObservation(5, N1, EntryLog.FileName, installed),
            new Raft.Simulation.DurableObservation(5, N1, "entries.0", null),
            new Raft.Simulation.StartObservation(6, N1, 2),
            new Raft.Simulation.IssuedObservation(7, N1, compacted, 3), new Raft.Simulation.IssuedObservation(7, N1, rename, 3),
        };

        var h = LogHistory.FromObservations(observations, new ElectionHistory(observations, 3), 3);

        Assert.Equal(new LogHistory.SnapshotAt(3, new Term(1)), h.Events.OfType<LogHistory.Issued>().Last().Snapshot);
    }
}

