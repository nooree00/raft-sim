using System;
using System.Collections.Generic;
using System.Globalization;

namespace Raft.Core;

/// <summary>An entry as recovery found it on disk, with the byte offset just past its record.</summary>
public sealed record StoredEntry(long Index, Term Term, byte[] Command, long EndOffset);

/// <summary>
/// A snapshot at the head of the log file (P7-04, phase 7 decision 2): the entries up to
/// <see cref="Index"/> compacted into the state machine's bytes, the last of them of term
/// <see cref="Term"/>, and the configuration in effect at <see cref="Index"/> (null for the initial one).
/// </summary>
public sealed record LogSnapshot(long Index, Term Term, Configuration? Configuration, byte[] State, long EndOffset);

/// <summary>
/// The outcome of reading the log file: the entries, the path taken, the length of its whole records,
/// the lowest index this read added or replaced (0 when it changed nothing), and the snapshot at the
/// file's head, if any; the entries then follow its index.
/// </summary>
public sealed record EntryLogRecovery(RecoveryPath Path, IReadOnlyList<StoredEntry> Entries, long ValidLength, string Detail, long FirstChanged = 0, LogSnapshot? Snapshot = null);

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
/// index and a term, or an index below 1, is corruption and recovery refuses. One exception, by
/// structure (P6-15, as for the term-vote file): a torn record followed, less than its length after
/// its start, by a whole valid record is a torn write whose cut a crash lost while keeping a later
/// write, and its bytes are skipped.
/// </summary>
public static class EntryLog
{
    public const string FileName = "entries.log";
    private const int Header = 4, Fixed = 24, Trailer = 4;

    /// <summary>The bytes a record adds to its command.</summary>
    public const int RecordOverhead = Header + Fixed + Trailer;

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

    /// <summary>
    /// A snapshot record (P7-04): the record framing with index 0, the snapshot's last term where an
    /// entry's term goes and its index where the previous term goes, then the configuration's length
    /// and encoding (length 0 for the initial configuration) and the state machine's bytes. Only the
    /// first record of a file may be one.
    /// </summary>
    public static byte[] SnapshotRecord(long index, Term term, Configuration? configuration, byte[] state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var config = configuration is null ? [] : configuration.Encode();
        var b = new List<byte>(Header + Fixed + 4 + config.Length + state.Length + Trailer);
        Put(b, Fixed + 4 + config.Length + state.Length, 4);
        Put(b, 0, 8);
        Put(b, term.Value, 8);
        Put(b, index, 8);
        Put(b, config.Length, 4);
        b.AddRange(config);
        b.AddRange(state);
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
    public static EntryLogRecovery Resume(List<StoredEntry> entries, byte[] file, long fileLength, long from, LogSnapshot? snapshot = null)
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
            if ((length < 0 || remaining < size || Checksum(file, (int)at, (int)(Header + length)) != Get(file, at + Header + length, 4))
                && TornBefore(file, fileLength, at, length >= Fixed && remaining >= size ? size : Header) is { } next)
            {
                at = next;
                continue;
            }

            if (length < 0 || remaining < size)
            {
                return new(RecoveryPath.TruncatedTornTail, entries, at, "torn tail after " + N(records) + " record(s): " + N(remaining) + " byte(s) cut", firstChanged, snapshot);
            }

            var last = at + size == fileLength;
            if (Checksum(file, (int)at, (int)(Header + length)) != Get(file, at + Header + length, 4))
            {
                return last
                    ? new(RecoveryPath.TruncatedTornTail, entries, at, "final record " + N(records) + " fails its checksum: torn, cut", firstChanged, snapshot)
                    : new(RecoveryPath.Refused, entries, at, "record " + N(records) + " at byte " + N(at) + " fails its checksum and is not the last: corruption", firstChanged, snapshot);
            }

            if (length < Fixed)
            {
                return new(RecoveryPath.Refused, entries, at, "record " + N(records) + " at byte " + N(at) + " is whole and valid but " + N(length) + " bytes long, too short for an index and two terms: corruption", firstChanged, snapshot);
            }

            var index = (long)Get(file, at + Header, 8);
            var term = (long)Get(file, at + Header + 8, 8);
            var previous = (long)Get(file, at + Header + 16, 8);
            if (index == 0 && at == 0 && term >= 0 && previous >= 0 && length >= Fixed + 4 && (long)Get(file, at + Header + Fixed, 4) is var configLength && configLength <= length - Fixed - 4)
            {
                var config = new byte[configLength];
                Array.Copy(file, at + Header + Fixed + 4, config, 0, configLength);
                var state = new byte[length - Fixed - 4 - configLength];
                Array.Copy(file, at + Header + Fixed + 4 + configLength, state, 0, state.Length);
                snapshot = new LogSnapshot(previous, new Term(term), configLength == 0 ? null : Configuration.Decode(config), state, at + size);
                at += size;
                records++;
                continue;
            }

            if (index < 1 || term < 0 || previous < 0)
            {
                return new(RecoveryPath.Refused, entries, at, "record " + N(records) + " at byte " + N(at) + " holds index " + N(index) + ", term " + N(term) + ": corruption", firstChanged, snapshot);
            }

            var command = new byte[length - Fixed];
            Array.Copy(file, at + Header + Fixed, command, 0, command.Length);
            var end = at + size;
            // Entries follow the snapshot's index, if there is one, and the first chains onto its term
            // (P7-04: a chain check counting from index 1 dropped every entry after a snapshot). A
            // record at or below the snapshot's index never overrides it: those entries are committed.
            var baseIndex = snapshot?.Index ?? 0;
            var at0 = index - baseIndex - 1;
            if (index <= baseIndex || at0 > entries.Count || (at0 == 0 ? snapshot?.Term.Value ?? 0 : entries[(int)at0 - 1].Term.Value) != previous)
            {
                unchained++;
            }
            else
            {
                entries.RemoveRange((int)at0, entries.Count - (int)at0);
                entries.Add(new StoredEntry(index, new Term(term), command, end));
                firstChanged = firstChanged == 0 ? index : Math.Min(firstChanged, index);
            }

            at = end;
            records++;
        }

        return new(RecoveryPath.Clean, entries, at, N(records) + " record(s), " + N(entries.Count) + " entries" + (unchained > 0 ? ", " + N(unchained) + " unchained dropped" : ""), firstChanged, snapshot);
    }

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static void Put(List<byte> b, long v, int bytes)
    {
        for (var shift = (bytes - 1) * 8; shift >= 0; shift -= 8)
        {
            b.Add((byte)(v >> shift));
        }
    }

    /// <summary>
    /// Where a whole valid record starts less than <paramref name="limit"/> bytes after
    /// <paramref name="at"/>, if one does: the record at <paramref name="at"/> is then a torn write a
    /// later one followed. The limit is the torn record's own length when its length field is whole
    /// and plausible, else the length field itself (a torn write shorter than it leaves it garbage).
    /// </summary>
    private static long? TornBefore(byte[] file, long fileLength, long at, long limit)
    {
        for (var next = at + 1; next < at + limit && next + Header + Fixed + Trailer <= fileLength; next++)
        {
            var length = (long)Get(file, next, 4);
            if (length >= Fixed && next + Header + length + Trailer <= fileLength && Get(file, next + Header, 8) >= 1
                && Checksum(file, (int)next, (int)(Header + length)) == Get(file, next + Header + length, 4))
            {
                return next;
            }
        }

        return null;
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
/// above, and cutting at the live record itself would bring the stale ones back (P4-02). After a
/// compaction (P7-05) the entries follow a snapshot: indices at or below its index no longer
/// resolve, except that the term at its index is its term.
/// </summary>
public sealed class LogStore
{
    /// <summary>The prefix of a compaction's file name; the rest is a number no file the node held at its start had (P7-05).</summary>
    public const string TempPrefix = "entries.";

    private readonly List<StoredEntry> _entries;
    private readonly List<long> _configurations = [];
    private long _fileLength;
    private long _nextTemp;

    public LogStore(EntryLogRecovery recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        if (recovery.Path == RecoveryPath.Refused)
        {
            throw new InvalidOperationException("refusing to start: " + recovery.Detail);
        }

        _entries = new List<StoredEntry>(recovery.Entries);
        _fileLength = recovery.ValidLength;
        Snapshot = recovery.Snapshot;
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

    /// <summary>The snapshot the log follows, null before the first compaction.</summary>
    public LogSnapshot? Snapshot { get; private set; }

    /// <summary>The snapshot's index: the entries start after it.</summary>
    public long BaseIndex => Snapshot?.Index ?? 0;

    public Term BaseTerm => Snapshot?.Term ?? Term.Zero;

    public long LastIndex => BaseIndex + _entries.Count;

    public Term LastTerm => _entries.Count == 0 ? BaseTerm : _entries[^1].Term;

    public Term TermAt(long index) => index == BaseIndex ? BaseTerm : index < BaseIndex ? throw Compacted(index) : _entries[(int)(index - BaseIndex) - 1].Term;

    public StoredEntry At(long index) => index <= BaseIndex ? throw Compacted(index) : _entries[(int)(index - BaseIndex) - 1];

    /// <summary>
    /// Names a compaction's file will not collide with: one past the largest number among the files
    /// the node held at its start. A crash can leave a compaction's file written and never renamed,
    /// and appending to it again would install its stale bytes (S-snapfile-2); any file a crash left
    /// is in the node's files at its next start, and every pending write died with the crash.
    /// </summary>
    public void AvoidNames(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (var f in files)
        {
            if (f.Length > TempPrefix.Length && f.Substring(0, TempPrefix.Length) == TempPrefix && Number(f, TempPrefix.Length) is { } n)
            {
                _nextTemp = Math.Max(_nextTemp, n + 1);
            }
        }
    }

    /// <summary>
    /// Compact the log up to <paramref name="index"/> (P7-05, phase 7 decision 2): a new, uniquely
    /// named file holding the snapshot record and then every retained entry, renamed over the log
    /// file. One rename, so a crash leaves the old log or the new one; the world holds the rename
    /// until the new file is durable (P7-00). Writes emitted after it reach the new file.
    /// </summary>
    public IReadOnlyList<Persist> Compact(long index, Configuration? configuration, byte[] state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (index <= BaseIndex || index > LastIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "a compaction must cover more than the snapshot and no more than the log");
        }

        var term = TermAt(index);
        var bytes = new List<byte>(EntryLog.SnapshotRecord(index, term, configuration, state));
        var snapshot = new LogSnapshot(index, term, configuration, state, bytes.Count);
        var retained = new List<StoredEntry>();
        var previous = term;
        for (var i = index + 1; i <= LastIndex; i++)
        {
            var e = At(i);
            bytes.AddRange(EntryLog.Record(i, e.Term, previous, e.Command));
            retained.Add(e with { EndOffset = bytes.Count });
            previous = e.Term;
        }

        _entries.Clear();
        _entries.AddRange(retained);
        while (_configurations.Count > 0 && _configurations[0] <= index)
        {
            _configurations.RemoveAt(0);
        }

        _fileLength = bytes.Count;
        Snapshot = snapshot;
        var name = NewFileName();
        return new List<Persist> { new PersistAppend(name, bytes.ToArray()), new PersistRename(name, EntryLog.FileName) };
    }

    /// <summary>The decimal number <paramref name="s"/> holds from <paramref name="at"/> to its end, or null.</summary>
    private static long? Number(string s, int at)
    {
        if (at >= s.Length || s.Length - at > 18)
        {
            return null;
        }

        long n = 0;
        for (var i = at; i < s.Length; i++)
        {
            if (s[i] < '0' || s[i] > '9')
            {
                return null;
            }

            n = (n * 10) + (s[i] - '0');
        }

        return n;
    }

    /// <summary>A name for a file that will replace the log: no file the node held at its start had it, and it was never given before.</summary>
    public string NewFileName() => TempPrefix + (_nextTemp++).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Install a snapshot a leader sent (P7-07, Figure 13), whose record the node has written to
    /// <paramref name="file"/>, <paramref name="written"/> bytes: the entries after it are kept if
    /// the log holds the snapshot's last entry, else the log is discarded. The kept entries follow
    /// the record in the file, which is then renamed over the log, as a compaction's is.
    /// </summary>
    public IReadOnlyList<Persist> Install(LogSnapshot snapshot, string file, long written)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var keep = snapshot.Index > BaseIndex && snapshot.Index <= LastIndex && TermAt(snapshot.Index) == snapshot.Term;
        var bytes = new List<byte>();
        var retained = new List<StoredEntry>();
        var previous = snapshot.Term;
        for (var i = snapshot.Index + 1; keep && i <= LastIndex; i++)
        {
            var e = At(i);
            bytes.AddRange(EntryLog.Record(i, e.Term, previous, e.Command));
            retained.Add(e with { EndOffset = written + bytes.Count });
            previous = e.Term;
        }

        _entries.Clear();
        _entries.AddRange(retained);
        while (_configurations.Count > 0 && (!keep || _configurations[0] <= snapshot.Index))
        {
            _configurations.RemoveAt(0);
        }

        _fileLength = written + bytes.Count;
        Snapshot = snapshot with { EndOffset = written };
        var writes = new List<Persist>();
        if (bytes.Count > 0)
        {
            writes.Add(new PersistAppend(file, bytes.ToArray()));
        }

        writes.Add(new PersistRename(file, EntryLog.FileName));
        return writes;
    }

    private static ArgumentOutOfRangeException Compacted(long index) => new(nameof(index), index, "compacted into the snapshot");

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
            var e = At(i);
            result.Add(new LogEntry(e.Term, e.Command));
        }

        return result;
    }

    /// <summary>Drop every entry at <paramref name="index"/> or above; null when there is none. Never at or below the snapshot: those entries are committed.</summary>
    public PersistTruncate? TruncateFrom(long index)
    {
        if (index > LastIndex)
        {
            return null;
        }

        if (index <= BaseIndex)
        {
            throw Compacted(index);
        }

        var at = (int)(index - BaseIndex) - 1;
        _fileLength = at == 0 ? Snapshot?.EndOffset ?? 0 : _entries[at - 1].EndOffset;
        _entries.RemoveRange(at, _entries.Count - at);
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
