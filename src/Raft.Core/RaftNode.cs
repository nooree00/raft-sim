using System;
using System.Collections.Generic;
using System.Globalization;

namespace Raft.Core;

/// <summary>
/// Timing and limits, in the node's own ticks (P3 decision 5): election timeout uniform in
/// [min, max), heartbeats every interval, at most <see cref="MaxEntriesPerAppend"/> entries in one
/// AppendEntries, commands of at most <see cref="MaxCommandBytes"/>. <see cref="DisruptionRule"/> is on
/// in every real configuration; it can be turned off only so that P3-06 and P4-06 can measure what
/// the rule prevents. A node refuses options outside the bounds <see cref="Refusal"/> states (P4-09).
/// </summary>
public sealed record RaftOptions(long ElectionTimeoutMin = 150, long ElectionTimeoutMax = 300, long HeartbeatInterval = 50, bool DisruptionRule = true, int MaxEntriesPerAppend = 64, int MaxCommandBytes = 1_048_576)
{
    /// <summary>
    /// Heartbeats that fit in the shortest election timeout, at least: with three, one lost heartbeat
    /// never starts an election (P4-09: at 149 against 150, 57 of 100 fault-free runs elected again).
    /// </summary>
    public const int HeartbeatsPerTimeout = 3;

    /// <summary>An encoded AppendEntries: its fixed fields, then per entry a term and a length.</summary>
    private const long AppendEntriesFixed = 41, PerEntry = 12;

    public static RaftOptions Default { get; } = new();

    /// <summary>The largest command for which a full batch of <paramref name="batch"/> still encodes into one array.</summary>
    public static int LargestCommandFor(int batch) => (int)(((int.MaxValue - AppendEntriesFixed) / Math.Max(1, batch)) - PerEntry);

    /// <summary>
    /// Why these options are refused, or null. The election timeout's spread must be at least one
    /// heartbeat (at a spread of one tick every node times out together and no leader is ever
    /// elected, P4-09), and a full batch of the largest commands must encode.
    /// </summary>
    public string? Refusal() =>
        ElectionTimeoutMin < 1 ? "ElectionTimeoutMin is below 1"
        : HeartbeatInterval < 1 ? "HeartbeatInterval is below 1"
        : HeartbeatInterval * HeartbeatsPerTimeout > ElectionTimeoutMin ? "HeartbeatInterval exceeds a third of ElectionTimeoutMin"
        : ElectionTimeoutMax - ElectionTimeoutMin < HeartbeatInterval ? "the election timeout's spread is less than one heartbeat"
        : MaxEntriesPerAppend < 1 ? "MaxEntriesPerAppend is below 1"
        : MaxCommandBytes < 1 || MaxCommandBytes > LargestCommandFor(MaxEntriesPerAppend) ? "MaxCommandBytes is outside 1 and the largest command a full batch can encode"
        : null;
}

/// <summary>A server's role (paper §5.1).</summary>
public enum Role
{
    Follower,
    Candidate,
    Leader,
}

/// <summary>
/// A Raft node, phase 4: leader election (paper §5.2) and log replication (§5.3, §5.4), with
/// `currentTerm` and `votedFor` in the term-and-vote file and the log in its own file (§8). A
/// deterministic, closed state machine: time arrives as ticks, messages and client requests as
/// inputs, randomness from the context. Every durable change is emitted *before* any message that
/// depends on it, and the persist barrier holds such a message until the change is durable: a
/// candidate's vote for itself, a follower's entries before its acknowledgement, a leader's own
/// entry before it is sent. The leader counts its own entry toward a majority through that barrier:
/// it persisted the entry before sending it, so any follower's acknowledgement of an index means the
/// leader's own copy of it is already durable (spec §8's "a leader counting its own entry").
/// </summary>
public sealed class RaftNode : INode
{
    private readonly NodeContext _context;
    private readonly RaftOptions _options;
    private readonly IStateMachine _stateMachine;
    private readonly LogStore _log;
    private readonly List<NodeId> _votes = [];
    private readonly Dictionary<NodeId, long> _nextIndex = [];
    private readonly Dictionary<NodeId, long> _matchIndex = [];
    private readonly Dictionary<long, long> _pending = [];
    private Term _term;
    private NodeId? _votedFor;
    private NodeId? _leaderHint;
    private bool _dirty;
    private long _sinceHeard;
    private long _timeout;
    private long _sinceHeartbeat;
    private bool _heardFromLeader;
    private PersistTruncate? _cutTornTail;
    private PersistTruncate? _cutTornLogTail;
    private long _sinceLeader;
    private long _commitIndex;
    private long _lastApplied;

    public RaftNode(NodeContext context, RaftOptions? options = null, IStateMachine? stateMachine = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _options = options ?? RaftOptions.Default;
        if (_options.Refusal() is { } refusal)
        {
            throw new ArgumentException("options refused: " + refusal, nameof(options));
        }

        _stateMachine = stateMachine ?? new NoStateMachine();
        var file = context.Files.TryGetValue(TermVoteLog.FileName, out var f) ? f.ToArray() : null;
        var recovery = TermVoteLog.Recover(file);
        if (recovery.Path == RecoveryPath.Refused)
        {
            throw new InvalidOperationException("refusing to start: " + recovery.Detail);
        }

        // Recovery reads past a torn tail; the file must lose it before anything is appended after it,
        // or the next recovery meets the torn record in the middle and refuses (found by the P3-08 soak).
        if (recovery.Path == RecoveryPath.TruncatedTornTail)
        {
            _cutTornTail = new PersistTruncate(TermVoteLog.FileName, recovery.ValidLength);
        }

        _log = new LogStore(EntryLog.Recover(context.Files.TryGetValue(EntryLog.FileName, out var l) ? l.ToArray() : null));
        _cutTornLogTail = _log.CutTornTail;
        _term = recovery.State.Term;
        _votedFor = recovery.State.VotedFor;
        _timeout = NextTimeout();
    }

    public Role Role { get; private set; }

    public IReadOnlyList<Effect> Handle(Input input)
    {
        var effects = new List<Effect>();
        if (_cutTornTail is not null)
        {
            effects.Add(_cutTornTail);
            _cutTornTail = null;
        }

        if (_cutTornLogTail is not null)
        {
            effects.Add(_cutTornLogTail);
            _cutTornLogTail = null;
        }

        switch (input)
        {
            case Tick t:
                OnTick(t.Elapsed, effects);
                break;
            case Receive r when MessageCodec.Decode(r.Payload.ToArray()) is { } m:
                OnMessage(r.From, m, effects);
                break;
            case Receive r:
                effects.Add(Event("undecodable", new Field("from", r.From.ToString())));
                break;
            case ClientRequest c:
                OnClientRequest(c, effects);
                break;
        }

        Save(effects);
        return effects;
    }

    private void OnTick(long elapsed, List<Effect> effects)
    {
        _sinceLeader += elapsed;
        if (Role == Role.Leader)
        {
            _sinceHeartbeat += elapsed;
            if (_sinceHeartbeat >= _options.HeartbeatInterval)
            {
                Heartbeats(effects);
            }

            return;
        }

        _sinceHeard += elapsed;
        if (_sinceHeard >= _timeout)
        {
            Stand(effects);
        }
    }

    /// <summary>Become a candidate: a new term, a vote for itself persisted before the requests leave.</summary>
    private void Stand(List<Effect> effects)
    {
        Role = Role.Candidate;
        SetTerm(_term.Next());
        _votedFor = _context.Id;
        _dirty = true;
        _votes.Add(_context.Id);
        ResetElectionTimer();
        effects.Add(Event("candidate", new Field("term", N(_term.Value))));
        Save(effects);
        if (Won())
        {
            BecomeLeader(effects);
            return;
        }

        foreach (var peer in _context.Peers)
        {
            effects.Add(new Send(peer, MessageCodec.Encode(new RequestVote(_term, _context.Id, _log.LastIndex, _log.LastTerm))));
        }
    }

    private void OnMessage(NodeId from, Message m, List<Effect> effects)
    {
        // Paper §6: a server that believes a current leader exists, having heard from one within the
        // minimum election timeout (or being the leader), disregards RequestVote: it neither adopts the
        // term nor answers. A node that cannot hear the leader then cannot depose it by standing.
        if (m is RequestVote && _options.DisruptionRule && (Role == Role.Leader || (_heardFromLeader && _sinceLeader < _options.ElectionTimeoutMin)))
        {
            effects.Add(Event("requestvote-ignored", new Field("from", from.ToString())));
            return;
        }

        if (m.Term > _term)
        {
            SetTerm(m.Term);
            _votedFor = null;
            _dirty = true;
            if (Role != Role.Follower)
            {
                effects.Add(Event(Role == Role.Leader ? "leader-steps-down" : "candidate-steps-down", new Field("term", N(_term.Value))));
            }

            Role = Role.Follower;
        }

        switch (m)
        {
            case RequestVote rv:
                // §5.4.1, the election restriction: only a candidate whose log is at least as up to
                // date as the voter's, by last term and then by length, can have every committed entry.
                var upToDate = rv.LastLogTerm > _log.LastTerm || (rv.LastLogTerm == _log.LastTerm && rv.LastLogIndex >= _log.LastIndex);
                var grant = rv.Term == _term && (_votedFor is null || _votedFor == rv.Candidate) && upToDate;
                if (grant)
                {
                    _votedFor = rv.Candidate;
                    _dirty = true;
                    ResetElectionTimer();
                }

                Reply(from, new RequestVoteResponse(_term, grant), effects);
                break;
            case RequestVoteResponse rvr:
                if (Role == Role.Candidate && rvr.Term == _term && rvr.VoteGranted)
                {
                    if (!_votes.Contains(from))
                    {
                        _votes.Add(from);
                    }

                    if (Won())
                    {
                        BecomeLeader(effects);
                    }
                }

                break;
            case AppendEntries ae:
                OnAppendEntries(from, ae, effects);
                break;
            case AppendEntriesResponse aer:
                OnAppendEntriesResponse(from, aer, effects);
                break;
        }
    }

    private void OnAppendEntries(NodeId from, AppendEntries ae, List<Effect> effects)
    {
        if (ae.Term < _term)
        {
            Reply(from, new AppendEntriesResponse(_term, false, 0), effects);
            return;
        }

        if (Role == Role.Candidate)
        {
            effects.Add(Event("candidate-steps-down", new Field("term", N(_term.Value))));
            Role = Role.Follower;
        }

        ResetElectionTimer();
        _heardFromLeader = true;
        _sinceLeader = 0;
        _leaderHint = ae.Leader;

        // The consistency check (§5.3): the entry before the new ones must be the leader's. A
        // rejection says how far this log could match: its last index when it is shorter, otherwise
        // the index before the one that failed. The leader jumps there instead of stepping back one
        // entry per rejection, which with heartbeats alone takes one step per interval (P4-03).
        if (ae.PrevLogIndex > _log.LastIndex || _log.TermAt(ae.PrevLogIndex) != ae.PrevLogTerm)
        {
            Reply(from, new AppendEntriesResponse(_term, false, Math.Min(_log.LastIndex, ae.PrevLogIndex - 1)), effects);
            return;
        }

        // A new term is persisted before the log changes it authorises: a deposed leader truncating its
        // suffix at the new leader's word acts on that term (found by the log checkers, P4-03: the
        // truncation was issued before the term that deposed it).
        Save(effects);

        // Truncate only at a real conflict: an entry already held with the same term is the same entry
        // (Log Matching), and a stale AppendEntries carrying a prefix must not cut what follows it.
        for (var k = 0; k < ae.Entries.Count; k++)
        {
            var index = ae.PrevLogIndex + 1 + k;
            if (index <= _log.LastIndex && _log.TermAt(index) == ae.Entries[k].Term)
            {
                continue;
            }

            if (_log.TruncateFrom(index) is { } cut)
            {
                effects.Add(cut);
            }

            var rest = new List<LogEntry>();
            for (var j = k; j < ae.Entries.Count; j++)
            {
                rest.Add(ae.Entries[j]);
            }

            effects.Add(_log.Append(rest));
            break;
        }

        var lastNew = ae.PrevLogIndex + ae.Entries.Count;
        if (ae.LeaderCommit > _commitIndex)
        {
            _commitIndex = Math.Min(ae.LeaderCommit, lastNew);
            Apply(effects);
        }

        Reply(from, new AppendEntriesResponse(_term, true, lastNew), effects);
    }

    private void OnAppendEntriesResponse(NodeId from, AppendEntriesResponse r, List<Effect> effects)
    {
        if (Role != Role.Leader || r.Term != _term || !_nextIndex.ContainsKey(from))
        {
            return;
        }

        if (r.Success)
        {
            _matchIndex[from] = Math.Max(_matchIndex[from], r.MatchIndex);
            _nextIndex[from] = _matchIndex[from] + 1;
            AdvanceCommit(effects);
            if (_nextIndex[from] <= _log.LastIndex)
            {
                SendAppend(from, effects);
            }

            return;
        }

        // Back off to just past where the follower said its log could match (at least one entry), and
        // retry at once; never below what the follower is known to match. P4-03 predicted that one
        // entry per rejection was fast enough; measured, it was not (the phase report).
        _nextIndex[from] = Math.Max(_matchIndex[from] + 1, Math.Min(_nextIndex[from] - 1, r.MatchIndex + 1));
        SendAppend(from, effects);
    }

    /// <summary>Figure 2: the highest N held by a majority whose entry is of the current term; older entries commit with it.</summary>
    private void AdvanceCommit(List<Effect> effects)
    {
        for (var n = _log.LastIndex; n > _commitIndex; n--)
        {
            if (_log.TermAt(n) != _term)
            {
                break;
            }

            var count = 1;
            foreach (var peer in _context.Peers)
            {
                if (_matchIndex[peer] >= n)
                {
                    count++;
                }
            }

            if (count >= Quorum())
            {
                _commitIndex = n;
                Apply(effects);
                return;
            }
        }
    }

    private void OnClientRequest(ClientRequest c, List<Effect> effects)
    {
        if (Role != Role.Leader)
        {
            var hint = Role == Role.Follower && _leaderHint is { } h ? h.ToString() : "-";
            effects.Add(new ClientResponse(c.RequestId, Ascii("redirect|" + hint)));
            return;
        }

        if (c.Payload.Length > _options.MaxCommandBytes)
        {
            effects.Add(new ClientResponse(c.RequestId, Ascii("too-large|" + _options.MaxCommandBytes.ToString(CultureInfo.InvariantCulture))));
            return;
        }

        Save(effects);
        effects.Add(_log.Append(new List<LogEntry> { new(_term, c.Payload.ToArray()) }));
        _pending[_log.LastIndex] = c.RequestId;
        foreach (var peer in _context.Peers)
        {
            SendAppend(peer, effects);
        }
    }

    /// <summary>Apply committed entries in order; the leader answers the client whose entry it applied.</summary>
    private void Apply(List<Effect> effects)
    {
        while (_lastApplied < _commitIndex)
        {
            _lastApplied++;
            var entry = _log.At(_lastApplied);
            var result = entry.Command.Length == 0 ? ReadOnlyMemory<byte>.Empty : _stateMachine.Apply(entry.Command);
            effects.Add(Event("apply", new Field("index", N(_lastApplied))));
            if (Role == Role.Leader && _pending.Remove(_lastApplied, out var request))
            {
                effects.Add(new ClientResponse(request, result));
            }
        }
    }

    private bool Won() => _votes.Count >= Quorum();

    private int Quorum() => ((_context.Peers.Count + 1) / 2) + 1;

    /// <summary>
    /// Lead: every follower is assumed to match nothing and to need everything after the last entry.
    /// An empty heartbeat leaves at once, then the term's no-op (P4 decision 6) is appended and sent.
    /// The heartbeat goes first because the barrier holds every send issued after a persist: a leader
    /// that persisted its no-op first stayed silent for as long as its disk was slow, past the
    /// followers' election timeouts (found by the soak sample, P4-03).
    /// </summary>
    private void BecomeLeader(List<Effect> effects)
    {
        Role = Role.Leader;
        _leaderHint = _context.Id;
        effects.Add(Event("leader", new Field("term", N(_term.Value))));
        foreach (var peer in _context.Peers)
        {
            _nextIndex[peer] = _log.LastIndex + 1;
            _matchIndex[peer] = 0;
        }

        Heartbeats(effects);
        effects.Add(_log.Append(new List<LogEntry> { new(_term, []) }));
        foreach (var peer in _context.Peers)
        {
            SendAppend(peer, effects);
        }
    }

    private void Heartbeats(List<Effect> effects)
    {
        _sinceHeartbeat = 0;
        Save(effects);
        foreach (var peer in _context.Peers)
        {
            SendAppend(peer, effects);
        }
    }

    private void SendAppend(NodeId peer, List<Effect> effects)
    {
        var next = _nextIndex[peer];
        var prev = next - 1;
        var ae = new AppendEntries(_term, _context.Id, prev, _log.TermAt(prev), _log.From(next, _options.MaxEntriesPerAppend), _commitIndex);
        effects.Add(new Send(peer, MessageCodec.Encode(ae)));
    }

    /// <summary>A reply leaves only after any change to the term or vote it depends on is persisted.</summary>
    private void Reply(NodeId to, Message m, List<Effect> effects)
    {
        Save(effects);
        effects.Add(new Send(to, MessageCodec.Encode(m)));
    }

    private void Save(List<Effect> effects)
    {
        if (_dirty)
        {
            effects.Add(new PersistAppend(TermVoteLog.FileName, TermVoteLog.Record(_term, _votedFor)));
            _dirty = false;
        }
    }

    /// <summary>A new term: the votes gathered for the old one no longer count, and neither does its leader or its clients.</summary>
    private void SetTerm(Term term)
    {
        _term = term;
        _votes.Clear();
        _leaderHint = null;
        _nextIndex.Clear();
        _matchIndex.Clear();
        _pending.Clear();
    }

    private void ResetElectionTimer()
    {
        _sinceHeard = 0;
        _timeout = NextTimeout();
    }

    private long NextTimeout() => _options.ElectionTimeoutMin + _context.Random.NextLong(_options.ElectionTimeoutMax - _options.ElectionTimeoutMin);

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static byte[] Ascii(string s)
    {
        var b = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            b[i] = (byte)s[i];
        }

        return b;
    }

    private static Emit Event(string name, Field field) => new(name, new List<Field> { field });

    /// <summary>For runs with no clients: commands are applied as nothing.</summary>
    private sealed class NoStateMachine : IStateMachine
    {
        public ReadOnlyMemory<byte> Apply(ReadOnlyMemory<byte> command) => ReadOnlyMemory<byte>.Empty;

        public ReadOnlyMemory<byte> Snapshot() => ReadOnlyMemory<byte>.Empty;

        public void Restore(ReadOnlyMemory<byte> snapshot)
        {
        }
    }
}
