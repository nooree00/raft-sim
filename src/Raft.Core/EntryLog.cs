using System;
using System.Collections.Generic;
using System.Globalization;

namespace Raft.Core;

/// <summary>An entry as recovery found it on disk, with the byte offset just past its record.</summary>
public sealed record StoredEntry(long Index, Term Term, byte[] Command, long EndOffset);

/// <summary>
/// The outcome of reading the log file: the entries, the path taken, the length of its whole records,
/// and the lowest index this read added or replaced (0 when it changed nothing).
/// </summary>
public sealed record EntryLogRecovery(RecoveryPath Path, IReadOnlyList<StoredEntry> Entries, long ValidLength, string Detail, long FirstChanged = 0);

/// <summary>
/// The log's entries, persisted in their own append-only file (spec §8, P4 decision 4), with the
/// term-and-vote record's framing (P3-03): a 4-byte big-endian payload length, the payload (index,
/// term and the previous entry's term, 8 bytes each, then the command), and a 4-byte FNV-1a
/// checksum over length and payload. Recovery reads records in order and a record for index i
/// discards every entry at i or above: a truncation lost in a crash cannot bring back the suffix it
/// removed, because the append that followed it overrides that suffix (P4-02's prediction). A
/// record is taken only if it chains onto what recovery holds: index at most one past the end, and
/// the previous entry's term matching (AppendEntries' consistency check, on disk). Unchained records
/// are dropped: an unsynced write that survived while one it depended on was lost (found by the
/// crash test: a lost truncate-and-append followed by a surviving append joined two logs that never
/// coexisted). Within one node's writes an (index, term) is always the same entry, which is what
/// makes the chain sound, by the induction Log Matching rests on. A torn final record
/// is truncated; a checksum failure in any earlier record, or a whole record too short to hold an
/// index and a term, or an index below 1, is corruption and recovery refuses.
/// </summary>
public static class EntryLog
{
    public const string FileName = "entries.log";
    private const int Header = 4, Fixed = 24, Trailer = 4;

    public static byte[] Record(long index, Term term, Term previousTerm, byte[] command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var b = new List<byte>(Header + Fixed + command.Length + Trailer);
        Put(b, Fixed + command.Length, 4);
        Put(b, index, 8);
        Put(b, term.Value, 8);
        Put(b, previousTerm.Value, 8);
        b.AddRange(command);

        Put(b, Checksum(b, 0, b.Count), 4);
        return b.ToArray();
    }

    public static EntryLogRecovery Recover(byte[]? file) =>
        file is null || file.Length == 0 ? new(RecoveryPath.Empty, new List<StoredEntry>(), 0, "no records") : Resume(new List<StoredEntry>(), file, file.Length, 0);

    /// <summary>
    /// Continues a recovery: reads the records of <paramref name="file"/>'s first
    /// <paramref name="fileLength"/> bytes from <paramref name="from"/> (the end of the whole records
    /// already read into <paramref name="entries"/>) onto <paramref name="entries"/>, which it
    /// changes. Recover is Resume from nothing; an observer of a growing file resumes where it
    /// stopped instead of reading it again.
    /// </summary>
    public static EntryLogRecovery Resume(List<StoredEntry> entries, byte[] file, long fileLength, long from)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(file);
        long at = from;
        var records = 0;
        var unchained = 0;
        var firstChanged = 0L;
        while (at < fileLength)
        {
            var remaining = fileLength - at;
            var length = remaining >= Header ? (long)Get(file, at, 4) : -1;
            var size = Header + length + Trailer;
            if (length < 0 || remaining < size)
            {
                return new(RecoveryPath.TruncatedTornTail, entries, at, "torn tail after " + N(records) + " record(s): " + N(remaining) + " byte(s) cut", firstChanged);
            }

            var last = at + size == fileLength;
            if (Checksum(file, (int)at, (int)(Header + length)) != Get(file, at + Header + length, 4))
            {
                return last
                    ? new(RecoveryPath.TruncatedTornTail, entries, at, "final record " + N(records) + " fails its checksum: torn, cut", firstChanged)
                    : new(RecoveryPath.Refused, entries, at, "record " + N(records) + " at byte " + N(at) + " fails its checksum and is not the last: corruption", firstChanged);
            }

            if (length < Fixed)
            {
                return new(RecoveryPath.Refused, entries, at, "record " + N(records) + " at byte " + N(at) + " is whole and valid but " + N(length) + " bytes long, too short for an index and two terms: corruption");
            }

            var index = (long)Get(file, at + Header, 8);
            var term = (long)Get(file, at + Header + 8, 8);
            var previous = (long)Get(file, at + Header + 16, 8);
            if (index < 1 || term < 0 || previous < 0)
            {
                return new(RecoveryPath.Refused, entries, at, "record " + N(records) + " at byte " + N(at) + " holds index " + N(index) + ", term " + N(term) + ": corruption");
            }

            var command = new byte[length - Fixed];
            Array.Copy(file, at + Header + Fixed, command, 0, command.Length);
            var end = at + size;
            if (index > entries.Count + 1 || (index == 1 ? 0 : entries[(int)index - 2].Term.Value) != previous)
            {
                unchained++;
            }
            else
            {
                entries.RemoveRange((int)(index - 1), entries.Count - (int)(index - 1));
                entries.Add(new StoredEntry(index, new Term(term), command, end));
                firstChanged = firstChanged == 0 ? index : Math.Min(firstChanged, index);
            }

            at = end;
            records++;
        }

        return new(RecoveryPath.Clean, entries, at, N(records) + " record(s), " + N(entries.Count) + " entries" + (unchained > 0 ? ", " + N(unchained) + " unchained dropped" : ""), firstChanged);
    }

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static void Put(List<byte> b, long v, int bytes)
    {
        for (var shift = (bytes - 1) * 8; shift >= 0; shift -= 8)
        {
            b.Add((byte)(v >> shift));
        }
    }

    private static ulong Get(byte[] b, long at, int bytes)
    {
        ulong v = 0;
        for (var i = 0; i < bytes; i++)
        {
            v = (v << 8) | b[at + i];
        }

        return v;
    }

    /// <summary>32-bit FNV-1a, as <see cref="TermVoteLog"/>.</summary>
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

/// <summary>
/// A node's log and the file under it: the entries, and for each the byte offset just past its
/// record, so that truncating from index i cuts the file at the end of the live entry i-1. After a
/// lost truncation the live record for an index can sit after stale records for that index and
/// above, and cutting at the live record itself would bring the stale ones back (P4-02).
/// </summary>
public sealed class LogStore
{
    private readonly List<StoredEntry> _entries;
    private readonly List<long> _configurations = [];
    private long _fileLength;

    public LogStore(EntryLogRecovery recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        if (recovery.Path == RecoveryPath.Refused)
        {
            throw new InvalidOperationException("refusing to start: " + recovery.Detail);
        }

        _entries = new List<StoredEntry>(recovery.Entries);
        _fileLength = recovery.ValidLength;
        for (var i = 0; i < _entries.Count; i++)
        {
            Note(_entries[i]);
        }

        if (recovery.Path == RecoveryPath.TruncatedTornTail)
        {
            CutTornTail = new PersistTruncate(EntryLog.FileName, recovery.ValidLength);
        }
    }

    /// <summary>The cut a node must make before anything else after recovering a torn tail (P3-08's finding, for this file).</summary>
    public PersistTruncate? CutTornTail { get; }

    public long LastIndex => _entries.Count;

    public Term LastTerm => _entries.Count == 0 ? Term.Zero : _entries[^1].Term;

    public Term TermAt(long index) => index == 0 ? Term.Zero : _entries[(int)index - 1].Term;

    public StoredEntry At(long index) => _entries[(int)index - 1];

    /// <summary>
    /// The index of the latest configuration entry at or below <paramref name="index"/>, 0 when there
    /// is none (P6-05). Kept with the log, dropped by a truncation and added by an append, so the
    /// configuration in effect is the log's and costs no scan (a value cached outside the log
    /// survived a truncation, P6-03).
    /// </summary>
    public long ConfigurationIndexAtOrBelow(long index)
    {
        for (var i = _configurations.Count - 1; i >= 0; i--)
        {
            if (_configurations[i] <= index)
            {
                return _configurations[i];
            }
        }

        return 0;
    }

    /// <summary>The entries from an index to the end, at most <paramref name="max"/> of them.</summary>
    public List<LogEntry> From(long index, int max)
    {
        var result = new List<LogEntry>();
        for (var i = index; i <= LastIndex && result.Count < max; i++)
        {
            result.Add(new LogEntry(_entries[(int)i - 1].Term, _entries[(int)i - 1].Command));
        }

        return result;
    }

    /// <summary>Drop every entry at <paramref name="index"/> or above; null when there is none.</summary>
    public PersistTruncate? TruncateFrom(long index)
    {
        if (index > LastIndex)
        {
            return null;
        }

        _fileLength = index == 1 ? 0 : _entries[(int)index - 2].EndOffset;
        _entries.RemoveRange((int)index - 1, _entries.Count - (int)index + 1);
        while (_configurations.Count > 0 && _configurations[^1] >= index)
        {
            _configurations.RemoveAt(_configurations.Count - 1);
        }

        return new PersistTruncate(EntryLog.FileName, _fileLength);
    }

    /// <summary>Append entries after the last, as one write.</summary>
    public PersistAppend Append(IReadOnlyList<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var bytes = new List<byte>();
        foreach (var e in entries)
        {
            var record = EntryLog.Record(LastIndex + 1, e.Term, LastTerm, e.Command);
            bytes.AddRange(record);
            _fileLength += record.Length;
            _entries.Add(new StoredEntry(LastIndex + 1, e.Term, e.Command, _fileLength));
            Note(_entries[^1]);
        }

        return new PersistAppend(EntryLog.FileName, bytes.ToArray());
    }

    private void Note(StoredEntry e)
    {
        if (Configuration.IsInternal(e.Command) && Configuration.Decode(e.Command) is not null)
        {
            _configurations.Add(e.Index);
        }
    }
}
