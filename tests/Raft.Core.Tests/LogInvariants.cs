using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Raft.Core;
using Raft.Simulation;

namespace Raft.Core.Tests;

/// <summary>An entry as a log holds it: its index, its term and its command's bytes.</summary>
internal sealed record LogEntryAt(long Index, Term Term, byte[] Command);

/// <summary>
/// An execution's log events, in observation order (P4-01): writes each node issued (with the
/// step that issued them), changes to each node's durable log, the `AppendEntries` sent and
/// delivered (a delivery starts a step at its receiver), crashes, elections and the end of each
/// leader's term, and applies. Built from observations (P4-03) or by hand in tests; ghost entry ids
/// are never part of it. They are assigned by <see cref="LogAnalysis"/> from creation and provenance
/// (P4 decision 1), because an id carried in the entry would be the node's own claim about identity.
/// A log change, issued or durable, is a truncation point (drop every index at or above it; 0 for
/// none) and the entries appended after it, so an event costs its size, not the log's.
/// </summary>
internal sealed class LogHistory
{
    public abstract record Event(long Seq, NodeId Node);

    /// <summary>
    /// A snapshot at the head of a log file (P7-04): every entry up to <see cref="Index"/>, the last of
    /// term <see cref="Term"/>. Which entries those are is never read from the node: the analysis
    /// takes them from the log that held them when the snapshot was made (phase 7 decision 5).
    /// </summary>
    public sealed record SnapshotAt(long Index, Term Term);

    /// <summary>
    /// A log write the node issued in <paramref name="Step"/>. With <paramref name="Snapshot"/>, the
    /// file was replaced by one beginning with that snapshot: the log's prefix up to its index is the
    /// snapshot's, and the truncation and appends follow it.
    /// </summary>
    public sealed record Issued(long Seq, NodeId Node, long Step, long TruncateFrom, IReadOnlyList<LogEntryAt> Append, SnapshotAt? Snapshot = null) : Event(Seq, Node);

    /// <summary>The node's durable log changed: a write completed, or a crash changed the disk; <paramref name="Snapshot"/> as for <see cref="Issued"/>.</summary>
    public sealed record Durable(long Seq, NodeId Node, long TruncateFrom, IReadOnlyList<LogEntryAt> Append, SnapshotAt? Snapshot = null) : Event(Seq, Node);

    /// <summary>The node restored its state machine from a snapshot up to <paramref name="Index"/> (a self-report, as an apply): it counts as applying every entry the snapshot covers (spec §5, invariant 5).</summary>
    public sealed record Restored(long Seq, NodeId Node, long Index) : Event(Seq, Node);

    /// <summary>
    /// An `AppendEntries` the node sent; its `LeaderCommit` is the sender's claim of commitment. `Step`
    /// is the step that composed it: the barrier may release it after later steps changed the log.
    /// </summary>
    public sealed record Sent(long Seq, NodeId Node, long Id, AppendEntries Message, long Step = 0) : Event(Seq, Node);

    /// <summary>A message delivered to the node, starting <paramref name="Step"/> there.</summary>
    public sealed record Delivered(long Seq, NodeId Node, long Id, long Step) : Event(Seq, Node);

    public sealed record Crashed(long Seq, NodeId Node) : Event(Seq, Node);

    public sealed record Elected(long Seq, NodeId Node, Term Term) : Event(Seq, Node);

    /// <summary>The node moved to a term above <paramref name="Term"/>: a leader of that term has left office.</summary>
    public sealed record LeftTerm(long Seq, NodeId Node, Term Term) : Event(Seq, Node);

    /// <summary>The node applied the entry at an index (a self-report, P4 decision 3).</summary>
    public sealed record Applied(long Seq, NodeId Node, long Index) : Event(Seq, Node);

    public LogHistory(int clusterSize, IEnumerable<Event> events)
    {
        ClusterSize = clusterSize;
        Events = events.OrderBy(e => e.Seq).ToList();
    }

    public int ClusterSize { get; }

    /// <summary>The configuration before any configuration entry: servers 1 to <see cref="ClusterSize"/>.</summary>
    public Configuration Initial => new(Enumerable.Range(1, ClusterSize).Select(i => new NodeId(i)));

    public IReadOnlyList<Event> Events { get; }

    /// <summary>The change from <paramref name="before"/> to <paramref name="after"/>, as a truncation point and the entries after it.</summary>
    public static (long TruncateFrom, List<LogEntryAt> Append) Change(IReadOnlyList<LogEntryAt> before, IReadOnlyList<LogEntryAt> after)
    {
        var same = 0;
        while (same < before.Count && same < after.Count && before[same].Term == after[same].Term && before[same].Command.AsSpan().SequenceEqual(after[same].Command))
        {
            same++;
        }

        return (same < before.Count ? same + 1 : 0, after.Skip(same).ToList());
    }

    /// <summary>
    /// The log events of an observed execution (P4-03). Each node's intended log file is rebuilt from
    /// the writes it issued and read with the recovery the node itself uses, resumed where the last
    /// read stopped when the file only grew, read again after a truncation. Durable files are read the
    /// same way. After a crash the intended file is the durable one. Elections come from
    /// <paramref name="elections"/> (quorums of observed grants), a leader leaves office when it
    /// issues a higher term, and applies are the node's own "apply" events (P4 decision 3).
    /// </summary>
    public static LogHistory FromObservations(IReadOnlyList<Observation> observations, ElectionHistory elections, int clusterSize)
    {
        var events = new List<Event>();
        var bySeq = new Dictionary<(Term Term, NodeId Candidate), long>();
        elections.Elections(bySeq);
        events.AddRange(bySeq.Select(e => new Elected(e.Value, e.Key.Candidate, e.Key.Term)));
        var intended = new Dictionary<NodeId, FileView>();
        var durable = new Dictionary<NodeId, FileView>();
        var intendedFiles = new Dictionary<NodeId, FileSet>();
        var durableFiles = new Dictionary<NodeId, FileSet>();
        var restarting = new HashSet<NodeId>();
        FileView View(Dictionary<NodeId, FileView> d, NodeId n) => d.TryGetValue(n, out var v) ? v : d[n] = new FileView();
        FileSet Files(Dictionary<NodeId, FileSet> d, NodeId n) => d.TryGetValue(n, out var v) ? v : d[n] = new FileSet();
        long seq = 0;
        foreach (var o in observations)
        {
            seq++;
            switch (o)
            {
                case SentObservation s when MessageCodec.Decode(s.Payload.ToArray()) is AppendEntries ae:
                    events.Add(new Sent(seq, s.Node, s.Id, ae, s.Step));
                    break;
                case DeliveredObservation d:
                    events.Add(new Delivered(seq, d.Node, d.Id, d.Step));
                    break;
                case IssuedObservation { Op: { File: EntryLog.FileName } op } i:
                    restarting.Remove(i.Node);
                    var (cut, added, snap) = View(intended, i.Node).Apply(op);
                    events.Add(new Issued(seq, i.Node, i.Step, cut, added, snap));
                    break;
                case IssuedObservation { Op: PersistRename { To: EntryLog.FileName } rn } i:
                    // Compaction or an installed snapshot (P7-04): the file is replaced whole, by what
                    // the node wrote under the source's name.
                    restarting.Remove(i.Node);
                    var (rcut, radded, rsnap) = View(intended, i.Node).Replace(Files(intendedFiles, i.Node).Take(rn.File));
                    events.Add(new Issued(seq, i.Node, i.Step, rcut, radded, rsnap));
                    break;
                case IssuedObservation { Op: var other } i when other.File != TermVoteLog.FileName:
                    Files(intendedFiles, i.Node).Apply(other);
                    break;
                case IssuedObservation { Op: PersistAppend { File: TermVoteLog.FileName } tv } i:
                    var term = TermVoteLog.Recover(tv.Data.ToArray()).State.Term;
                    events.Add(new LeftTerm(seq, i.Node, new Term(term.Value - 1)));
                    break;
                case DurableObservation { Completed: PersistRename { To: EntryLog.FileName } moved } dm:
                    var (mcut, madded, msnap) = View(durable, dm.Node).Replace(Files(durableFiles, dm.Node).Take(moved.File));
                    events.Add(new Durable(seq, dm.Node, mcut, madded, msnap));
                    if (restarting.Contains(dm.Node))
                    {
                        intended[dm.Node] = View(durable, dm.Node).Copy();
                    }

                    break;
                case DurableObservation { File: EntryLog.FileName } du:
                    var (dcut, dadded, dsnap) = du.Completed is { } done ? View(durable, du.Node).Apply(done) : View(durable, du.Node).Replace(du.Content?.ToArray() ?? []);
                    events.Add(new Durable(seq, du.Node, dcut, dadded, dsnap));
                    if (restarting.Contains(du.Node))
                    {
                        intended[du.Node] = View(durable, du.Node).Copy();
                    }

                    break;
                case DurableObservation du when du.File != TermVoteLog.FileName:
                    if (du.Completed is { } completed)
                    {
                        Files(durableFiles, du.Node).Apply(completed);
                    }
                    else
                    {
                        Files(durableFiles, du.Node).Set(du.File, du.Content?.ToArray());

                        // What a crash left, reported after the crash itself (P7-11, the membership soak,
                        // seed 434): the node restarts with these files. Copied into the intended files
                        // only at the crash, a file the crash removed stayed there, and a later file of the
                        // same name was read as the stale bytes with the new ones after them.
                        if (restarting.Contains(du.Node))
                        {
                            Files(intendedFiles, du.Node).Set(du.File, du.Content?.ToArray());
                        }
                    }

                    break;
                case CrashObservation c:
                    events.Add(new Crashed(seq, c.Node));
                    intended[c.Node] = View(durable, c.Node).Copy();
                    intendedFiles[c.Node] = Files(durableFiles, c.Node).Copy();
                    restarting.Add(c.Node);
                    break;
                case EmittedObservation { Event.Name: "apply" } em:
                    events.Add(new Applied(seq, em.Node, long.Parse(em.Event.Fields.First(f => f.Key == "index").Value, CultureInfo.InvariantCulture)));
                    break;
                case EmittedObservation { Event.Name: "restore" } rs:
                    events.Add(new Restored(seq, rs.Node, long.Parse(rs.Event.Fields.First(f => f.Key == "index").Value, CultureInfo.InvariantCulture)));
                    break;
            }
        }

        return new LogHistory(clusterSize, events);
    }

    /// <summary>
    /// A node's files other than its two logs, as a sequence of writes leaves them (P7-04): what a
    /// rename onto the entry log puts there is what the node wrote under the source's name.
    /// </summary>
    internal sealed class FileSet
    {
        private Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public FileSet Copy() => new() { _files = _files.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone(), StringComparer.Ordinal) };

        public void Set(string file, byte[]? content)
        {
            if (content is null)
            {
                _files.Remove(file);
            }
            else
            {
                _files[file] = content;
            }
        }

        /// <summary>The file's bytes, removed from the set (a rename moves them); empty when it was never written, as the disk models a rename whose source data was lost (P7-00).</summary>
        public byte[] Take(string file) => _files.Remove(file, out var b) ? b : [];

        public void Apply(Persist op)
        {
            switch (op)
            {
                case PersistAppend a:
                    _files[a.File] = [.. _files.GetValueOrDefault(a.File, []), .. a.Data.Span];
                    break;
                case PersistWriteAt w:
                    var old = _files.GetValueOrDefault(w.File, []);
                    var grown = new byte[Math.Max(old.Length, w.Offset + w.Data.Length)];
                    old.CopyTo(grown, 0);
                    w.Data.Span.CopyTo(grown.AsSpan((int)w.Offset));
                    _files[w.File] = grown;
                    break;
                case PersistTruncate t when _files.TryGetValue(t.File, out var cur) && cur.Length > t.Length:
                    _files[t.File] = cur[..(int)t.Length];
                    break;
                case PersistRename r:
                    _files[r.To] = Take(r.File);
                    break;
                case PersistDelete d:
                    _files.Remove(d.File);
                    break;
            }
        }
    }

    /// <summary>
    /// A log file as a node's recovery reads it, kept up to date write by write. From P7-04 it may
    /// begin with a snapshot: its entries then follow the snapshot's index, and a change that brings a
    /// different snapshot is reported with it, the appends being every entry after it.
    /// </summary>
    internal sealed class FileView
    {
        private byte[] _bytes = new byte[256];
        private int _length;
        private List<StoredEntry> _entries = [];
        private LogSnapshot? _snapshot;
        private long _valid;

        /// <summary>Entries in the file, after the snapshot's index if there is one.</summary>
        public int Count => _entries.Count;

        public LogSnapshot? Snapshot => _snapshot;

        /// <summary>The configuration this log puts in effect (P6 decisions 1, 2): its latest configuration entry, else the snapshot's, else <paramref name="initial"/>.</summary>
        public Configuration InEffect(Configuration initial) => Configuration.InEffect(_snapshot?.Configuration ?? initial, _entries.Select(e => e.Command));

        public FileView Copy() => new() { _bytes = (byte[])_bytes.Clone(), _length = _length, _entries = [.. _entries], _snapshot = _snapshot, _valid = _valid };

        public (long TruncateFrom, List<LogEntryAt> Append, SnapshotAt? Snapshot) Apply(Persist op)
        {
            switch (op)
            {
                case PersistAppend a:
                    Grow(_length + a.Data.Length);
                    a.Data.Span.CopyTo(_bytes.AsSpan(_length));
                    _length += a.Data.Length;
                    return Resume();
                case PersistTruncate t when t.Length < _length:
                    _length = (int)t.Length;
                    return Reread();
                default:
                    return (0, [], null);
            }
        }

        public (long TruncateFrom, List<LogEntryAt> Append, SnapshotAt? Snapshot) Replace(byte[] content)
        {
            if (content.Length >= _length && content.AsSpan(0, _length).SequenceEqual(_bytes.AsSpan(0, _length)))
            {
                Grow(content.Length);
                content.AsSpan(_length).CopyTo(_bytes.AsSpan(_length));
                _length = content.Length;
                return Resume();
            }

            Grow(content.Length);
            content.CopyTo(_bytes, 0);
            _length = content.Length;
            return Reread();
        }

        private long Base => _snapshot?.Index ?? 0;

        private (long, List<LogEntryAt>, SnapshotAt?) Resume()
        {
            var before = _entries.Count;
            var had = _snapshot;
            var r = EntryLog.Resume(_entries, _bytes, _length, _valid, _snapshot);
            _valid = r.ValidLength;
            _snapshot = r.Snapshot;
            if (!Same(had, _snapshot))
            {
                return (Base + 1, _entries.Select(At).ToList(), At(_snapshot));
            }

            if (r.FirstChanged == 0)
            {
                return (0, [], null);
            }

            return (r.FirstChanged <= Base + before ? r.FirstChanged : 0, _entries.Skip((int)(r.FirstChanged - Base) - 1).Select(At).ToList(), null);
        }

        private (long, List<LogEntryAt>, SnapshotAt?) Reread()
        {
            var had = _snapshot;
            var old = _entries.Select(At).ToList();
            _entries = [];
            var r = EntryLog.Resume(_entries, _bytes, _length, 0);
            _valid = r.ValidLength;
            _snapshot = r.Snapshot;
            if (!Same(had, _snapshot))
            {
                // A different snapshot (or none, where there was one): the log is described afresh.
                return (Base + 1, _entries.Select(At).ToList(), At(_snapshot) ?? (had is null ? null : new SnapshotAt(0, Term.Zero)));
            }

            var (cut, added) = Change(old, _entries.Select(At).ToList());
            return (cut == 0 ? 0 : cut + Base, added, null);
        }

        private static bool Same(LogSnapshot? a, LogSnapshot? b) => a?.Index == b?.Index && a?.Term == b?.Term;

        private static SnapshotAt? At(LogSnapshot? s) => s is null ? null : new SnapshotAt(s.Index, s.Term);

        private static LogEntryAt At(StoredEntry e) => new(e.Index, e.Term, e.Command);

        private void Grow(int size)
        {
            if (size > _bytes.Length)
            {
                Array.Resize(ref _bytes, Math.Max(size, _bytes.Length * 2));
            }
        }
    }
}

/// <summary>
/// One pass over a <see cref="LogHistory"/>: ghost ids, each node's intended and durable logs,
/// commitment in fact, and every log invariant's violations (spec §5: 2, 3, 4, 5, 6, 7, 10). Every
/// log is dense from index 1, held by position; commitment in fact is a prefix, and each check
/// looks only at the indices an event changed.
/// </summary>
internal sealed class LogAnalysis
{
    /// <summary>An entry as a node holds it: the ghost id, and the term of the leader whose write put this copy there.</summary>
    /// <summary>
    /// <see cref="Config"/> is the configuration in effect at this index in the log holding it: the
    /// latest configuration entry at or below it (P6-04). By Log Matching every log holding this
    /// entry has the same prefix, so it is the configuration of the leader that created the entry
    /// at the time it did, whatever a log holds above it.
    /// </summary>
    private sealed record Held(long Index, Term Term, string Ghost, Term CopyTerm, Configuration Config);

    public static readonly string[] Names = ["leader-append-only", "log-matching", "leader-completeness", "state-machine-safety", "committed-durable", "no-spurious-commit", "entry-uniqueness"];

    private readonly LogHistory _h;
    private readonly bool _byBytes;
    private readonly Dictionary<NodeId, List<Held>> _intended = [];
    private readonly Dictionary<NodeId, List<LogHistory.Restored>> _pendingRestores = [];
    private readonly Dictionary<NodeId, List<Held>> _durable = [];
    private readonly Dictionary<long, Dictionary<long, string>> _messageGhosts = [];
    private readonly Dictionary<long, Term> _messageTerms = [];
    private readonly Dictionary<(NodeId, long), (long Id, Term Term)> _stepSource = [];
    private readonly Dictionary<(Term, long), int> _creations = [];
    private readonly Dictionary<string, byte[]> _commands = [];
    private readonly Dictionary<(NodeId, long, Term), (string Ghost, Term CopyTerm)> _lastIssued = [];
    private readonly List<(string Ghost, Term Term, Configuration Config)> _committed = [];
    private readonly List<CommitInFact> _commits = [];
    private readonly Dictionary<NodeId, List<(long Step, Held Entry)>> _removed = [];
    private readonly Dictionary<string, long> _createdAt = new(StringComparer.Ordinal);
    private readonly Dictionary<long, (string Key, NodeId Node)> _applied = [];
    private readonly Dictionary<NodeId, Term> _tenure = [];
    private readonly Dictionary<NodeId, long> _claimVerified = [];
    private readonly HashSet<NodeId> _restarting = [];
    private readonly HashSet<long> _belowQuorum = [];
    private readonly Dictionary<(long Index, Term Term), List<Held>> _coverage = [];
    private readonly Dictionary<string, List<string>> _violations = [];
    private long _ghosts;

    public LogAnalysis(LogHistory h, bool byBytes = false)
    {
        _h = h;
        _byBytes = byBytes;
        foreach (var name in Names)
        {
            _violations[name] = [];
        }

        foreach (var e in h.Events)
        {
            switch (e)
            {
                case LogHistory.Delivered d:
                    if (_messageTerms.TryGetValue(d.Id, out var mt))
                    {
                        _stepSource[(d.Node, d.Step)] = (d.Id, mt);
                    }

                    break;
                case LogHistory.Sent s:
                    OnSent(s);
                    break;
                case LogHistory.Issued i:
                    OnIssued(i);
                    Restore(i.Node);
                    break;
                case LogHistory.Durable du:
                    OnDurable(du);
                    break;
                case LogHistory.Crashed c:
                    _removed.Remove(c.Node);
                    if (_tenure.ContainsKey(c.Node) && Intended(c.Node).Count > _committed.Count)
                    {
                        Counts["leaders-crashed-with-uncommitted-entries"]++;
                    }

                    _tenure.Remove(c.Node);
                    _pendingRestores.Remove(c.Node);
                    Intended(c.Node).Clear();
                    Intended(c.Node).AddRange(Durable(c.Node));
                    Lower(c.Node, Intended(c.Node).Count);
                    _restarting.Add(c.Node);
                    break;
                case LogHistory.Elected el:
                    _tenure[el.Node] = el.Term;
                    Counts["elections"]++;
                    if (Intended(el.Node).Count > 0)
                    {
                        Counts["leaders-with-entries"]++;
                    }

                    for (var i = 1; i <= _committed.Count; i++)
                    {
                        if (_committed[i - 1].Term < el.Term)
                        {
                            RequireHeld(el.Node, el.Term, i, _committed[i - 1].Ghost, "elected");
                        }
                    }

                    break;
                case LogHistory.LeftTerm lt when _tenure.TryGetValue(lt.Node, out var t) && t <= lt.Term:
                    _tenure.Remove(lt.Node);
                    break;
                case LogHistory.Applied a:
                    OnApplied(a);
                    break;
                case LogHistory.Restored rs:
                    (_pendingRestores.TryGetValue(rs.Node, out var pending) ? pending : _pendingRestores[rs.Node] = []).Add(rs);
                    Restore(rs.Node);
                    break;
            }
        }

        Counts["entries-committed"] = _committed.Count;
        Counts["terms-with-a-commit"] = _committed.Select(c => c.Term).Distinct().Count();
        Counts["entries-created"] = _creations.Values.Sum();
    }

    public Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal)
    {
        ["elections"] = 0, ["leaders-with-entries"] = 0, ["suffixes-truncated"] = 0, ["applies"] = 0, ["claims"] = 0,
        ["leaders-crashed-with-uncommitted-entries"] = 0, ["committed-in-a-later-term"] = 0,
    };

    /// <summary>
    /// An entry committed in fact: its ghost id and index, the event (observation sequence number) at
    /// which a quorum's durable copies committed it, the event that created it, and whether it carries
    /// a client's command (a leader's no-op is empty).
    /// </summary>
    public sealed record CommitInFact(string Ghost, long Index, long Seq, long CreatedSeq, bool Command);

    /// <summary>Every entry committed in fact, in index order (P4-06: invariant 11's commit clause reads it).</summary>
    public IReadOnlyList<CommitInFact> Commits => _commits;

    /// <summary>The command a created entry carries, by ghost id, or null for an entry never created.</summary>
    public byte[]? CommandOf(string ghost) => _commands.GetValueOrDefault(ghost);

    /// <summary>The ghost id of the entry a node holds at an index in its intended log, or null.</summary>
    public string? GhostAt(NodeId node, long index) => At(Intended(node), index)?.Ghost;

    public InvariantResult Result(string name) => new(name, _violations[name], Counts);

    private static Held? At(List<Held> log, long index) => index >= 1 && index <= log.Count ? log[(int)index - 1] : null;

    private List<Held> Intended(NodeId n) => _intended.TryGetValue(n, out var l) ? l : _intended[n] = [];

    /// <summary>
    /// A restore counts as applying every index its snapshot covers once the node's log holds them
    /// (P7-06): a follower installing a snapshot restores its state machine at once, while the world
    /// holds the rename that puts the snapshot in its log until the snapshot's file is durable, so the
    /// event arrives before the log holds what it covers. Lost with the node in a crash.
    /// </summary>
    private void Restore(NodeId node)
    {
        if (!_pendingRestores.TryGetValue(node, out var pending))
        {
            return;
        }

        while (pending.Count > 0 && pending[0].Index <= Intended(node).Count)
        {
            var rs = pending[0];
            pending.RemoveAt(0);
            for (var p = 1L; p <= rs.Index; p++)
            {
                OnApplied(new LogHistory.Applied(rs.Seq, rs.Node, p));
            }
        }
    }

    private List<Held> Durable(NodeId n) => _durable.TryGetValue(n, out var l) ? l : _durable[n] = [];

    private void Fail(string invariant, string message) => _violations[invariant].Add(message);

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Claims above this index are verified again: the node's log changed at or below it.</summary>
    private void Lower(NodeId node, long index) => _claimVerified[node] = Math.Min(_claimVerified.GetValueOrDefault(node), index);

    /// <summary>Entries removed from a node's intended log since it last crashed, with the step that removed them.</summary>
    private void Removed(NodeId node, long step, List<Held> log, long from)
    {
        var removed = _removed.TryGetValue(node, out var r) ? r : _removed[node] = [];
        for (var j = from; j <= log.Count; j++)
        {
            removed.Add((step, log[(int)j - 1]));
        }
    }

    private void OnSent(LogHistory.Sent s)
    {
        var m = s.Message;
        var log = Intended(s.Node);
        var ghosts = new Dictionary<long, string>();
        for (var k = 0; k < m.Entries.Count; k++)
        {
            var index = m.PrevLogIndex + 1 + k;
            var term = m.Entries[k].Term;
            // The log as it was when the message was composed: an entry a later step removed, while
            // the barrier held this message behind an earlier write, was held then (P4-07).
            var held = At(log, index) is { } now && now.Term == term ? now
                : _removed.GetValueOrDefault(s.Node)?.Where(x => x.Step > s.Step && x.Entry.Index == index && x.Entry.Term == term).Select(x => x.Entry).FirstOrDefault();
            if (held is not null && held.Term == m.Entries[k].Term)
            {
                ghosts[index] = held.Ghost;
            }
            else
            {
                Fail("log-matching", $"{s.Node} sent entry {N(index)} of term {N(m.Entries[k].Term.Value)} that its log does not hold");
                ghosts[index] = "orphan-" + N(s.Id) + "-" + N(index);
            }
        }

        _messageGhosts[s.Id] = ghosts;
        _messageTerms[s.Id] = m.Term;
        if (m.LeaderCommit > 0)
        {
            Counts["claims"]++;
            Claim(s.Node, m.LeaderCommit, $"{s.Node} claimed commit index {N(m.LeaderCommit)}");
        }
    }

    /// <summary>Invariant 7: every index up to a claim is committed in fact, with the claimant's entry.</summary>
    private void Claim(NodeId node, long upTo, string what)
    {
        if (upTo > _committed.Count)
        {
            Fail("no-spurious-commit", $"{what}, but index {N(_committed.Count + 1)} is not committed in fact");
            return;
        }

        for (var i = _claimVerified.GetValueOrDefault(node) + 1; i <= upTo; i++)
        {
            if (GhostAt(node, i) != _committed[(int)i - 1].Ghost)
            {
                Fail("no-spurious-commit", $"{what}, but its entry at {N(i)} is not the one committed in fact");
                return;
            }
        }

        _claimVerified[node] = Math.Max(_claimVerified.GetValueOrDefault(node), upTo);
    }

    /// <summary>
    /// What a snapshot covers, as held entries (phase 7 decision 5): the prefix of the log that held its
    /// last entry when it was made, recorded then; an installed snapshot takes the record its maker
    /// left. Never read from the node. A snapshot no log ever held is a violation.
    /// </summary>
    private List<Held> Coverage(NodeId node, List<Held> log, LogHistory.SnapshotAt s)
    {
        if (s.Index == 0)
        {
            return [];
        }

        if (At(log, s.Index) is { } last && last.Term == s.Term)
        {
            var prefix = log.Take((int)s.Index).ToList();
            if (_coverage.TryGetValue((s.Index, s.Term), out var known) && !known.Select(x => x.Ghost).SequenceEqual(prefix.Select(x => x.Ghost)))
            {
                Fail("log-matching", $"{node}'s snapshot of ({N(s.Term.Value)}, {N(s.Index)}) covers other entries than an earlier snapshot of it");
            }

            _coverage.TryAdd((s.Index, s.Term), [.. prefix]);
            return prefix;
        }

        if (_coverage.TryGetValue((s.Index, s.Term), out var made))
        {
            return [.. made];
        }

        Fail("log-matching", $"{node} holds a snapshot of ({N(s.Term.Value)}, {N(s.Index)}) that no log held when it was made");
        return [.. Enumerable.Range(1, (int)s.Index).Select(k => new Held(k, k == s.Index ? s.Term : Term.Zero, "unmade-" + N(s.Index) + "-" + N(k), Term.Zero, _h.Initial))];
    }

    /// <summary>
    /// The log after a file replacement (P7-04): the snapshot's coverage, then the entries the new file
    /// holds after it, each the entry the log already held at that index and term (compaction and an
    /// install keep entries; they never create one). Over the logical log, so Leader Append-Only holds
    /// for a leader that compacts and fails for one whose replacement drops an entry.
    /// </summary>
    private List<Held> Replaced(NodeId node, List<Held> log, LogHistory.SnapshotAt s, IReadOnlyList<LogEntryAt> append, Func<LogEntryAt, Held?> known)
    {
        var next = Coverage(node, log, s);
        foreach (var e in append)
        {
            if (known(e) is { } held)
            {
                next.Add(held with { Config = ConfigAfter(next, e.Command) });
            }
            else
            {
                Fail("log-matching", $"{node}'s replaced log holds ({N(e.Term.Value)}, {N(e.Index)}), which it never held");
                next.Add(new Held(e.Index, e.Term, "unheld-" + N(e.Index), e.Term, ConfigAfter(next, e.Command)));
            }
        }

        return next;
    }

    private void OnIssued(LogHistory.Issued i)
    {
        var log = Intended(i.Node);
        _restarting.Remove(i.Node);
        var leader = _tenure.TryGetValue(i.Node, out var tenure);
        if (i.Snapshot is { } snap)
        {
            var old = log.ToList();
            var replaced = Replaced(i.Node, log, snap, i.Append, e => At(old, e.Index) is { } h && h.Term == e.Term ? h : null);
            var same = 0;
            while (same < old.Count && same < replaced.Count && old[same].Ghost == replaced[same].Ghost)
            {
                same++;
            }

            if (leader && same < old.Count)
            {
                Fail("leader-append-only", $"{i.Node}, leader of term {N(tenure.Value)}, replaced its log and lost its entry at {N(same + 1)}");
            }

            if (same < old.Count)
            {
                Removed(i.Node, i.Step, log, same + 1);
                Lower(i.Node, same);
            }

            log.Clear();
            log.AddRange(replaced);
            foreach (var h in replaced)
            {
                _lastIssued[(i.Node, h.Index, h.Term)] = (h.Ghost, h.CopyTerm);
            }

            return;
        }

        if (i.TruncateFrom > 0 && i.TruncateFrom <= log.Count)
        {
            if (leader)
            {
                Fail("leader-append-only", $"{i.Node}, leader of term {N(tenure.Value)}, truncated its log from {N(i.TruncateFrom)}");
            }

            Removed(i.Node, i.Step, log, i.TruncateFrom);
            log.RemoveRange((int)i.TruncateFrom - 1, log.Count - (int)i.TruncateFrom + 1);
            Lower(i.Node, i.TruncateFrom - 1);
            Counts["suffixes-truncated"]++;
        }

        var source = _stepSource.TryGetValue((i.Node, i.Step), out var src) ? src : ((long Id, Term Term)?)null;
        for (var k = 0; k < i.Append.Count; k++)
        {
            var e = i.Append[k];
            string ghost;
            Term copyTerm;
            if (source is { } from && _messageGhosts[from.Id].TryGetValue(e.Index, out var g))
            {
                ghost = g;
                copyTerm = from.Term;
            }
            else
            {
                ghost = "g" + N(++_ghosts);
                copyTerm = e.Term;
                _commands[ghost] = e.Command;
                _createdAt[ghost] = i.Seq;
                var key = (e.Term, e.Index);
                var created = _creations[key] = _creations.GetValueOrDefault(key) + 1;
                if (created > 1)
                {
                    Fail("entry-uniqueness", $"({N(e.Term.Value)}, {N(e.Index)}) was created {N(created)} times");
                }

                if (!leader || tenure != e.Term)
                {
                    Fail("entry-uniqueness", $"{i.Node} created an entry of term {N(e.Term.Value)} at {N(e.Index)} without being that term's leader");
                }
            }

            var existing = At(log, e.Index);
            if (existing is not null && existing.Ghost != ghost && leader)
            {
                Fail("leader-append-only", $"{i.Node}, leader of term {N(tenure.Value)}, overwrote its entry at {N(e.Index)}");
            }

            if (e.Index > log.Count + 1)
            {
                Fail("log-matching", $"{i.Node} appended at {N(e.Index)} past the end of its log");
                continue;
            }

            if (e.Index <= log.Count)
            {
                Removed(i.Node, i.Step, log, e.Index);
                log.RemoveRange((int)e.Index - 1, log.Count - (int)e.Index + 1);
                Lower(i.Node, e.Index - 1);
            }

            log.Add(new Held(e.Index, e.Term, ghost, copyTerm, ConfigAfter(log, e.Command)));
            _lastIssued[(i.Node, e.Index, e.Term)] = (ghost, copyTerm);
        }
    }

    private void OnDurable(LogHistory.Durable d)
    {
        var held = Durable(d.Node);
        var from = held.Count + 1L;
        if (d.Snapshot is { } snap)
        {
            var source = At(held, snap.Index) is { } h && h.Term == snap.Term ? held : Intended(d.Node);
            var replaced = Replaced(d.Node, source, snap, d.Append, e => _lastIssued.TryGetValue((d.Node, e.Index, e.Term), out var x)
                ? At(source, e.Index) is { } y && y.Ghost == x.Ghost ? y : new Held(e.Index, e.Term, x.Ghost, x.CopyTerm, _h.Initial)
                : null);
            from = 1;
            while (from <= held.Count && from <= replaced.Count && held[(int)from - 1].Ghost == replaced[(int)from - 1].Ghost)
            {
                from++;
            }

            held.Clear();
            held.AddRange(replaced);
            if (_restarting.Contains(d.Node))
            {
                Intended(d.Node).Clear();
                Intended(d.Node).AddRange(held);
                Lower(d.Node, held.Count);
            }

            CheckMatching(d.Node, held, from);
            Commit(held, from, d.Seq);
            CheckDurable(d, from);
            return;
        }

        if (d.TruncateFrom > 0 && d.TruncateFrom <= held.Count)
        {
            held.RemoveRange((int)d.TruncateFrom - 1, held.Count - (int)d.TruncateFrom + 1);
            from = d.TruncateFrom;
        }

        foreach (var e in d.Append)
        {
            if (!_lastIssued.TryGetValue((d.Node, e.Index, e.Term), out var x))
            {
                Fail("log-matching", $"{d.Node}'s disk holds ({N(e.Term.Value)}, {N(e.Index)}), which it never wrote");
                x = ("unwritten-" + N(d.Seq) + "-" + N(e.Index), e.Term);
            }

            if (e.Index <= held.Count)
            {
                held.RemoveRange((int)e.Index - 1, held.Count - (int)e.Index + 1);
            }

            from = Math.Min(from, e.Index);
            held.Add(new Held(e.Index, e.Term, x.Ghost, x.CopyTerm, ConfigAfter(held, e.Command)));
        }

        if (_restarting.Contains(d.Node))
        {
            Intended(d.Node).Clear();
            Intended(d.Node).AddRange(held);
            Lower(d.Node, held.Count);
        }

        CheckMatching(d.Node, held, from);
        Commit(held, from, d.Seq);
        CheckDurable(d, from);
    }

    private void CheckDurable(LogHistory.Durable d, long from)
    {
        // Invariant 6: every entry committed in fact stays durable on a quorum. Only indices at or
        // above the change can have lost a copy. The quorum is of the configuration in effect at the
        // latest committed index, the one any future leader is elected by (P6-08): an entry committed
        // under `C_old,new` need not stay on old servers that a committed `C_new` has since removed
        // (found by the membership sample, seed 152: the entry's own configuration was asked for
        // after the cluster had moved on twice).
        var current = _committed.Count > 0 ? _committed[^1].Config : _h.Initial;
        for (var i = from; i <= _committed.Count; i++)
        {
            var c = _committed[(int)i - 1];
            var holders = _durable.Where(l => At(l.Value, i)?.Ghost == c.Ghost).Select(l => l.Key).ToList();
            if (!current.IsQuorum(holders) && _belowQuorum.Add(i))
            {
                Fail("committed-durable", $"entry {N(i)}, committed in fact, is durable on only {N(holders.Count)} node(s) after {d.Node}'s disk changed");
            }
        }
    }

    /// <summary>
    /// Invariant 3 over the durable logs: a shared (index, term) means identical logs up to it. Only
    /// from the changed range upward needs checking; below it, both logs were compared when they last
    /// changed.
    /// </summary>
    private void CheckMatching(NodeId node, List<Held> held, long from)
    {
        foreach (var (other, log) in _durable.Where(x => x.Key != node))
        {
            var shared = 0L;
            for (var j = (long)Math.Min(held.Count, log.Count); j >= from; j--)
            {
                if (held[(int)j - 1].Term == log[(int)j - 1].Term)
                {
                    shared = j;
                    break;
                }
            }

            for (var i = Math.Max(1, from - 1); i <= shared; i++)
            {
                if (held[(int)i - 1].Ghost != log[(int)i - 1].Ghost)
                {
                    Fail("log-matching", $"{node} and {other} share an entry at {N(shared)} but differ at {N(i)}");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Commitment in fact: an entry of term T durable on a quorum in copies written in term T (by
    /// its creator or by that term's AppendEntries) commits every index up to it. A copy placed by a
    /// later leader does not count: in Figure 8(c) the term-2 entry is on a majority and is not
    /// committed. A new quorum can only form at an index this node's disk just changed.
    /// </summary>
    private void Commit(List<Held> held, long from, long seq)
    {
        for (var j = (long)held.Count; j >= Math.Max(from, _committed.Count + 1); j--)
        {
            var e = held[(int)j - 1];
            if (e.CopyTerm != e.Term)
            {
                continue;
            }

            // A quorum of the configuration in effect at this index (P6-04): during joint consensus a
            // majority of both, a removed server's copy counting for nothing.
            var copies = _durable.Where(l => At(l.Value, j) is { } x && x.Ghost == e.Ghost && x.CopyTerm == e.Term).Select(l => l.Key).ToList();
            if (!e.Config.IsQuorum(copies))
            {
                continue;
            }

            for (var p = 1L; p <= j; p++)
            {
                var ghost = held[(int)p - 1].Ghost;
                if (p <= _committed.Count)
                {
                    if (_committed[(int)p - 1].Ghost != ghost)
                    {
                        Fail("leader-completeness", $"index {N(p)} was committed in fact, then another entry was committed there");
                    }

                    continue;
                }

                _committed.Add((ghost, e.Term, held[(int)p - 1].Config));
                if (held[(int)p - 1].Term < e.Term)
                {
                    Counts["committed-in-a-later-term"]++;
                }

                _commits.Add(new CommitInFact(ghost, p, seq, _createdAt.GetValueOrDefault(ghost, -1), _commands.TryGetValue(ghost, out var c) && c.Length > 0));
                foreach (var (leader, term) in _tenure.Where(t => t.Value > e.Term).ToList())
                {
                    RequireHeld(leader, term, p, ghost, "in office");
                }
            }

            return;
        }
    }

    /// <summary>The configuration in effect after appending an entry with <paramref name="command"/> to <paramref name="log"/>.</summary>
    private Configuration ConfigAfter(List<Held> log, byte[] command) =>
        Configuration.IsInternal(command) && Configuration.Decode(command) is { } c ? c : log.Count > 0 ? log[^1].Config : _h.Initial;

    /// <summary>Invariant 4: a leader of a higher term holds every entry committed in fact.</summary>
    private void RequireHeld(NodeId leader, Term term, long index, string ghost, string when)
    {
        if (GhostAt(leader, index) != ghost)
        {
            Fail("leader-completeness", $"{leader}, leader of term {N(term.Value)} ({when}), lacks the entry committed in fact at {N(index)}");
        }
    }

    private void OnApplied(LogHistory.Applied a)
    {
        Counts["applies"]++;
        var ghost = GhostAt(a.Node, a.Index);
        if (ghost is null)
        {
            Fail("state-machine-safety", $"{a.Node} applied index {N(a.Index)}, which its log does not hold");
            return;
        }

        Claim(a.Node, a.Index, $"{a.Node} applied index {N(a.Index)}");
        var key = _byBytes ? Convert.ToHexString(_commands.GetValueOrDefault(ghost, [])) : ghost;
        if (_applied.TryGetValue(a.Index, out var was))
        {
            if (was.Key != key)
            {
                Fail("state-machine-safety", $"{a.Node} applied a different entry at {N(a.Index)} than {was.Node} did");
            }
        }
        else
        {
            _applied[a.Index] = (key, a.Node);
        }
    }
}

/// <summary>The log invariants (spec §5: 2, 3, 4, 5, 6, 7, 10), each a view of one <see cref="LogAnalysis"/>.</summary>
internal static class LogInvariants
{
    public static IReadOnlyList<InvariantResult> All(LogHistory h)
    {
        var a = new LogAnalysis(h);
        return LogAnalysis.Names.Select(a.Result).ToList();
    }

    public static InvariantResult Check(LogHistory h, string name, bool byBytes = false) => new LogAnalysis(h, byBytes).Result(name);
}
