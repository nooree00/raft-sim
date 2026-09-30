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

    /// <summary>A log write the node issued in <paramref name="Step"/>.</summary>
    public sealed record Issued(long Seq, NodeId Node, long Step, long TruncateFrom, IReadOnlyList<LogEntryAt> Append) : Event(Seq, Node);

    /// <summary>The node's durable log changed: a write completed, or a crash changed the disk.</summary>
    public sealed record Durable(long Seq, NodeId Node, long TruncateFrom, IReadOnlyList<LogEntryAt> Append) : Event(Seq, Node);

    /// <summary>An `AppendEntries` the node sent; its `LeaderCommit` is the sender's claim of commitment.</summary>
    public sealed record Sent(long Seq, NodeId Node, long Id, AppendEntries Message) : Event(Seq, Node);

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

    public int Quorum => (ClusterSize / 2) + 1;

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
        var restarting = new HashSet<NodeId>();
        FileView View(Dictionary<NodeId, FileView> d, NodeId n) => d.TryGetValue(n, out var v) ? v : d[n] = new FileView();
        long seq = 0;
        foreach (var o in observations)
        {
            seq++;
            switch (o)
            {
                case SentObservation s when MessageCodec.Decode(s.Payload.ToArray()) is AppendEntries ae:
                    events.Add(new Sent(seq, s.Node, s.Id, ae));
                    break;
                case DeliveredObservation d:
                    events.Add(new Delivered(seq, d.Node, d.Id, d.Step));
                    break;
                case IssuedObservation { Op: { File: EntryLog.FileName } op } i:
                    restarting.Remove(i.Node);
                    var (cut, added) = View(intended, i.Node).Apply(op);
                    events.Add(new Issued(seq, i.Node, i.Step, cut, added));
                    break;
                case IssuedObservation { Op: PersistAppend { File: TermVoteLog.FileName } tv } i:
                    var term = TermVoteLog.Recover(tv.Data.ToArray()).State.Term;
                    events.Add(new LeftTerm(seq, i.Node, new Term(term.Value - 1)));
                    break;
                case DurableObservation { File: EntryLog.FileName } du:
                    var (dcut, dadded) = du.Completed is { } done ? View(durable, du.Node).Apply(done) : View(durable, du.Node).Replace(du.Content?.ToArray() ?? []);
                    events.Add(new Durable(seq, du.Node, dcut, dadded));
                    if (restarting.Contains(du.Node))
                    {
                        intended[du.Node] = View(durable, du.Node).Copy();
                    }

                    break;
                case CrashObservation c:
                    events.Add(new Crashed(seq, c.Node));
                    intended[c.Node] = View(durable, c.Node).Copy();
                    restarting.Add(c.Node);
                    break;
                case EmittedObservation { Event.Name: "apply" } em:
                    events.Add(new Applied(seq, em.Node, long.Parse(em.Event.Fields.First(f => f.Key == "index").Value, CultureInfo.InvariantCulture)));
                    break;
            }
        }

        return new LogHistory(clusterSize, events);
    }

    /// <summary>A log file as a node's recovery reads it, kept up to date write by write.</summary>
    internal sealed class FileView
    {
        private byte[] _bytes = new byte[256];
        private int _length;
        private List<StoredEntry> _entries = [];
        private long _valid;

        public int Count => _entries.Count;

        public FileView Copy() => new() { _bytes = (byte[])_bytes.Clone(), _length = _length, _entries = [.. _entries], _valid = _valid };

        public (long TruncateFrom, List<LogEntryAt> Append) Apply(Persist op)
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
                    return (0, []);
            }
        }

        public (long TruncateFrom, List<LogEntryAt> Append) Replace(byte[] content)
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

        private (long, List<LogEntryAt>) Resume()
        {
            var before = _entries.Count;
            var r = EntryLog.Resume(_entries, _bytes, _length, _valid);
            _valid = r.ValidLength;
            if (r.FirstChanged == 0)
            {
                return (0, []);
            }

            return (r.FirstChanged <= before ? r.FirstChanged : 0, _entries.Skip((int)r.FirstChanged - 1).Select(At).ToList());
        }

        private (long, List<LogEntryAt>) Reread()
        {
            var old = _entries.Select(At).ToList();
            _entries = [];
            var r = EntryLog.Resume(_entries, _bytes, _length, 0);
            _valid = r.ValidLength;
            return Change(old, _entries.Select(At).ToList());
        }

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
    private sealed record Held(long Index, Term Term, string Ghost, Term CopyTerm);

    public static readonly string[] Names = ["leader-append-only", "log-matching", "leader-completeness", "state-machine-safety", "committed-durable", "no-spurious-commit", "entry-uniqueness"];

    private readonly LogHistory _h;
    private readonly bool _byBytes;
    private readonly Dictionary<NodeId, List<Held>> _intended = [];
    private readonly Dictionary<NodeId, List<Held>> _durable = [];
    private readonly Dictionary<long, Dictionary<long, string>> _messageGhosts = [];
    private readonly Dictionary<long, Term> _messageTerms = [];
    private readonly Dictionary<(NodeId, long), (long Id, Term Term)> _stepSource = [];
    private readonly Dictionary<(Term, long), int> _creations = [];
    private readonly Dictionary<string, byte[]> _commands = [];
    private readonly Dictionary<(NodeId, long, Term), (string Ghost, Term CopyTerm)> _lastIssued = [];
    private readonly List<(string Ghost, Term Term)> _committed = [];
    private readonly List<CommitInFact> _commits = [];
    private readonly Dictionary<string, long> _createdAt = new(StringComparer.Ordinal);
    private readonly Dictionary<long, (string Key, NodeId Node)> _applied = [];
    private readonly Dictionary<NodeId, Term> _tenure = [];
    private readonly Dictionary<NodeId, long> _claimVerified = [];
    private readonly HashSet<NodeId> _restarting = [];
    private readonly HashSet<long> _belowQuorum = [];
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
                    break;
                case LogHistory.Durable du:
                    OnDurable(du);
                    break;
                case LogHistory.Crashed c:
                    _tenure.Remove(c.Node);
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
            }
        }

        Counts["entries-committed"] = _committed.Count;
        Counts["terms-with-a-commit"] = _committed.Select(c => c.Term).Distinct().Count();
        Counts["entries-created"] = _creations.Values.Sum();
    }

    public Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal)
    {
        ["elections"] = 0, ["leaders-with-entries"] = 0, ["suffixes-truncated"] = 0, ["applies"] = 0, ["claims"] = 0,
    };

    /// <summary>
    /// An entry committed in fact: its ghost id and index, the event (observation sequence number) at
    /// which a quorum's durable copies committed it, the event that created it, and whether it carries
    /// a client's command (a leader's no-op is empty).
    /// </summary>
    public sealed record CommitInFact(string Ghost, long Index, long Seq, long CreatedSeq, bool Command);

    /// <summary>Every entry committed in fact, in index order (P4-06: invariant 11's commit clause reads it).</summary>
    public IReadOnlyList<CommitInFact> Commits => _commits;

    /// <summary>The ghost id of the entry a node holds at an index in its intended log, or null.</summary>
    public string? GhostAt(NodeId node, long index) => At(Intended(node), index)?.Ghost;

    public InvariantResult Result(string name) => new(name, _violations[name], Counts);

    private static Held? At(List<Held> log, long index) => index >= 1 && index <= log.Count ? log[(int)index - 1] : null;

    private List<Held> Intended(NodeId n) => _intended.TryGetValue(n, out var l) ? l : _intended[n] = [];

    private List<Held> Durable(NodeId n) => _durable.TryGetValue(n, out var l) ? l : _durable[n] = [];

    private void Fail(string invariant, string message) => _violations[invariant].Add(message);

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Claims above this index are verified again: the node's log changed at or below it.</summary>
    private void Lower(NodeId node, long index) => _claimVerified[node] = Math.Min(_claimVerified.GetValueOrDefault(node), index);

    private void OnSent(LogHistory.Sent s)
    {
        var m = s.Message;
        var log = Intended(s.Node);
        var ghosts = new Dictionary<long, string>();
        for (var k = 0; k < m.Entries.Count; k++)
        {
            var index = m.PrevLogIndex + 1 + k;
            var held = At(log, index);
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

    private void OnIssued(LogHistory.Issued i)
    {
        var log = Intended(i.Node);
        _restarting.Remove(i.Node);
        var leader = _tenure.TryGetValue(i.Node, out var tenure);
        if (i.TruncateFrom > 0 && i.TruncateFrom <= log.Count)
        {
            if (leader)
            {
                Fail("leader-append-only", $"{i.Node}, leader of term {N(tenure.Value)}, truncated its log from {N(i.TruncateFrom)}");
            }

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
                log.RemoveRange((int)e.Index - 1, log.Count - (int)e.Index + 1);
                Lower(i.Node, e.Index - 1);
            }

            log.Add(new Held(e.Index, e.Term, ghost, copyTerm));
            _lastIssued[(i.Node, e.Index, e.Term)] = (ghost, copyTerm);
        }
    }

    private void OnDurable(LogHistory.Durable d)
    {
        var held = Durable(d.Node);
        var from = held.Count + 1L;
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
            held.Add(new Held(e.Index, e.Term, x.Ghost, x.CopyTerm));
        }

        if (_restarting.Contains(d.Node))
        {
            Intended(d.Node).Clear();
            Intended(d.Node).AddRange(held);
            Lower(d.Node, held.Count);
        }

        CheckMatching(d.Node, held, from);
        Commit(held, from, d.Seq);

        // Invariant 6: every entry committed in fact stays durable on a quorum. Only indices at or
        // above the change can have lost a copy.
        for (var i = from; i <= _committed.Count; i++)
        {
            var c = _committed[(int)i - 1];
            var holders = _durable.Values.Count(l => At(l, i)?.Ghost == c.Ghost);
            if (holders < _h.Quorum && _belowQuorum.Add(i))
            {
                Fail("committed-durable", $"entry {N(i)}, committed in fact, is durable on only {N(holders)} node(s) after {d.Node}'s disk changed");
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

            var copies = _durable.Values.Count(l => At(l, j) is { } x && x.Ghost == e.Ghost && x.CopyTerm == e.Term);
            if (copies < _h.Quorum)
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

                _committed.Add((ghost, e.Term));
                _commits.Add(new CommitInFact(ghost, p, seq, _createdAt.GetValueOrDefault(ghost, -1), _commands.TryGetValue(ghost, out var c) && c.Length > 0));
                foreach (var (leader, term) in _tenure.Where(t => t.Value > e.Term).ToList())
                {
                    RequireHeld(leader, term, p, ghost, "in office");
                }
            }

            return;
        }
    }

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
