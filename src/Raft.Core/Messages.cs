using System;
using System.Collections.Generic;
using System.Linq;

namespace Raft.Core;

/// <summary>A Raft RPC or its response (paper Figure 2). Every message carries its sender's term.</summary>
public abstract record Message(Term Term);

/// <summary>Invoked by candidates to gather votes (§5.2).</summary>
public sealed record RequestVote(Term Term, NodeId Candidate, long LastLogIndex, Term LastLogTerm) : Message(Term);

/// <summary>The reply to <see cref="RequestVote"/>.</summary>
public sealed record RequestVoteResponse(Term Term, bool VoteGranted) : Message(Term);

/// <summary>An entry as it travels: its term and its command's bytes.</summary>
public sealed record LogEntry(Term Term, byte[] Command)
{
    public bool Equals(LogEntry? other) => other is not null && Term == other.Term && Enumerable.SequenceEqual(Command, other.Command);

    public override int GetHashCode() => Term.GetHashCode() ^ Command.Length;
}

/// <summary>Invoked by leaders to replicate entries (§5.3); with no entries, a heartbeat (§5.2).</summary>
public sealed record AppendEntries(Term Term, NodeId Leader, long PrevLogIndex, Term PrevLogTerm, IReadOnlyList<LogEntry> Entries, long LeaderCommit) : Message(Term)
{
    public bool Equals(AppendEntries? other) =>
        other is not null && Term == other.Term && Leader == other.Leader && PrevLogIndex == other.PrevLogIndex
        && PrevLogTerm == other.PrevLogTerm && LeaderCommit == other.LeaderCommit && Entries.SequenceEqual(other.Entries);

    public override int GetHashCode() => Term.GetHashCode() ^ Leader.GetHashCode() ^ Entries.Count;
}

/// <summary>The reply to <see cref="AppendEntries"/>; <see cref="MatchIndex"/> is the last index the follower now matches.</summary>
public sealed record AppendEntriesResponse(Term Term, bool Success, long MatchIndex) : Message(Term);

/// <summary>
/// Invoked by leaders to send a follower behind their snapshot a chunk of it (Figure 13, phase 7
/// decision 4): the bytes of the snapshot's record at <see cref="Offset"/>, the last when
/// <see cref="Done"/>.
/// </summary>
public sealed record InstallSnapshot(Term Term, NodeId Leader, long LastIncludedIndex, Term LastIncludedTerm, long Offset, byte[] Data, bool Done) : Message(Term)
{
    public bool Equals(InstallSnapshot? other) =>
        other is not null && Term == other.Term && Leader == other.Leader && LastIncludedIndex == other.LastIncludedIndex
        && LastIncludedTerm == other.LastIncludedTerm && Offset == other.Offset && Done == other.Done && Enumerable.SequenceEqual(Data, other.Data);

    public override int GetHashCode() => Term.GetHashCode() ^ Leader.GetHashCode() ^ Offset.GetHashCode();
}

/// <summary>
/// The reply to <see cref="InstallSnapshot"/>: for the snapshot of <see cref="LastIncludedIndex"/>,
/// the bytes the follower holds from its start (<see cref="Received"/>), or that the follower holds
/// it whole (<see cref="Done"/>), installed now or covered by its own snapshot.
/// </summary>
public sealed record InstallSnapshotResponse(Term Term, long LastIncludedIndex, long Received, bool Done) : Message(Term);
