using System;
using System.Collections.Generic;
using System.Globalization;

namespace Raft.Core;

/// <summary>
/// Timing and limits, in the node's own ticks (P3 decision 5): election timeout uniform in
/// [min, max), heartbeats every interval, at most <see cref="MaxEntriesPerAppend"/> entries in one
/// AppendEntries, commands of at most <see cref="MaxCommandBytes"/>, a compaction once
/// <see cref="SnapshotThreshold"/> applied entries follow the last snapshot (phase 7 decision 3),
/// sent to a follower behind it in chunks of at most <see cref="SnapshotChunkBytes"/> (decision 4).
/// <see cref="CompactPastCommit"/> is off in every real configuration: it exists only for P7-10's
/// positive control, a node that compacts entries no quorum has committed.
/// <see cref="DisruptionRule"/> is on
/// in every real configuration; it can be turned off only so that P3-06 and P4-06 can measure what
/// the rule prevents. A node refuses options outside the bounds <see cref="Refusal"/> states (P4-09).
/// </summary>
public sealed record RaftOptions(long ElectionTimeoutMin = 150, long ElectionTimeoutMax = 300, long HeartbeatInterval = 50, bool DisruptionRule = true, int MaxEntriesPerAppend = 64, int MaxCommandBytes = 1_048_576, int SnapshotThreshold = 1_000, int SnapshotChunkBytes = 65_536, bool CompactPastCommit = false, bool ReadsWithoutQuorum = false, bool AnswerAtAppend = false)
{
    /// <summary>
    /// Heartbeats that fit in the shortest election timeout, at least: with three, one lost heartbeat
    /// never starts an election (P4-09: at 149 against 150, 57 of 100 fault-free runs elected again).
    /// </summary>
    public const int HeartbeatsPerTimeout = 3;

    /// <summary>An encoded AppendEntries: its fixed fields, then per entry a term and a length.</summary>
    private const long AppendEntriesFixed = 49, PerEntry = 12;

    public static RaftOptions Default { get; } = new();

    /// <summary>The largest command for which a full batch of <paramref name="batch"/> still encodes into one array.</summary>
    public static int LargestCommandFor(int batch) => (int)(((int.MaxValue - AppendEntriesFixed) / Math.Max(1, batch)) - PerEntry);

    /// <summary>
    /// The largest threshold for which the entries a compaction waits for, each of the largest
    /// command, still fit the log file, which recovery reads into one array (P7-06). Entries past
    /// them that are not yet applied are not bounded by it.
    /// </summary>
    public static int LargestThresholdFor(int command) => Array.MaxLength / (Math.Max(1, command) + EntryLog.RecordOverhead);

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
        : SnapshotThreshold < 1 || SnapshotThreshold > LargestThresholdFor(MaxCommandBytes) ? "SnapshotThreshold is outside 1 and the largest threshold whose entries fit the log file"
        : SnapshotChunkBytes < 1 || SnapshotChunkBytes > LargestCommandFor(1) ? "SnapshotChunkBytes is outside 1 and the largest chunk one message can encode"
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

    // P11-02: per follower, the last index the outstanding append with entries carried. While one is
    // outstanding a write sends that follower nothing; the answer that reaches it sends the next
    // batch. Before, every write and every answer sent every unacknowledged entry again, and each
    // answer's send drew an answer that sent again: phase 10 measured about 200 messages per committed
    // write past the knee, and P11-01 1,629 entries per committed entry at 32 in flight.
    private readonly Dictionary<NodeId, long> _inFlight = [];
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
    private readonly Configuration _initial;
    private long? _membershipRequest;
    private Configuration? _catchingUp;
    private long _restored;
    private readonly Dictionary<NodeId, Sent> _snapshotSent = [];
    private Incoming? _incoming;
    private readonly List<Read> _reads = [];
    private readonly Dictionary<NodeId, long> _acked = [];
    private long _round;
    private bool _termCommitted;

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
        _log.AvoidNames(context.Files.Keys);
        _cutTornLogTail = _log.CutTornTail;

        // A snapshot covers committed, applied entries: the state machine starts from it (P7-06).
        if (_log.Snapshot is { } snapshot)
        {
            _stateMachine.Restore(snapshot.State);
            _lastApplied = _commitIndex = _restored = snapshot.Index;
        }

        _term = recovery.State.Term;
        _votedFor = recovery.State.VotedFor;
        _timeout = NextTimeout();
        var members = new List<NodeId>();
        if (context.Members is { } initial)
        {
            members.AddRange(initial);
        }
        else
        {
            members.Add(context.Id);
            members.AddRange(context.Peers);
        }

        _initial = new Configuration(members);
    }

    public Role Role { get; private set; }

    /// <summary>
    /// The configuration in effect: the latest configuration entry in the log, else the initial one
    /// (P6 decisions 1, 2). Read from the log, never cached beside it: a truncation that removes a
    /// configuration entry must take its effect with it, and the log is the one place that knows
    /// (P6-03's prediction: a value cached on append survived the truncation). The log keeps the
    /// indices of its configuration entries (LogStore), so this costs no scan.
    /// </summary>
    public Configuration Configuration => ConfigurationAt(_log.LastIndex);

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

        if (_restored > 0)
        {
            effects.Add(Event("restore", new Field("index", N(_restored))));
            _restored = 0;
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

        ServeReads(effects);
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

    /// <summary>
    /// Become a candidate: a new term, a vote for itself persisted before the requests leave. A node
    /// outside its own configuration in effect never stands (P6-05): a new server before a
    /// configuration includes it, and a removed one once it holds the configuration that removed it.
    /// </summary>
    private void Stand(List<Effect> effects)
    {
        if (!Contains(Configuration.Members, _context.Id))
        {
            ResetElectionTimer();
            return;
        }

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

        var peers = Peers();
        for (var k = 0; k < peers.Count; k++)
        {
            var peer = peers[k];
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
            case InstallSnapshot ins:
                OnInstallSnapshot(from, ins, effects);
                break;
            case InstallSnapshotResponse isr:
                OnInstallSnapshotResponse(from, isr, effects);
                break;
        }
    }

    /// <summary>How far a follower holds the snapshot of an index, by its last reply (P7-07).</summary>
    private sealed record Sent(long Index, long Offset);

    /// <summary>A snapshot's record, built once.</summary>
    private sealed record Built(long Index, byte[] Bytes);

    /// <summary>A snapshot arriving in chunks (P7-07): its bytes so far, the file they are written to, and how many from its start are held.</summary>
    private sealed class Incoming(long index, Term term, string file)
    {
        public long Index { get; } = index;

        public Term Term { get; } = term;

        public string File { get; } = file;

        public List<byte> Bytes { get; } = [];
    }

    /// <summary>
    /// A chunk of the leader's snapshot (Figure 13, decision 4). Chunks are written at their offsets
    /// to a file of their own; one that leaves a gap is dropped and the reply says where the held
    /// bytes end, so a lost chunk is sent again and a duplicate rewrites what is there. On the last,
    /// the record must read back whole as the snapshot named, or it is discarded; then it is
    /// installed: the state machine restored from it, the log kept after it if the log holds its
    /// last entry, else discarded, and the file renamed over the log.
    /// </summary>
    private void OnInstallSnapshot(NodeId from, InstallSnapshot m, List<Effect> effects)
    {
        if (m.Term < _term)
        {
            Reply(from, new InstallSnapshotResponse(_term, m.LastIncludedIndex, 0, false), effects);
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
        _leaderHint = m.Leader;
        if (m.LastIncludedIndex <= _log.BaseIndex)
        {
            Reply(from, new InstallSnapshotResponse(_term, m.LastIncludedIndex, 0, true), effects);
            return;
        }

        // Chunks belong to the snapshot of their index and term (P7-07): keyed by the follower alone, a
        // delayed chunk of an earlier snapshot was written into a newer one's file, and a repeated
        // first chunk started the transfer again in a new file.
        if (m.Offset == 0 && (_incoming is null || _incoming.Index != m.LastIncludedIndex || _incoming.Term != m.LastIncludedTerm))
        {
            _incoming = new Incoming(m.LastIncludedIndex, m.LastIncludedTerm, _log.NewFileName());
        }

        var incoming = _incoming is { } i && i.Index == m.LastIncludedIndex && i.Term == m.LastIncludedTerm ? i : null;
        if (incoming is null || m.Offset > incoming.Bytes.Count)
        {
            Reply(from, new InstallSnapshotResponse(_term, m.LastIncludedIndex, incoming?.Bytes.Count ?? 0, false), effects);
            return;
        }

        Save(effects);
        effects.Add(new PersistWriteAt(incoming.File, m.Offset, m.Data));
        for (var k = 0; k < m.Data.Length; k++)
        {
            var at = (int)m.Offset + k;
            if (at < incoming.Bytes.Count)
            {
                incoming.Bytes[at] = m.Data[k];
            }
            else
            {
                incoming.Bytes.Add(m.Data[k]);
            }
        }

        if (!m.Done || m.Offset + m.Data.Length != incoming.Bytes.Count)
        {
            Reply(from, new InstallSnapshotResponse(_term, m.LastIncludedIndex, incoming.Bytes.Count, false), effects);
            return;
        }

        _incoming = null;
        var bytes = incoming.Bytes.ToArray();
        var read = EntryLog.Recover(bytes);
        if (read is not { Path: RecoveryPath.Clean, Snapshot: { } snapshot } || snapshot.Index != m.LastIncludedIndex || snapshot.Term != m.LastIncludedTerm || read.ValidLength != bytes.Length || read.Entries.Count != 0)
        {
            effects.Add(Event("snapshot-discarded", new Field("index", N(m.LastIncludedIndex))));
            Reply(from, new InstallSnapshotResponse(_term, m.LastIncludedIndex, 0, false), effects);
            return;
        }

        effects.AddRange(_log.Install(snapshot, incoming.File, bytes.Length));
        _stateMachine.Restore(snapshot.State);
        _commitIndex = Math.Max(_commitIndex, snapshot.Index);
        _lastApplied = snapshot.Index;
        effects.Add(Event("restore", new Field("index", N(snapshot.Index))));
        Apply(effects);
        Reply(from, new InstallSnapshotResponse(_term, m.LastIncludedIndex, bytes.Length, true), effects);
    }

    /// <summary>The follower holds the snapshot, or the bytes it reports: the next chunk, or AppendEntries after the snapshot.</summary>
    private void OnInstallSnapshotResponse(NodeId from, InstallSnapshotResponse r, List<Effect> effects)
    {
        if (Role != Role.Leader || r.Term != _term || !_nextIndex.TryGetValue(from, out var next))
        {
            return;
        }

        if (r.Done)
        {
            _snapshotSent.Remove(from);
            _inFlight.Remove(from);
            _matchIndex[from] = Math.Max(_matchIndex[from], r.LastIncludedIndex);
            _nextIndex[from] = Math.Max(next, r.LastIncludedIndex + 1);
            AdvanceCommit(effects);
            ProceedIfCaughtUp(effects);
            SendAppend(from, effects);
            return;
        }

        if (r.LastIncludedIndex == _log.BaseIndex)
        {
            _snapshotSent[from] = new Sent(r.LastIncludedIndex, r.Received);
            SendSnapshot(from, effects);
        }
    }

    private void OnAppendEntries(NodeId from, AppendEntries ae, List<Effect> effects)
    {
        if (ae.Term < _term)
        {
            Reply(from, new AppendEntriesResponse(_term, false, 0, ae.Round), effects);
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

        // Entries at or below the snapshot are committed, so this term's leader holds the same ones
        // (Leader Completeness): those are skipped, and the check moves to the snapshot's index, where
        // the term is the snapshot's (P7-06).
        if (ae.PrevLogIndex < _log.BaseIndex)
        {
            var covered = (int)Math.Min(ae.Entries.Count, _log.BaseIndex - ae.PrevLogIndex);
            var after = new List<LogEntry>();
            for (var k = covered; k < ae.Entries.Count; k++)
            {
                after.Add(ae.Entries[k]);
            }

            ae = ae with { PrevLogIndex = ae.PrevLogIndex + covered, PrevLogTerm = covered == 0 ? ae.PrevLogTerm : ae.Entries[covered - 1].Term, Entries = after };
            if (ae.PrevLogIndex < _log.BaseIndex)
            {
                Reply(from, new AppendEntriesResponse(_term, true, ae.PrevLogIndex, ae.Round), effects);
                return;
            }
        }

        // The consistency check (§5.3): the entry before the new ones must be the leader's. A
        // rejection says how far this log could match: its last index when it is shorter, otherwise
        // the index before the one that failed. The leader jumps there instead of stepping back one
        // entry per rejection, which with heartbeats alone takes one step per interval (P4-03).
        if (ae.PrevLogIndex > _log.LastIndex || _log.TermAt(ae.PrevLogIndex) != ae.PrevLogTerm)
        {
            Reply(from, new AppendEntriesResponse(_term, false, Math.Max(_log.BaseIndex, Math.Min(_log.LastIndex, ae.PrevLogIndex - 1)), ae.Round), effects);
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

        // The commit index never falls (P7-06): an AppendEntries carrying a prefix once lowered it, and
        // a compaction relies on it staying at or above the snapshot's index.
        var lastNew = ae.PrevLogIndex + ae.Entries.Count;
        if (ae.LeaderCommit > _commitIndex && Math.Min(ae.LeaderCommit, lastNew) > _commitIndex)
        {
            _commitIndex = Math.Min(ae.LeaderCommit, lastNew);
            Apply(effects);
        }

        Reply(from, new AppendEntriesResponse(_term, true, lastNew, ae.Round), effects);
    }

    private void OnAppendEntriesResponse(NodeId from, AppendEntriesResponse r, List<Effect> effects)
    {
        if (Role != Role.Leader || r.Term != _term || !_nextIndex.ContainsKey(from))
        {
            return;
        }

        // Any answer in this term, success or not, says the peer followed this leader when it handled
        // the request: the reads of that request's round, and every earlier one, have its vote (P8-05).
        if (!_acked.TryGetValue(from, out var acked) || r.Round > acked)
        {
            _acked[from] = r.Round;
        }

        if (r.Success)
        {
            _matchIndex[from] = Math.Max(_matchIndex[from], r.MatchIndex);
            _nextIndex[from] = _matchIndex[from] + 1;
            if (_inFlight.TryGetValue(from, out var carried) && _matchIndex[from] >= carried)
            {
                _inFlight.Remove(from);
            }

            AdvanceCommit(effects);
            ProceedIfCaughtUp(effects);
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
        _inFlight.Remove(from);
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

            // A majority of every configuration in effect at index n (P6-05; the checker's rule, P6-04):
            // the leader's own copy counts only where it is a member.
            var holders = new List<NodeId> { _context.Id };
            var peers = Peers();
            for (var k = 0; k < peers.Count; k++)
            {
                var peer = peers[k];
                if (_matchIndex.TryGetValue(peer, out var match) && match >= n)
                {
                    holders.Add(peer);
                }
            }

            if (ConfigurationAt(n).IsQuorum(holders))
            {
                _commitIndex = n;
                TermCommitted();
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

        if (Configuration.IsInternal(c.Payload.ToArray()))
        {
            effects.Add(new ClientResponse(c.RequestId, Ascii("reserved|")));
            return;
        }

        if (ParseMembership(c.Payload.ToArray()) is { } members)
        {
            OnMembershipRequest(c.RequestId, members, effects);
            return;
        }

        if (_stateMachine.IsQuery(c.Payload))
        {
            OnRead(c, effects);
            return;
        }

        if (c.Payload.Length > _options.MaxCommandBytes)
        {
            effects.Add(new ClientResponse(c.RequestId, Ascii("too-large|" + _options.MaxCommandBytes.ToString(CultureInfo.InvariantCulture))));
            return;
        }

        // The positive control (P9-07): a session's write other than a compare-and-swap is answered
        // when the leader appends it, before any follower holds it. Off in every real configuration.
        if (_options.AnswerAtAppend && IsPlainSessionWrite(c.Payload.ToArray()))
        {
            AnswerAtAppend(c, effects);
            return;
        }

        Save(effects);
        effects.Add(_log.Append(new List<LogEntry> { new(_term, c.Payload.ToArray()) }));
        _pending[_log.LastIndex] = c.RequestId;
        var peers = Peers();
        for (var k = 0; k < peers.Count; k++)
        {
            var peer = peers[k];
            SendAppend(peer, effects);
        }
    }

    /// <summary>The P9-07 control: the entry appended and sent, and its client answered now, not at commit.</summary>
    private void AnswerAtAppend(ClientRequest c, List<Effect> effects)
    {
        Save(effects);
        effects.Add(_log.Append(new List<LogEntry> { new(_term, c.Payload.ToArray()) }));
        effects.Add(new ClientResponse(c.RequestId, Ascii("ok")));
        var peers = Peers();
        for (var k = 0; k < peers.Count; k++)
        {
            SendAppend(peers[k], effects);
        }
    }

    /// <summary>`Session|…` whose command is not a `Cas`: what the P9-07 control answers at append, since its reply is "ok" whatever it applies to.</summary>
    private static bool IsPlainSessionWrite(byte[] payload) => At(payload, 0, "Session|") && !Contains(payload, "|Cas|");

    private static bool At(byte[] bytes, int offset, string text)
    {
        if (offset + text.Length > bytes.Length)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (bytes[offset + i] != text[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(byte[] bytes, string text)
    {
        for (var i = 0; i + text.Length <= bytes.Length; i++)
        {
            if (At(bytes, i, text))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A read (P8-05, phase 8 decision 4: ReadIndex). It joins a new round, which leaves at once in a
    /// heartbeat to every peer; only an answer to a request sent from now on carries this round or a
    /// later one. Its index is the commit index, once an entry of this term has committed: before
    /// that, the commit index can be behind entries a predecessor committed and answered.
    /// </summary>
    private void OnRead(ClientRequest c, List<Effect> effects)
    {
        // The positive control (P8-08): a leader that answers from its own state at once, as if it
        // could not have been deposed.
        if (_options.ReadsWithoutQuorum)
        {
            effects.Add(new ClientResponse(c.RequestId, _stateMachine.Query(c.Payload)));
            return;
        }

        _round++;
        _reads.Add(new Read(c.RequestId, c.Payload.ToArray(), _round) { Index = _termCommitted ? _commitIndex : -1 });
        Heartbeats(effects, SendMode.Probe);
    }

    /// <summary>An entry of this term has committed: the reads waiting for it take the commit index as theirs.</summary>
    private void TermCommitted()
    {
        if (_termCommitted)
        {
            return;
        }

        _termCommitted = true;
        for (var k = 0; k < _reads.Count; k++)
        {
            if (_reads[k].Index < 0)
            {
                _reads[k].Index = _commitIndex;
            }
        }
    }

    /// <summary>
    /// Answer every read whose index has been applied and whose round a quorum of the configuration in
    /// effect has acknowledged, the leader counting where it is a member; a joint configuration needs
    /// both majorities, as commitment does (P6-05). A node that is no longer leader answers none: its
    /// reads are dropped unanswered, and their clients time out.
    /// </summary>
    private void ServeReads(List<Effect> effects)
    {
        if (_reads.Count == 0)
        {
            return;
        }

        if (Role != Role.Leader)
        {
            _reads.Clear();
            return;
        }

        var configuration = Configuration;
        var peers = Peers();
        for (var k = 0; k < _reads.Count; k++)
        {
            var read = _reads[k];
            if (read.Index < 0 || read.Index > _lastApplied)
            {
                continue;
            }

            var holders = new List<NodeId> { _context.Id };
            for (var j = 0; j < peers.Count; j++)
            {
                if (_acked.TryGetValue(peers[j], out var acked) && acked >= read.Round)
                {
                    holders.Add(peers[j]);
                }
            }

            if (!configuration.IsQuorum(holders))
            {
                continue;
            }

            effects.Add(new ClientResponse(read.RequestId, _stateMachine.Query(read.Query)));
            _reads.RemoveAt(k);
            k--;
        }
    }

    /// <summary>A read waiting to be answered: its round, and its index (-1 until an entry of this term commits).</summary>
    private sealed class Read(long requestId, byte[] query, long round)
    {
        public long RequestId { get; } = requestId;

        public byte[] Query { get; } = query;

        public long Round { get; } = round;

        public long Index { get; set; }
    }

    /// <summary>
    /// A membership request (P6 decisions 4, 6): `Member|1,2,4`. One change at a time: while a joint
    /// configuration or an uncommitted configuration is in the log the answer is `busy|`, a definite
    /// failure. Otherwise `C_old,new` is appended; the leader appends `C_new` once it commits, and
    /// answers `ok` once that commits.
    /// </summary>
    private void OnMembershipRequest(long requestId, List<NodeId> members, List<Effect> effects)
    {
        var current = Configuration;
        if (current.IsJoint || LatestConfigurationIndex() > _commitIndex || _membershipRequest is not null)
        {
            effects.Add(new ClientResponse(requestId, Ascii("busy|")));
            return;
        }

        var target = new Configuration(members);
        if (target == current)
        {
            effects.Add(new ClientResponse(requestId, Ascii("ok")));
            return;
        }

        _membershipRequest = requestId;
        if (NewServers(current, target).Count == 0)
        {
            AppendConfiguration(new Configuration(current.Old, target.Old), effects);
            return;
        }

        // P6-06 (decision 3): the new servers first catch up as non-voting members; `C_old,new` is
        // appended once each is within one batch of the log, so a joint configuration whose new
        // majority needs them does not wait on a whole log's replication with commitment stalled.
        _catchingUp = target;
        EnsurePeers();
        var added = NewServers(current, target);
        for (var k = 0; k < added.Count; k++)
        {
            SendAppend(added[k], effects);
        }

        // A log shorter than one batch needs no catching up: the change proceeds at once.
        ProceedIfCaughtUp(effects);
    }

    /// <summary>The servers <paramref name="target"/> adds to <paramref name="current"/>.</summary>
    private static List<NodeId> NewServers(Configuration current, Configuration target)
    {
        var added = new List<NodeId>();
        foreach (var m in target.Old)
        {
            if (!Contains(current.Members, m))
            {
                added.Add(m);
            }
        }

        return added;
    }

    /// <summary>When every server being caught up is within one batch of the log, the change proceeds to `C_old,new`.</summary>
    private void ProceedIfCaughtUp(List<Effect> effects)
    {
        if (_catchingUp is not { } target)
        {
            return;
        }

        var current = Configuration;
        var added = NewServers(current, target);
        for (var k = 0; k < added.Count; k++)
        {
            if (!_matchIndex.TryGetValue(added[k], out var match) || match < _log.LastIndex - _options.MaxEntriesPerAppend)
            {
                return;
            }
        }

        _catchingUp = null;
        AppendConfiguration(new Configuration(current.Old, target.Old), effects);
    }

    private void AppendConfiguration(Configuration c, List<Effect> effects)
    {
        Save(effects);
        effects.Add(_log.Append(new List<LogEntry> { new(_term, c.Encode()) }));
        EnsurePeers();
        effects.Add(Event("configuration", new Field("index", N(_log.LastIndex))));
        var peers = Peers();
        for (var k = 0; k < peers.Count; k++)
        {
            var peer = peers[k];
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
            var result = entry.Command.Length == 0 || Configuration.IsInternal(entry.Command) ? ReadOnlyMemory<byte>.Empty : _stateMachine.Apply(_lastApplied, entry.Command);
            effects.Add(Event("apply", new Field("index", N(_lastApplied))));
            if (Role == Role.Leader && _pending.Remove(_lastApplied, out var request))
            {
                effects.Add(new ClientResponse(request, result));
            }

            if (Role == Role.Leader && Configuration.IsInternal(entry.Command) && Configuration.Decode(entry.Command) is { } applied && _lastApplied == LatestConfigurationIndex())
            {
                OnConfigurationCommitted(applied, effects);
            }
        }

        Compact(effects);
    }

    /// <summary>
    /// Phase 7 decision 3: once <see cref="RaftOptions.SnapshotThreshold"/> applied entries follow the
    /// snapshot, compact up to the last applied one, never further. The snapshot carries the
    /// configuration in effect at its index (null for the initial one), so a node restored from it
    /// does not revert to its initial configuration (register).
    /// </summary>
    private void Compact(List<Effect> effects)
    {
        if (_lastApplied - _log.BaseIndex < _options.SnapshotThreshold)
        {
            return;
        }

        // The positive control (P7-10) compacts the whole log and counts what it compacted as applied
        // and committed, without applying it: the wrong node, coherent enough to keep running.
        var index = _options.CompactPastCommit ? _log.LastIndex : _lastApplied;
        var at = _log.ConfigurationIndexAtOrBelow(index);
        var configuration = at == 0 ? _log.Snapshot?.Configuration : Configuration.Decode(_log.At(at).Command);
        effects.AddRange(_log.Compact(index, configuration, _stateMachine.Snapshot().ToArray()));
        effects.Add(Event("compact", new Field("index", N(index))));
        _lastApplied = index;
        _commitIndex = Math.Max(_commitIndex, index);
    }

    /// <summary>
    /// The latest configuration in the log has committed: `C_old,new` is followed by `C_new`; `C_new`
    /// answers the request, and a leader outside it steps down (P6 decision 5).
    /// </summary>
    private void OnConfigurationCommitted(Configuration applied, List<Effect> effects)
    {
        if (applied.IsJoint)
        {
            AppendConfiguration(new Configuration(applied.New!), effects);
            return;
        }

        if (_membershipRequest is { } request)
        {
            effects.Add(new ClientResponse(request, Ascii("ok")));
            _membershipRequest = null;
        }

        if (!Contains(applied.Members, _context.Id))
        {
            effects.Add(Event("leader-steps-down-removed", new Field("term", N(_term.Value))));
            Role = Role.Follower;
            _leaderHint = null;
        }
    }

    private bool Won() => Configuration.IsQuorum(_votes);

    /// <summary>The servers this node sends to: every member of the configuration in effect but itself (P6-05), and any server being caught up (P6-06).</summary>
    private List<NodeId> Peers()
    {
        var peers = new List<NodeId>();
        foreach (var m in Configuration.Members)
        {
            if (m != _context.Id)
            {
                peers.Add(m);
            }
        }

        // Servers being caught up (P6-06) are sent to, and never counted: no configuration names them yet.
        if (_catchingUp is { } target)
        {
            foreach (var m in target.Old)
            {
                if (m != _context.Id && !Contains(peers, m))
                {
                    peers.Add(m);
                }
            }
        }

        return peers;
    }

    /// <summary>A leader tracks every peer the configuration in effect adds: a new server needs everything after nothing.</summary>
    private void EnsurePeers()
    {
        var peers = Peers();
        for (var k = 0; k < peers.Count; k++)
        {
            var peer = peers[k];
            if (!_nextIndex.ContainsKey(peer))
            {
                _nextIndex[peer] = _log.LastIndex + 1;
                _matchIndex[peer] = 0;
            }
        }
    }

    /// <summary>
    /// The configuration in effect at an index: the latest configuration entry at or below it, else
    /// the snapshot's, else the initial one. The log keeps only the configuration entries after its
    /// snapshot (P7-06).
    /// </summary>
    private Configuration ConfigurationAt(long index)
    {
        var at = _log.ConfigurationIndexAtOrBelow(index);
        return at != 0 ? Configuration.Decode(_log.At(at).Command)! : _log.Snapshot?.Configuration ?? _initial;
    }

    /// <summary>The index of the latest configuration entry in the log, 0 when there is none.</summary>
    private long LatestConfigurationIndex() => _log.ConfigurationIndexAtOrBelow(_log.LastIndex);

    private static bool Contains(IReadOnlyList<NodeId> ids, NodeId id)
    {
        foreach (var x in ids)
        {
            if (x == id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>`Member|1,2,4` as the servers it names, or null when the command is not a membership request.</summary>
    private static List<NodeId>? ParseMembership(byte[] command)
    {
        const string Prefix = "Member|";
        if (command.Length <= Prefix.Length)
        {
            return null;
        }

        for (var i = 0; i < Prefix.Length; i++)
        {
            if (command[i] != Prefix[i])
            {
                return null;
            }
        }

        var ids = new List<NodeId>();
        var value = 0;
        var digits = 0;
        for (var i = Prefix.Length; i <= command.Length; i++)
        {
            if (i == command.Length || command[i] == (byte)',')
            {
                if (digits == 0)
                {
                    return null;
                }

                ids.Add(new NodeId(value));
                value = 0;
                digits = 0;
            }
            else if (command[i] >= (byte)'0' && command[i] <= (byte)'9' && digits < 9)
            {
                value = (value * 10) + (command[i] - (byte)'0');
                digits++;
            }
            else
            {
                return null;
            }
        }

        return ids;
    }

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
        _reads.Clear();
        _acked.Clear();
        _termCommitted = false;
        effects.Add(Event("leader", new Field("term", N(_term.Value))));
        EnsurePeers();
        Heartbeats(effects);
        effects.Add(_log.Append(new List<LogEntry> { new(_term, []) }));
        var peers = Peers();
        for (var k = 0; k < peers.Count; k++)
        {
            var peer = peers[k];
            SendAppend(peer, effects);
        }

        // A joint configuration its predecessor committed but did not follow with `C_new` (it lost
        // office in between) is finished by this leader, or the cluster stays joint for good.
        if (Configuration is { IsJoint: true } joint && LatestConfigurationIndex() <= _commitIndex)
        {
            AppendConfiguration(new Configuration(joint.New!), effects);
        }
    }

    /// <summary>
    /// How an append is sent (P11-02). <c>Replicate</c> (a write, an answer, a configuration, a new
    /// leader's no-op): nothing to a follower with an append outstanding. <c>Probe</c> (a read): to such
    /// a follower, an append without entries, carrying the read's round, so the read waits one round
    /// trip and the window is not sent again. <c>Resend</c> (the heartbeat timer): the outstanding
    /// window again, which recovers a lost append or answer within one interval.
    /// </summary>
    private enum SendMode
    {
        Replicate,
        Probe,
        Resend,
    }

    private void Heartbeats(List<Effect> effects, SendMode mode = SendMode.Resend)
    {
        _sinceHeartbeat = 0;
        Save(effects);
        var peers = Peers();
        for (var k = 0; k < peers.Count; k++)
        {
            var peer = peers[k];
            SendAppend(peer, effects, mode);
        }
    }

    private void SendAppend(NodeId peer, List<Effect> effects, SendMode mode = SendMode.Replicate)
    {
        var next = _nextIndex[peer];
        if (next <= _log.BaseIndex)
        {
            SendSnapshot(peer, effects);
            return;
        }

        var outstanding = _inFlight.ContainsKey(peer);
        if (outstanding && mode == SendMode.Replicate)
        {
            return;
        }

        var prev = next - 1;
        List<LogEntry> entries = outstanding && mode == SendMode.Probe ? [] : _log.From(next, _options.MaxEntriesPerAppend);
        if (entries.Count > 0)
        {
            _inFlight[peer] = prev + entries.Count;
        }

        var ae = new AppendEntries(_term, _context.Id, prev, _log.TermAt(prev), entries, _commitIndex, _round);
        effects.Add(new Send(peer, MessageCodec.Encode(ae)));
    }

    /// <summary>
    /// A follower behind the snapshot is sent it (P7-07): the chunk at the offset it last reported, so
    /// a heartbeat resends a lost chunk; from the start when the snapshot changed since.
    /// </summary>
    private void SendSnapshot(NodeId peer, List<Effect> effects)
    {
        var snapshot = _log.Snapshot!;
        var record = SnapshotRecord();
        var offset = _snapshotSent.TryGetValue(peer, out var sent) && sent.Index == snapshot.Index && sent.Offset < record.Length ? sent.Offset : 0;
        var length = (int)Math.Min(_options.SnapshotChunkBytes, record.Length - offset);
        var data = new byte[length];
        Array.Copy(record, offset, data, 0, length);
        effects.Add(new Send(peer, MessageCodec.Encode(new InstallSnapshot(_term, _context.Id, snapshot.Index, snapshot.Term, offset, data, offset + length == record.Length))));
    }

    private Built? _record;

    /// <summary>The snapshot's record, as a follower writes it: built once per snapshot.</summary>
    private byte[] SnapshotRecord()
    {
        var s = _log.Snapshot!;
        if (_record is null || _record.Index != s.Index)
        {
            _record = new Built(s.Index, EntryLog.SnapshotRecord(s.Index, s.Term, s.Configuration, s.State));
        }

        return _record.Bytes;
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
        _inFlight.Clear();
        _pending.Clear();
        _membershipRequest = null;
        _catchingUp = null;
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
        public ReadOnlyMemory<byte> Apply(long index, ReadOnlyMemory<byte> command) => ReadOnlyMemory<byte>.Empty;

        public bool IsQuery(ReadOnlyMemory<byte> command) => false;

        public ReadOnlyMemory<byte> Query(ReadOnlyMemory<byte> command) => ReadOnlyMemory<byte>.Empty;

        public ReadOnlyMemory<byte> Snapshot() => ReadOnlyMemory<byte>.Empty;

        public void Restore(ReadOnlyMemory<byte> snapshot)
        {
        }
    }
}
