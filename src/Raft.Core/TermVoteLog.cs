using System;
using System.Collections.Generic;
using System.Globalization;

namespace Raft.Core;

/// <summary>How recovery read the term-and-vote file.</summary>
public enum RecoveryPath
{
    /// <summary>No file, or an empty one: term 0, no vote.</summary>
    Empty,

    /// <summary>Every record whole and valid.</summary>
    Clean,

    /// <summary>The final record was incomplete or failed its checksum: a torn write, cut off.</summary>
    TruncatedTornTail,

    /// <summary>A record before the last failed its checksum, or a whole record has the wrong shape: corruption, not a torn write. The node must not start.</summary>
    Refused,
}

/// <summary>What the node restores: its current term and its vote in that term.</summary>
public sealed record TermVoteState(Term Term, NodeId? VotedFor)
{
    public static TermVoteState Initial { get; } = new(Term.Zero, null);
}

/// <summary>The outcome of reading the file: the state, the path taken, and the length of its valid prefix.</summary>
public sealed record TermVoteRecovery(RecoveryPath Path, TermVoteState State, int ValidLength, string Detail);

/// <summary>
/// `currentTerm` and `votedFor`, persisted as an append-only file of records (spec §8, P3-03):
/// a 4-byte big-endian payload length (always 12), the payload (term as 8 bytes, the vote as a
/// 4-byte node id, 0 for none), and a 4-byte FNV-1a checksum over the length and payload.
/// Recovery takes the last valid record. A torn **final** record, incomplete or failing its
/// checksum, is truncated; a checksum failure in any earlier record, or a whole record whose
/// length is not 12, is corruption and recovery refuses. One exception, by structure (P6-15): a
/// torn record followed, less than a record's length after its start, by a whole valid record is
/// a torn write the cut after it never reached (a crash may keep any subset of the writes in
/// flight, docs/design/node-interface.md §4), and its bytes are skipped; a corrupted whole record
/// has no such successor and is still refused.
/// </summary>
public static class TermVoteLog
{
    public const string FileName = "term-vote.log";
    private const int PayloadLength = 12, RecordLength = 4 + PayloadLength + 4;

    public static byte[] Record(Term term, NodeId? votedFor)
    {
        var b = new List<byte>(RecordLength);
        Put(b, PayloadLength, 4);
        Put(b, term.Value, 8);
        Put(b, votedFor?.Value ?? 0, 4);
        Put(b, Checksum(b, 0, b.Count), 4);
        return b.ToArray();
    }

    public static TermVoteRecovery Recover(byte[]? file)
    {
        if (file is null || file.Length == 0)
        {
            return new(RecoveryPath.Empty, TermVoteState.Initial, 0, "no records");
        }

        var state = TermVoteState.Initial;
        var at = 0;
        var records = 0;
        var skipped = 0;
        while (at < file.Length)
        {
            var remaining = file.Length - at;
            var length = remaining >= 4 ? (int)Get(file, at, 4) : -1;
            var size = 4 + length + 4;
            if ((length < 0 || remaining < size || Checksum(file, at, 4 + length) != Get(file, at + 4 + length, 4)) && TornBefore(file, at) is { } next)
            {
                skipped += next - at;
                at = next;
                continue;
            }

            if (length < 0 || remaining < size)
            {
                // Runs past the end: only the final record can, and only a torn write makes it.
                return new(RecoveryPath.TruncatedTornTail, state, at, "torn tail after " + N(records) + " record(s): " + N(remaining) + " byte(s) cut");
            }

            var last = at + size == file.Length;
            if (Checksum(file, at, 4 + length) != Get(file, at + 4 + length, 4))
            {
                return last
                    ? new(RecoveryPath.TruncatedTornTail, state, at, "final record " + N(records) + " fails its checksum: torn, cut")
                    : new(RecoveryPath.Refused, state, at, "record " + N(records) + " at byte " + N(at) + " fails its checksum and is not the last: corruption");
            }

            if (length != PayloadLength)
            {
                return new(RecoveryPath.Refused, state, at, "record " + N(records) + " at byte " + N(at) + " is whole and valid but " + N(length) + " bytes long, not " + N(PayloadLength) + ": corruption");
            }

            var term = (long)Get(file, at + 4, 8);
            var vote = (int)Get(file, at + 12, 4);
            if (term < 0 || vote < 0)
            {
                return new(RecoveryPath.Refused, state, at, "record " + N(records) + " at byte " + N(at) + " holds a negative term or vote: corruption");
            }

            state = new TermVoteState(new Term(term), vote == 0 ? null : new NodeId(vote));
            at += size;
            records++;
        }

        return new(RecoveryPath.Clean, state, at, N(records) + " record(s)" + (skipped > 0 ? ", " + N(skipped) + " torn byte(s) skipped" : ""));
    }

    /// <summary>
    /// Where a whole valid record starts less than a record's length after <paramref name="at"/>, if
    /// one does: the record at <paramref name="at"/> is then a torn write that a later one followed.
    /// </summary>
    private static int? TornBefore(byte[] file, int at)
    {
        for (var next = at + 1; next < at + RecordLength && next + RecordLength <= file.Length; next++)
        {
            if (Get(file, next, 4) == PayloadLength && Checksum(file, next, 4 + PayloadLength) == Get(file, next + 4 + PayloadLength, 4))
            {
                return next;
            }
        }

        return null;
    }

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static void Put(List<byte> b, long v, int bytes)
    {
        for (var shift = (bytes - 1) * 8; shift >= 0; shift -= 8)
        {
            b.Add((byte)(v >> shift));
        }
    }

    private static ulong Get(byte[] b, int at, int bytes)
    {
        ulong v = 0;
        for (var i = 0; i < bytes; i++)
        {
            v = (v << 8) | b[at + i];
        }

        return v;
    }

    /// <summary>32-bit FNV-1a, hand-written (the BCL's hashes are per-process randomized or absent from Core's allowlist).</summary>
    private static uint Checksum(IReadOnlyList<byte> b, int at, int count)
    {
        var h = 2166136261u;
        for (var i = at; i < at + count; i++)
        {
            h = (h ^ b[i]) * 16777619u;
        }

        return h;
    }
}
