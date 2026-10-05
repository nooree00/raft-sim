using System;
using System.Collections.Generic;

namespace Raft.Core;

/// <summary>
/// The wire form of <see cref="Message"/> (docs/design/node-interface.md §6). A type byte, then
/// fixed-width big-endian fields, and a count or a length before every variable part. The rule
/// (P3-02): every byte string decodes to at most one message, and every message has exactly one
/// encoding. So decoding rejects trailing bytes, an unknown type, a boolean other than 0 or 1, a
/// negative term, index or length, and a node id below 1. Canonical form removes the ambiguity of
/// two encodings; it does not detect corruption that yields another valid message.
/// </summary>
public static class MessageCodec
{
    private const byte RequestVoteType = 1, RequestVoteResponseType = 2, AppendEntriesType = 3, AppendEntriesResponseType = 4, InstallSnapshotType = 5, InstallSnapshotResponseType = 6;

    public static byte[] Encode(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var w = new List<byte>();
        switch (message)
        {
            case RequestVote m:
                w.Add(RequestVoteType); Long(w, m.Term.Value); Int(w, m.Candidate.Value); Long(w, m.LastLogIndex); Long(w, m.LastLogTerm.Value);
                break;
            case RequestVoteResponse m:
                w.Add(RequestVoteResponseType); Long(w, m.Term.Value); w.Add(m.VoteGranted ? (byte)1 : (byte)0);
                break;
            case AppendEntries m:
                w.Add(AppendEntriesType); Long(w, m.Term.Value); Int(w, m.Leader.Value); Long(w, m.PrevLogIndex); Long(w, m.PrevLogTerm.Value); Long(w, m.LeaderCommit); Long(w, m.Round);
                Int(w, m.Entries.Count);
                foreach (var e in m.Entries)
                {
                    Long(w, e.Term.Value); Int(w, e.Command.Length); w.AddRange(e.Command);
                }

                break;
            case AppendEntriesResponse m:
                w.Add(AppendEntriesResponseType); Long(w, m.Term.Value); w.Add(m.Success ? (byte)1 : (byte)0); Long(w, m.MatchIndex); Long(w, m.Round);
                break;
            case InstallSnapshot m:
                w.Add(InstallSnapshotType); Long(w, m.Term.Value); Int(w, m.Leader.Value); Long(w, m.LastIncludedIndex); Long(w, m.LastIncludedTerm.Value); Long(w, m.Offset);
                Int(w, m.Data.Length); w.AddRange(m.Data); w.Add(m.Done ? (byte)1 : (byte)0);
                break;
            case InstallSnapshotResponse m:
                w.Add(InstallSnapshotResponseType); Long(w, m.Term.Value); Long(w, m.LastIncludedIndex); Long(w, m.Received); w.Add(m.Done ? (byte)1 : (byte)0);
                break;
            default:
                throw new ArgumentException("unknown message type", nameof(message));
        }

        return w.ToArray();
    }

    /// <summary>The one message these bytes encode, or null when they encode none.</summary>
    public static Message? Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var r = new Reader(bytes);
        Message? m = r.Byte() switch
        {
            RequestVoteType => r.Term() is { } t && r.Node() is { } c && r.Index() is { } li && r.Term() is { } lt ? new RequestVote(t, c, li, lt) : null,
            RequestVoteResponseType => r.Term() is { } t && r.Bool() is { } g ? new RequestVoteResponse(t, g) : null,
            AppendEntriesType => DecodeAppendEntries(r),
            AppendEntriesResponseType => r.Term() is { } t && r.Bool() is { } s && r.Index() is { } mi && r.Index() is { } round ? new AppendEntriesResponse(t, s, mi, round) : null,
            InstallSnapshotType => r.Term() is { } t && r.Node() is { } leader && r.Index() is { } li && r.Term() is { } lt && r.Index() is { } offset && r.Count() is { } n && r.Bytes(n) is { } data && r.Bool() is { } done
                ? new InstallSnapshot(t, leader, li, lt, offset, data, done) : null,
            InstallSnapshotResponseType => r.Term() is { } t && r.Index() is { } li && r.Index() is { } received && r.Bool() is { } done ? new InstallSnapshotResponse(t, li, received, done) : null,
            _ => null,
        };
        return m is not null && r.Ok && r.AtEnd ? m : null;
    }

    private static AppendEntries? DecodeAppendEntries(Reader r)
    {
        if (r.Term() is not { } t || r.Node() is not { } leader || r.Index() is not { } pi || r.Term() is not { } pt || r.Index() is not { } commit || r.Index() is not { } round || r.Count() is not { } n)
        {
            return null;
        }

        var entries = new List<LogEntry>();
        for (var i = 0; i < n; i++)
        {
            if (r.Term() is not { } et || r.Count() is not { } len || r.Bytes(len) is not { } command)
            {
                return null;
            }

            entries.Add(new LogEntry(et, command));
        }

        return new AppendEntries(t, leader, pi, pt, entries, commit, round);
    }

    private static void Int(List<byte> w, int v)
    {
        for (var shift = 24; shift >= 0; shift -= 8)
        {
            w.Add((byte)(v >> shift));
        }
    }

    private static void Long(List<byte> w, long v)
    {
        for (var shift = 56; shift >= 0; shift -= 8)
        {
            w.Add((byte)(v >> shift));
        }
    }

    /// <summary>Reads fields in order; any malformed or missing field makes <see cref="Ok"/> false for good.</summary>
    private sealed class Reader(byte[] bytes)
    {
        private int _at;

        public bool Ok { get; private set; } = true;

        public bool AtEnd => _at == bytes.Length;

        private bool Has(int n)
        {
            Ok = Ok && n >= 0 && bytes.Length - _at >= n;
            return Ok;
        }

        private long Read(int n)
        {
            long v = 0;
            for (var i = 0; i < n; i++)
            {
                v = (v << 8) | bytes[_at++];
            }

            return v;
        }

        public byte Byte() => Has(1) ? (byte)Read(1) : (byte)0;

        public bool? Bool() => Has(1) && Read(1) is var v && v <= 1 ? v == 1 : Fail<bool>();

        public Term? Term() => Has(8) && Read(8) is var v and >= 0 ? new Term(v) : Fail<Term>();

        public long? Index() => Has(8) && Read(8) is var v and >= 0 ? v : Fail<long>();

        public NodeId? Node() => Has(4) && (int)Read(4) is var v and >= 1 ? new NodeId(v) : Fail<NodeId>();

        public int? Count() => Has(4) && (int)Read(4) is var v and >= 0 ? v : Fail<int>();

        public byte[]? Bytes(int n)
        {
            if (!Has(n))
            {
                return null;
            }

            var b = new byte[n];
            Array.Copy(bytes, _at, b, 0, n);
            _at += n;
            return b;
        }

        private T? Fail<T>()
            where T : struct
        {
            Ok = false;
            return null;
        }
    }
}
