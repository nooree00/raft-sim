using System;
using System.Collections.Generic;
using System.Globalization;

namespace Raft.Core;

/// <summary>Timing, in the node's own ticks (P3 decision 5): election timeout uniform in [min, max), heartbeats every interval.</summary>
public sealed record RaftOptions(long ElectionTimeoutMin = 150, long ElectionTimeoutMax = 300, long HeartbeatInterval = 50)
{
    public static RaftOptions Default { get; } = new();
}

/// <summary>A server's role (paper §5.1).</summary>
public enum Role
{
    Follower,
    Candidate,
    Leader,
}

/// <summary>
/// A Raft node, phase 3: leader election (paper §5.2) with `currentTerm` and `votedFor` persisted
/// (§8), logs still empty. A deterministic, closed state machine: time arrives as ticks, messages as
/// inputs, randomness from the context. Every change to the term or the vote is appended to the
/// term-and-vote file *before* any message that depends on it, including a candidate's vote for
/// itself, which is sent, not a response (spec §8's two-leader example).
/// </summary>
public sealed class RaftNode : INode
{
    private readonly NodeContext _context;
    private readonly RaftOptions _options;
    private readonly List<NodeId> _votes = [];
    private Term _term;
    private NodeId? _votedFor;
    private bool _dirty;
    private long _sinceHeard;
    private long _timeout;
    private long _sinceHeartbeat;

    public RaftNode(NodeContext context, RaftOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _options = options ?? RaftOptions.Default;
        var file = context.Files.TryGetValue(TermVoteLog.FileName, out var f) ? f.ToArray() : null;
        var recovery = TermVoteLog.Recover(file);
        if (recovery.Path == RecoveryPath.Refused)
        {
            throw new InvalidOperationException("refusing to start: " + recovery.Detail);
        }

        _term = recovery.State.Term;
        _votedFor = recovery.State.VotedFor;
        _timeout = NextTimeout();
    }

    public Role Role { get; private set; }

    public IReadOnlyList<Effect> Handle(Input input)
    {
        var effects = new List<Effect>();
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
        }

        Save(effects);
        return effects;
    }

    private void OnTick(long elapsed, List<Effect> effects)
    {
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
            effects.Add(new Send(peer, MessageCodec.Encode(new RequestVote(_term, _context.Id, 0, Term.Zero))));
        }
    }

    private void OnMessage(NodeId from, Message m, List<Effect> effects)
    {
        if (m.Term > _term)
        {
            SetTerm(m.Term);
            _votedFor = null;
            _dirty = true;
            if (Role != Role.Follower)
            {
                effects.Add(Event("step-down", new Field("term", N(_term.Value))));
            }

            Role = Role.Follower;
        }

        switch (m)
        {
            case RequestVote rv:
                var grant = rv.Term == _term && (_votedFor is null || _votedFor == rv.Candidate);
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
                if (ae.Term < _term)
                {
                    Reply(from, new AppendEntriesResponse(_term, false, 0), effects);
                    break;
                }

                if (Role == Role.Candidate)
                {
                    effects.Add(Event("step-down", new Field("term", N(_term.Value))));
                    Role = Role.Follower;
                }

                ResetElectionTimer();
                Reply(from, new AppendEntriesResponse(_term, true, 0), effects);
                break;
            case AppendEntriesResponse:
                break;
        }
    }

    private bool Won() => _votes.Count >= ((_context.Peers.Count + 1) / 2) + 1;

    private void BecomeLeader(List<Effect> effects)
    {
        Role = Role.Leader;
        effects.Add(Event("leader", new Field("term", N(_term.Value))));
        Heartbeats(effects);
    }

    private void Heartbeats(List<Effect> effects)
    {
        _sinceHeartbeat = 0;
        Save(effects);
        foreach (var peer in _context.Peers)
        {
            effects.Add(new Send(peer, MessageCodec.Encode(new AppendEntries(_term, _context.Id, 0, Term.Zero, [], 0))));
        }
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

    /// <summary>A new term: the votes gathered for the old one no longer count.</summary>
    private void SetTerm(Term term)
    {
        _term = term;
        _votes.Clear();
    }

    private void ResetElectionTimer()
    {
        _sinceHeard = 0;
        _timeout = NextTimeout();
    }

    private long NextTimeout() => _options.ElectionTimeoutMin + _context.Random.NextLong(_options.ElectionTimeoutMax - _options.ElectionTimeoutMin);

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static Emit Event(string name, Field field) => new(name, new List<Field> { field });
}
