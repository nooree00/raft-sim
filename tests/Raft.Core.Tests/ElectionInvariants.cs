using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Raft.Core;
using Raft.Simulation;

namespace Raft.Core.Tests;

/// <summary>One checker's verdict, with the counts that show the mechanism happened (spec §5: assert the mechanism, with a count).</summary>
internal sealed record InvariantResult(string Invariant, IReadOnlyList<string> Violations, IReadOnlyDictionary<string, long> Counts)
{
    public bool Holds => Violations.Count == 0;

    public long Count(string key) => Counts.GetValueOrDefault(key);
}

/// <summary>
/// An execution as the simulator observed it, decoded (P3-04): messages sent and delivered, each
/// node's durable term-and-vote state over time, and when each node was up. Built only from
/// <see cref="Observation"/>s, never from a node's report about itself.
/// </summary>
internal sealed class ElectionHistory
{
    /// <summary>Each record keeps <c>Seq</c>, its position in the observation stream: the true order of events, which many events sharing one instant do not give.</summary>
    public sealed record Sent(long Seq, long Time, NodeId From, NodeId To, long Id, Message Message);

    public sealed record Delivered(long Seq, long Time, NodeId To, NodeId From, Message Message);

    public sealed record Durable(long Seq, long Time, NodeId Node, TermVoteState State, RecoveryPath Path);

    public List<Sent> Sends { get; } = [];

    public List<Delivered> Deliveries { get; } = [];

    public List<Durable> States { get; } = [];

    /// <summary>Term-and-vote records the node issued, each self-contained: its intended state from that moment, durable or not.</summary>
    public List<Durable> Intended { get; } = [];

    public List<(long Time, NodeId Node, bool Up)> Liveness { get; } = [];

    public List<(long Seq, long Time, NodeId Node)> CrashSeqs { get; } = [];

    public int ClusterSize { get; }

    public int Quorum => (ClusterSize / 2) + 1;

    public long Undecodable { get; private set; }

    public ElectionHistory(IEnumerable<Observation> observations, int clusterSize)
    {
        ClusterSize = clusterSize;
        var byId = new Dictionary<long, Message>();
        long seq = 0;
        foreach (var o in observations)
        {
            seq++;
            switch (o)
            {
                case SentObservation s when MessageCodec.Decode(s.Payload.ToArray()) is { } m:
                    byId[s.Id] = m;
                    Sends.Add(new Sent(seq, s.Time, s.Node, s.To, s.Id, m));
                    break;
                case SentObservation:
                    Undecodable++;
                    break;
                case DeliveredObservation d when byId.TryGetValue(d.Id, out var m):
                    Deliveries.Add(new Delivered(seq, d.Time, d.Node, d.From, m));
                    break;
                case DurableObservation { File: TermVoteLog.FileName } d:
                    var r = TermVoteLog.Recover(d.Content?.ToArray());
                    States.Add(new Durable(seq, d.Time, d.Node, r.State, r.Path));
                    break;
                case IssuedObservation { Op: PersistAppend { File: TermVoteLog.FileName } a } i:
                    var rec = TermVoteLog.Recover(a.Data.ToArray());
                    Intended.Add(new Durable(seq, i.Time, i.Node, rec.State, rec.Path));
                    break;
                case StartObservation st:
                    Liveness.Add((st.Time, st.Node, true));
                    break;
                case CrashObservation c:
                    Liveness.Add((c.Time, c.Node, false));
                    CrashSeqs.Add((seq, c.Time, c.Node));
                    break;
            }
        }
    }

    /// <summary>
    /// When each candidate became elected in each term: the first moment a quorum had granted it,
    /// counting its own durable vote for itself and the grants delivered to it while it was still a
    /// candidate in that term, meaning its latest record, issued or durable, was that term with a vote
    /// for itself. A grant that arrives after the candidate moved to a later term elects no one (found by
    /// the soak twice: a delayed grant, and a grant arriving while the next candidacy's record was still
    /// in flight). After a crash the latest record is the durable one: writes in flight may be lost.
    /// </summary>
    public Dictionary<(Term Term, NodeId Candidate), long> Elections()
    {
        var voters = new Dictionary<(Term, NodeId), HashSet<NodeId>>();
        var elected = new Dictionary<(Term, NodeId), long>();
        var latest = new Dictionary<NodeId, TermVoteState>();
        var durable = new Dictionary<NodeId, TermVoteState>();
        var issuedSinceCrash = new HashSet<NodeId>();
        var events = Deliveries.Where(d => d.Message is RequestVoteResponse { VoteGranted: true })
            .Select(d => (d.Seq, d.Time, Kind: 0, Node: d.To, d.Message.Term, Voter: d.From, State: (TermVoteState?)null))
            .Concat(States.Select(s => (s.Seq, s.Time, Kind: 1, s.Node, s.State.Term, Voter: s.Node, State: (TermVoteState?)s.State)))
            .Concat(Intended.Select(s => (s.Seq, s.Time, Kind: 2, s.Node, s.State.Term, Voter: s.Node, State: (TermVoteState?)s.State)))
            .Concat(CrashSeqs.Select(c => (c.Seq, c.Time, Kind: 3, c.Node, Term.Zero, Voter: c.Node, State: (TermVoteState?)null)))
            .OrderBy(e => e.Seq);
        foreach (var (_, time, kind, node, term, voter, state) in events)
        {
            if (kind == 3)
            {
                issuedSinceCrash.Remove(node);
                latest[node] = durable.GetValueOrDefault(node, TermVoteState.Initial);
                continue;
            }

            if (kind == 2)
            {
                issuedSinceCrash.Add(node);
                latest[node] = state!;
                continue;
            }

            if (kind == 1)
            {
                // An older record completing does not undo a newer one already issued.
                durable[node] = state!;
                if (!issuedSinceCrash.Contains(node))
                {
                    latest[node] = state!;
                }

                if (state!.VotedFor != node)
                {
                    continue;
                }
            }
            else if (!latest.TryGetValue(node, out var now) || now.Term != term || now.VotedFor != node)
            {
                continue;
            }

            var key = (term, node);
            if (!voters.TryGetValue(key, out var set))
            {
                voters[key] = set = [];
            }

            if (set.Add(voter) && set.Count >= Quorum && !elected.ContainsKey(key))
            {
                elected[key] = time;
            }
        }

        return elected;
    }

    public bool IsUp(NodeId node, long time)
    {
        var up = false;
        foreach (var (t, n, u) in Liveness.Where(l => l.Node == node).OrderBy(l => l.Time))
        {
            if (t > time)
            {
                break;
            }

            up = u;
        }

        return up;
    }
}

/// <summary>Invariants 1, 8, 9 and 11 (spec §5), phase 3's scope, over an <see cref="ElectionHistory"/>.</summary>
internal static class ElectionInvariants
{
    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Invariant 1, Election Safety: at most one candidate elected per term over the whole run. Also
    /// decision 4's two views of leadership: a node sending AppendEntries as leader of a term must
    /// have been elected in it, and an elected node that stays up must act within <paramref name="actWithin"/>.
    /// </summary>
    public static InvariantResult ElectionSafety(ElectionHistory h, long actWithin)
    {
        var violations = new List<string>();
        var elected = h.Elections();
        foreach (var term in elected.Keys.GroupBy(k => k.Term).Where(g => g.Count() > 1))
        {
            violations.Add("term " + N(term.Key.Value) + ": elected " + string.Join(" and ", term.Select(k => k.Candidate + " at " + N(elected[k]))));
        }

        var acting = h.Sends.Where(s => s.Message is AppendEntries a && a.Leader == s.From).ToList();
        foreach (var s in acting.GroupBy(s => (s.Message.Term, s.From)).Select(g => g.First()))
        {
            if (!elected.TryGetValue((s.Message.Term, s.From), out var at) || at > s.Time)
            {
                violations.Add(s.From + " acts as leader of term " + N(s.Message.Term.Value) + " at " + N(s.Time) + " without an observed quorum");
            }
        }

        foreach (var ((term, c), at) in elected)
        {
            var deadline = at + actWithin;
            // Acting, or leaving office: a higher term sent or made durable (a leader steps down silently on a response with a higher term).
            var acted = h.Sends.Any(s => s.From == c && s.Time >= at && s.Time <= deadline && (s.Message.Term > term || (s.Message is AppendEntries && s.Message.Term == term)))
                || h.States.Any(s => s.Node == c && s.Time >= at && s.Time <= deadline && s.State.Term > term);
            var stayedUp = !h.Liveness.Any(l => l.Node == c && !l.Up && l.Time > at && l.Time <= deadline);
            if (!acted && stayedUp && h.Liveness.Count > 0 && h.Liveness.Max(l => l.Time) >= deadline)
            {
                violations.Add(c + " elected in term " + N(term.Value) + " at " + N(at) + " stays up and never acts by " + N(deadline));
            }
        }

        return new("election-safety", violations, new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["elections"] = elected.Count,
            ["terms-with-a-leader"] = elected.Keys.Select(k => k.Term).Distinct().Count(),
            ["terms-with-a-candidate"] = h.Sends.Where(s => s.Message is RequestVote).Select(s => s.Message.Term).Distinct().Count(),
            ["acting-leaders"] = acting.Select(s => (s.Message.Term, s.From)).Distinct().Count(),
        });
    }

    /// <summary>
    /// Invariant 8, Vote Uniqueness: per node and term, at most one candidate, over its durable vote
    /// records and the grants it sent, restarts included. <paramref name="wireOnly"/> ignores the
    /// durable records and <paramref name="deliveredOnly"/> counts only grants that arrived: the
    /// weaker observations, kept so a test can show what each misses (P3-04's prediction).
    /// </summary>
    public static InvariantResult VoteUniqueness(ElectionHistory h, bool wireOnly = false, bool deliveredOnly = false)
    {
        var votes = new Dictionary<(NodeId Voter, Term Term), SortedSet<int>>();
        void Add(NodeId voter, Term term, NodeId candidate)
        {
            if (!votes.TryGetValue((voter, term), out var set))
            {
                votes[(voter, term)] = set = [];
            }

            set.Add(candidate.Value);
        }

        var grants = deliveredOnly
            ? h.Deliveries.Where(d => d.Message is RequestVoteResponse { VoteGranted: true }).Select(d => (Voter: d.From, d.Message.Term, Candidate: d.To))
            : h.Sends.Where(s => s.Message is RequestVoteResponse { VoteGranted: true }).Select(s => (Voter: s.From, s.Message.Term, Candidate: s.To));
        foreach (var (voter, term, candidate) in grants)
        {
            Add(voter, term, candidate);
        }

        if (!wireOnly)
        {
            foreach (var s in h.States.Where(s => s.State.VotedFor is not null))
            {
                Add(s.Node, s.State.Term, s.State.VotedFor!.Value);
            }
        }

        var violations = votes.Where(kv => kv.Value.Count > 1)
            .Select(kv => kv.Key.Voter + " voted for " + string.Join(" and ", kv.Value.Select(v => "n" + N(v))) + " in term " + N(kv.Key.Term.Value)).ToList();
        return new("vote-uniqueness", violations, new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["votes"] = votes.Count,
            ["grants-sent"] = h.Sends.Count(s => s.Message is RequestVoteResponse { VoteGranted: true }),
            ["denials-sent"] = h.Sends.Count(s => s.Message is RequestVoteResponse { VoteGranted: false }),
        });
    }

    /// <summary>Invariant 9, Term monotonicity: a node's durable term and the term of every message it sends never decrease, across restarts.</summary>
    public static InvariantResult TermMonotonicity(ElectionHistory h)
    {
        var violations = new List<string>();
        long advances = 0;
        var events = h.States.Select(s => (s.Seq, s.Time, s.Node, s.State.Term, What: "durable term"))
            .Concat(h.Sends.Select(s => (s.Seq, s.Time, Node: s.From, s.Message.Term, What: "sent " + s.Message.GetType().Name)));
        foreach (var node in events.GroupBy(e => e.Node))
        {
            var max = Term.Zero;
            foreach (var e in node.OrderBy(e => e.Seq))
            {
                if (e.Term < max)
                {
                    violations.Add(node.Key + " at " + N(e.Time) + ": " + e.What + " " + N(e.Term.Value) + " after term " + N(max.Value));
                }
                else if (e.Term > max)
                {
                    advances++;
                    max = e.Term;
                }
            }
        }

        return new("term-monotonicity", violations, new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["term-advances"] = advances,
            ["highest-term"] = h.States.Select(s => s.State.Term.Value).Concat(h.Sends.Select(s => s.Message.Term.Value)).DefaultIfEmpty(0).Max(),
        });
    }

    /// <summary>
    /// Invariant 11, phase 3's scope (decision 3): after the last fault heals at
    /// <paramref name="stableFrom"/>, with a majority up throughout, an elected node acts as leader
    /// within <paramref name="window"/>. An execution with no such suffix (shorter than
    /// <paramref name="minimumSuffix"/>, or no majority up) is counted, not passed.
    /// </summary>
    public static InvariantResult Liveness(ElectionHistory h, long stableFrom, long end, long window, long minimumSuffix)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var nodes = Enumerable.Range(1, h.ClusterSize).Select(i => new NodeId(i)).ToList();
        var checkpoints = h.Liveness.Select(l => l.Time).Where(t => t >= stableFrom && t <= end).Append(stableFrom).Distinct();
        if (end - stableFrom < minimumSuffix || checkpoints.Any(t => nodes.Count(n => h.IsUp(n, t)) < h.Quorum))
        {
            counts["no-stable-suffix"] = 1;
            return new("liveness", [], counts);
        }

        var elected = h.Elections();
        var first = h.Sends.Where(s => s.Time >= stableFrom && s.Message is AppendEntries a && a.Leader == s.From
                && elected.TryGetValue((s.Message.Term, s.From), out var at) && at <= s.Time)
            .Select(s => s.Time).DefaultIfEmpty(long.MaxValue).Min();
        counts["checked"] = 1;
        if (first == long.MaxValue || first - stableFrom > window)
        {
            return new("liveness", ["no elected leader acted within " + N(window) + " of the stable suffix starting at " + N(stableFrom)], counts);
        }

        counts["time-to-leader"] = first - stableFrom;
        return new("liveness", [], counts);
    }

    /// <summary>
    /// Invariant 11 under a fault that never heals (P3-06's constructed one-way partition): from
    /// <paramref name="from"/> to <paramref name="to"/>, no interval of <paramref name="window"/>
    /// passes without an elected leader acting, among nodes that are up. Counts the terms started in
    /// the stretch: a leader deposed again and again still passes the gap check, and shows up here.
    /// </summary>
    public static InvariantResult LeaderContinuity(ElectionHistory h, long from, long to, long window)
    {
        var elected = h.Elections();
        var acts = h.Sends.Where(s => s.Time >= from && s.Time <= to && s.Message is AppendEntries a && a.Leader == s.From
                && elected.TryGetValue((s.Message.Term, s.From), out var at) && at <= s.Time)
            .Select(s => s.Time).Order().ToList();
        var violations = new List<string>();
        var last = from;
        foreach (var t in acts.Append(to))
        {
            if (t - last > window)
            {
                violations.Add("no elected leader acted from " + N(last) + " to " + N(t));
            }

            last = Math.Max(last, t);
        }

        return new("leader-continuity", violations, new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["terms-started"] = h.Sends.Where(s => s.Time >= from && s.Time <= to && s.Message is RequestVote).Select(s => s.Message.Term).Distinct().Count(),
            ["leaders-elected"] = elected.Count(kv => kv.Value >= from && kv.Value <= to),
        });
    }
}
