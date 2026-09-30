using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Raft.Core;

namespace Raft.Core.Tests;

/// <summary>An entry as a log holds it: its index, its term and its command's bytes.</summary>
internal sealed record LogEntryAt(long Index, Term Term, byte[] Command);

/// <summary>
/// An execution's log events, in observation order (P4-01): writes each node issued (with the
/// step that issued them), each node's durable log after every change, the `AppendEntries` sent
/// and delivered (a delivery starts a step at its receiver), crashes, elections and the end of each
/// leader's term, and applies. Built by the observation adapter (P4-03) or by hand in tests; ghost
/// entry ids are never part of it. They are assigned by <see cref="LogAnalysis"/> from creation and
/// provenance (P4 decision 1), because an id carried in the entry would be the node's own claim
/// about identity.
/// </summary>
internal sealed class LogHistory
{
    public abstract record Event(long Seq, NodeId Node);

    /// <summary>A log write: an optional truncation (drop every index at or above <paramref name="TruncateFrom"/>; 0 for none), then entries appended.</summary>
    public sealed record Issued(long Seq, NodeId Node, long Step, long TruncateFrom, IReadOnlyList<LogEntryAt> Append) : Event(Seq, Node);

    /// <summary>The node's durable log after a write completed or a crash changed it.</summary>
    public sealed record Durable(long Seq, NodeId Node, IReadOnlyList<LogEntryAt> Log) : Event(Seq, Node);

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
}

/// <summary>
/// One pass over a <see cref="LogHistory"/>: ghost ids, each node's intended and durable logs,
/// commitment in fact, and every log invariant's violations (spec §5: 2, 3, 4, 5, 6, 7, 10).
/// </summary>
internal sealed class LogAnalysis
{
    /// <summary>An entry as a node holds it: the ghost id, and the term of the leader whose write put this copy there.</summary>
    private sealed record Held(long Index, Term Term, string Ghost, Term CopyTerm);

    private readonly LogHistory _h;
    private readonly bool _byBytes;
    private readonly Dictionary<NodeId, List<Held>> _intended = [];
    private readonly Dictionary<NodeId, List<Held>> _durable = [];
    private readonly Dictionary<long, Dictionary<long, string>> _messageGhosts = [];
    private readonly Dictionary<(NodeId, long), (long Id, Term Term)> _stepSource = [];
    private readonly Dictionary<(Term, long), List<string>> _creations = [];
    private readonly Dictionary<string, byte[]> _commands = [];
    private readonly Dictionary<(NodeId, long, Term), (string Ghost, Term CopyTerm)> _lastIssued = [];
    private readonly Dictionary<long, (string Ghost, Term Term, long Seq)> _committed = [];
    private readonly Dictionary<long, (string Key, NodeId Node)> _applied = [];
    private readonly Dictionary<NodeId, Term> _tenure = [];
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
                    if (_messageGhosts.ContainsKey(d.Id))
                    {
                        _stepSource[(d.Node, d.Step)] = (d.Id, _messageTerms[d.Id]);
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
                    _restarting.Add(c.Node);
                    break;
                case LogHistory.Elected el:
                    _tenure[el.Node] = el.Term;
                    Counts["elections"]++;
                    if (Intended(el.Node).Count > 0)
                    {
                        Counts["leaders-with-entries"]++;
                    }

                    foreach (var (index, c) in _committed.Where(c => c.Value.Term < el.Term))
                    {
                        RequireHeld(el.Node, el.Term, index, c.Ghost, "elected");
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
        Counts["terms-with-a-commit"] = _committed.Values.Select(c => c.Term).Distinct().Count();
        Counts["entries-created"] = _creations.Values.Sum(v => v.Count);
    }

    public static readonly string[] Names = ["leader-append-only", "log-matching", "leader-completeness", "state-machine-safety", "committed-durable", "no-spurious-commit", "entry-uniqueness"];

    private readonly Dictionary<long, Term> _messageTerms = [];

    public Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal)
    {
        ["elections"] = 0, ["leaders-with-entries"] = 0, ["suffixes-truncated"] = 0, ["applies"] = 0, ["claims"] = 0,
    };

    /// <summary>The ghost id of the entry a node holds at an index in its intended log, or null.</summary>
    public string? GhostAt(NodeId node, long index) => Intended(node).FirstOrDefault(x => x.Index == index)?.Ghost;

    public InvariantResult Result(string name) => new(name, _violations[name], Counts);

    private List<Held> Intended(NodeId n) => _intended.TryGetValue(n, out var l) ? l : _intended[n] = [];

    private List<Held> Durable(NodeId n) => _durable.TryGetValue(n, out var l) ? l : _durable[n] = [];

    private void Fail(string invariant, string message) => _violations[invariant].Add(message);

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    private void OnSent(LogHistory.Sent s)
    {
        var m = s.Message;
        var log = Intended(s.Node);
        var ghosts = new Dictionary<long, string>();
        for (var k = 0; k < m.Entries.Count; k++)
        {
            var index = m.PrevLogIndex + 1 + k;
            var held = log.FirstOrDefault(x => x.Index == index);
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
        for (var i = 1L; i <= upTo; i++)
        {
            var mine = GhostAt(node, i);
            if (!_committed.TryGetValue(i, out var c))
            {
                Fail("no-spurious-commit", $"{what}, but index {N(i)} is not committed in fact");
                return;
            }

            if (mine != c.Ghost)
            {
                Fail("no-spurious-commit", $"{what}, but its entry at {N(i)} is not the one committed in fact");
                return;
            }
        }
    }

    private void OnIssued(LogHistory.Issued i)
    {
        var log = Intended(i.Node);
        _restarting.Remove(i.Node);
        var leader = _tenure.TryGetValue(i.Node, out var tenure);
        if (i.TruncateFrom > 0 && log.Any(x => x.Index >= i.TruncateFrom))
        {
            if (leader)
            {
                Fail("leader-append-only", $"{i.Node}, leader of term {N(tenure.Value)}, truncated its log from {N(i.TruncateFrom)}");
            }

            log.RemoveAll(x => x.Index >= i.TruncateFrom);
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
                var key = (e.Term, e.Index);
                if (!_creations.TryGetValue(key, out var list))
                {
                    _creations[key] = list = [];
                }

                list.Add(ghost);
                if (list.Count > 1)
                {
                    Fail("entry-uniqueness", $"({N(e.Term.Value)}, {N(e.Index)}) was created {N(list.Count)} times");
                }

                if (!leader || tenure != e.Term)
                {
                    Fail("entry-uniqueness", $"{i.Node} created an entry of term {N(e.Term.Value)} at {N(e.Index)} without being that term's leader");
                }
            }

            var existing = log.FirstOrDefault(x => x.Index == e.Index);
            if (existing is not null && existing.Ghost != ghost && leader)
            {
                Fail("leader-append-only", $"{i.Node}, leader of term {N(tenure.Value)}, overwrote its entry at {N(e.Index)}");
            }

            if (e.Index > (log.Count == 0 ? 0 : log.Max(x => x.Index)) + 1)
            {
                Fail("log-matching", $"{i.Node} appended at {N(e.Index)} past the end of its log");
            }

            log.RemoveAll(x => x.Index >= e.Index);
            log.Add(new Held(e.Index, e.Term, ghost, copyTerm));
            _lastIssued[(i.Node, e.Index, e.Term)] = (ghost, copyTerm);
        }
    }

    private void OnDurable(LogHistory.Durable d)
    {
        var held = new List<Held>();
        foreach (var e in d.Log)
        {
            if (_lastIssued.TryGetValue((d.Node, e.Index, e.Term), out var x))
            {
                held.Add(new Held(e.Index, e.Term, x.Ghost, x.CopyTerm));
            }
            else
            {
                Fail("log-matching", $"{d.Node}'s disk holds ({N(e.Term.Value)}, {N(e.Index)}), which it never wrote");
            }
        }

        _durable[d.Node] = held;
        if (_restarting.Contains(d.Node))
        {
            Intended(d.Node).Clear();
            Intended(d.Node).AddRange(held);
        }

        // Invariant 3 over the durable logs: a shared (index, term) means identical logs up to it.
        foreach (var (other, log) in _durable.Where(x => x.Key != d.Node))
        {
            var shared = held.Where(a => log.Any(b => b.Index == a.Index && b.Term == a.Term)).Select(a => a.Index).DefaultIfEmpty(0).Max();
            for (var i = 1L; i <= shared; i++)
            {
                var a = held.FirstOrDefault(x => x.Index == i)?.Ghost;
                var b = log.FirstOrDefault(x => x.Index == i)?.Ghost;
                if (a != b)
                {
                    Fail("log-matching", $"{d.Node} and {other} share an entry at {N(shared)} but differ at {N(i)}");
                    break;
                }
            }
        }

        // Commitment in fact: an entry of term T durable on a quorum in copies written in term T
        // (by its creator or by that term's AppendEntries) commits every index up to it. A copy
        // placed by a later leader does not count: in Figure 8(c) the term-2 entry is on a majority
        // and is not committed.
        foreach (var e in held)
        {
            var copies = _durable.Values.Count(l => l.Any(x => x.Index == e.Index && x.Ghost == e.Ghost && x.CopyTerm == e.Term));
            if (copies < _h.Quorum)
            {
                continue;
            }

            foreach (var p in held.Where(p => p.Index <= e.Index))
            {
                if (_committed.TryGetValue(p.Index, out var was))
                {
                    if (was.Ghost != p.Ghost)
                    {
                        Fail("leader-completeness", $"index {N(p.Index)} was committed in fact, then another entry was committed there");
                    }

                    continue;
                }

                _committed[p.Index] = (p.Ghost, e.Term, d.Seq);
                foreach (var (leader, term) in _tenure.Where(t => t.Value > e.Term).ToList())
                {
                    RequireHeld(leader, term, p.Index, p.Ghost, "in office");
                }
            }
        }

        // Invariant 6: every entry committed in fact stays durable on a quorum.
        foreach (var (index, c) in _committed)
        {
            var holders = _durable.Values.Count(l => l.Any(x => x.Index == index && x.Ghost == c.Ghost));
            if (holders < _h.Quorum && _belowQuorum.Add(index))
            {
                Fail("committed-durable", $"entry {N(index)}, committed in fact, is durable on only {N(holders)} node(s) after {d.Node}'s disk changed");
            }
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
    public static IReadOnlyList<InvariantResult> All(LogHistory h) => LogAnalysis.Names.Select(new LogAnalysis(h).Result).ToList();

    public static InvariantResult Check(LogHistory h, string name, bool byBytes = false) => new LogAnalysis(h, byBytes).Result(name);
}
