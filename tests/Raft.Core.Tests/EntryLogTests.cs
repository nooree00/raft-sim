using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P4-02: the log file. Recovery truncates a torn final record, refuses corruption, and lets a
/// later record for an index override everything at and above it; the crash-during-write test
/// (spec §11) crashes a log after each write in every loss mode, over several crash-and-restart
/// cycles, and requires every recovery to be a prefix of something that was written. Vacuity risk:
/// a crash test that only crashes between whole records never tears one, and one that never
/// truncates never meets a stale suffix; guarded by counts per recovery path and per write in
/// flight, each required non-zero. Sabotages S-logfile-1..3.
/// </summary>
public sealed class EntryLogTests
{
    private static LogEntry E(long term, string command) => new(new Term(term), Encoding.ASCII.GetBytes(command));

    /// <summary>Records as given, each chained to the term the previous record in the list gave its index - 1 (0 at index 1).</summary>
    private static byte[] File(params (long Index, long Term, string Command)[] records)
    {
        var terms = new Dictionary<long, long>();
        var bytes = new List<byte>();
        foreach (var r in records)
        {
            bytes.AddRange(EntryLog.Record(r.Index, new Term(r.Term), new Term(r.Index == 1 ? 0 : terms.GetValueOrDefault(r.Index - 1)), Encoding.ASCII.GetBytes(r.Command)));
            terms[r.Index] = r.Term;
        }

        return bytes.ToArray();
    }

    private static string Show(IEnumerable<StoredEntry> es) => string.Join(" ", es.Select(e => $"{e.Index}:{e.Term.Value}:{Encoding.ASCII.GetString(e.Command)}"));

    [Fact]
    public void RecordsRoundTripAndALaterRecordForAnIndexOverridesEverythingFromIt()
    {
        var clean = EntryLog.Recover(File((1, 1, "a"), (2, 1, "b"), (3, 2, "c")));
        Assert.Equal(RecoveryPath.Clean, clean.Path);
        Assert.Equal("1:1:a 2:1:b 3:2:c", Show(clean.Entries));

        var overridden = EntryLog.Recover(File((1, 1, "a"), (2, 1, "b"), (3, 1, "c"), (2, 3, "B")));
        Assert.Equal("1:1:a 2:3:B", Show(overridden.Entries));
    }

    [Fact]
    public void ARecordThatDoesNotChainOntoTheRecoveredLogIsDropped()
    {
        var gap = EntryLog.Recover(File((1, 1, "a"), (3, 1, "c"), (2, 1, "b")));
        Assert.Equal(RecoveryPath.Clean, gap.Path);
        Assert.Equal("1:1:a 2:1:b", Show(gap.Entries));
        Assert.Contains("1 unchained dropped", gap.Detail, StringComparison.Ordinal);

        // The crash test's finding: a lost truncate-and-append (entries 2..3 of term 4) followed by a
        // surviving append of 4, chained to term 4: the file holds the old 2..3 of term 1 and the new 4.
        var old = File((1, 1, "a"), (2, 1, "b"), (3, 1, "c"));
        var survivor = EntryLog.Record(4, new Term(5), new Term(4), Encoding.ASCII.GetBytes("d"));
        var mixed = EntryLog.Recover([.. old, .. survivor]);
        Assert.Equal("1:1:a 2:1:b 3:1:c", Show(mixed.Entries));
    }

    [Fact]
    public void ATornFinalRecordIsCutAtEveryLength()
    {
        var whole = File((1, 1, "a"), (2, 1, "bb"), (3, 1, "ccc"));
        var twoEnd = File((1, 1, "a"), (2, 1, "bb")).Length;
        Assert.Equal(twoEnd, whole.Length - EntryLog.Record(3, new Term(1), new Term(1), Encoding.ASCII.GetBytes("ccc")).Length);
        for (var cut = twoEnd + 1; cut < whole.Length; cut++)
        {
            var r = EntryLog.Recover(whole[..cut]);
            Assert.True(r.Path == RecoveryPath.TruncatedTornTail && r.Entries.Count == 2 && r.ValidLength == twoEnd, $"cut at {cut}: {r.Path}, {r.Entries.Count} entries, valid {r.ValidLength}");
        }
    }

    [Fact]
    public void CorruptionBeforeTheLastRecordAndMalformedRecordsRefuse()
    {
        var f = File((1, 1, "a"), (2, 1, "b"));
        f[10] ^= 0x40;
        Assert.Equal(RecoveryPath.Refused, EntryLog.Recover(f).Path);
        Assert.Equal(RecoveryPath.Refused, EntryLog.Recover([.. File((0, 1, "x")), .. File((1, 1, "a"))]).Path);
        Assert.Throws<InvalidOperationException>(() => new LogStore(EntryLog.Recover(f)));
    }

    /// <summary>
    /// The case the file offsets exist for: after a lost truncation the live record for index 4 sits
    /// after the stale 4 and 5. Truncating from 4 again must cut at the end of live 3, not at the
    /// live 4, or the stale suffix comes back.
    /// </summary>
    [Fact]
    public void TruncatingAfterALostTruncationCutsTheStaleSuffixToo()
    {
        var file = File((1, 1, "a"), (2, 1, "b"), (3, 1, "c"), (4, 1, "d"), (5, 1, "e"), (4, 2, "D"));
        var store = new LogStore(EntryLog.Recover(file));
        Assert.Equal(4, store.LastIndex);
        var cut = store.TruncateFrom(4)!;
        var after = EntryLog.Recover(file[..(int)cut.Length]);
        Assert.Equal("1:1:a 2:1:b 3:1:c", Show(after.Entries));
    }

    /// <summary>
    /// The crash-during-write test. A log is written through <see cref="SimDisk"/> (appends of one
    /// to three entries, some after truncating a random suffix), crashed with some writes in flight,
    /// recovered, and written again, three cycles per seed. Every recovery must not refuse, and must
    /// equal a prefix of the log as it stood after some write between the last completed one and the
    /// last issued. Returns the failures and the counts.
    /// </summary>
    private static (List<string> Failures, Dictionary<string, int> Counts) CrashDuringWrites(Func<byte[]?, EntryLogRecovery> recover)
    {
        var failures = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(string k) => counts[k] = counts.GetValueOrDefault(k) + 1;
        foreach (var mode in new[] { DiskLoss.Pending, DiskLoss.Torn, DiskLoss.Reordered })
        {
            for (var seed = 1; seed <= 200; seed++)
            {
                var rng = new Random(seed);
                var draws = new Random(seed * 31);
                var disk = new SimDisk();
                var store = new LogStore(recover(null));
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    string Snapshot() => string.Join(" ", Enumerable.Range(1, (int)store.LastIndex).Select(i => store.At(i)).Select(e => $"{e.Index}:{e.Term.Value}:{Encoding.ASCII.GetString(e.Command)}"));
                    var states = new List<string> { Snapshot() };
                    var completed = 0;
                    var lowestTruncate = long.MaxValue;
                    var writes = rng.Next(2, 6);
                    for (var w = 1; w <= writes; w++)
                    {
                        var from = store.LastIndex > 0 && rng.Next(3) == 0 ? rng.Next(1, (int)store.LastIndex + 1) : 0;
                        if (from > 0 && store.TruncateFrom(from) is { } t)
                        {
                            disk.Issue(t, 0);
                            lowestTruncate = Math.Min(lowestTruncate, from);
                        }

                        var term = (cycle * 10) + w;
                        disk.Issue(store.Append(Enumerable.Range(0, rng.Next(1, 4)).Select(k => E(term, $"s{seed}c{cycle}w{w}k{k}")).ToList()), 0);
                        states.Add(Snapshot());
                        if (rng.Next(2) == 0)
                        {
                            while (disk.Pending.Count > 0)
                            {
                                disk.CompleteNext();
                            }

                            completed = w;
                            lowestTruncate = long.MaxValue;
                        }
                    }

                    if (disk.Pending.Any(p => p.Op is PersistTruncate))
                    {
                        Count("truncate-in-flight");
                    }

                    if (disk.Pending.Any(p => p.Op is PersistAppend))
                    {
                        Count("append-in-flight");
                    }

                    disk.Crash(mode, () => (ulong)draws.NextInt64());
                    var file = disk.Snapshot().TryGetValue(EntryLog.FileName, out var m) ? m.ToArray() : null;
                    var r = recover(file);
                    Count(r.Path.ToString());
                    var got = Show(r.Entries);
                    if (r.Path == RecoveryPath.Refused)
                    {
                        failures.Add($"{mode} seed {seed} cycle {cycle}: refused: {r.Detail}");
                        break;
                    }

                    // A prefix of the log after some write at or past the last completed one, keeping every
                    // completed entry below the lowest truncation in flight: only writes in flight may be
                    // lost, and in Raft a truncation removes only uncommitted entries.
                    var gotList = got.Length == 0 ? [] : got.Split(' ');
                    var durable = states[completed].Length == 0 ? [] : states[completed].Split(' ');
                    var kept = (int)Math.Min(durable.Length, lowestTruncate - 1);
                    if (!gotList.Take(kept).SequenceEqual(durable.Take(kept)) || !states.Skip(completed).Any(st =>
                    {
                        var s = st.Length == 0 ? [] : st.Split(' ');
                        return gotList.Length <= s.Length && s.Take(gotList.Length).SequenceEqual(gotList);
                    }))
                    {
                        failures.Add($"{mode} seed {seed} cycle {cycle}: recovered [{got}], a prefix of none of [{string.Join("] [", states.Skip(completed))}]");
                        break;
                    }

                    store = new LogStore(r);
                    if (store.CutTornTail is { } cut)
                    {
                        disk.Issue(cut, 0);
                        disk.CompleteNext();
                    }
                }
            }
        }

        return (failures, counts);
    }

    [Fact]
    public void EveryCrashDuringAWriteRecoversToAPrefixOfWhatWasWritten()
    {
        var (failures, counts) = CrashDuringWrites(EntryLog.Recover);
        var (control, _) = CrashDuringWrites(IndexFree);
        System.IO.File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "entry-log-crashes.txt"), string.Join("\n",
            "3 loss modes x 200 seeds x 3 crash-and-restart cycles",
            "counts: " + string.Join(", ", counts.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}")),
            $"index-free control (records read in order, index ignored): {control.Count} failure(s)",
            control.Count == 0 ? "" : "  first: " + control[0]) + "\n");

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(5)));
        foreach (var k in new[] { "TruncatedTornTail", "Clean", "truncate-in-flight", "append-in-flight" })
        {
            Assert.True(counts.GetValueOrDefault(k) > 0, $"no recovery or crash counted as {k}: the test did not reach that case");
        }

        Assert.True(control.Count > 0, "the index-free control never failed: a lost truncation never met a later append, so the test does not reach the case the index exists for");
    }

    /// <summary>The log as it stands, the snapshot's covered entries (its state, in these tests) and then the retained ones.</summary>
    private static string Logical(LogStore store) =>
        string.Join(" ", new[] { store.Snapshot is { } sn ? Encoding.ASCII.GetString(sn.State) : "" }
            .Concat(Enumerable.Range((int)store.BaseIndex + 1, (int)(store.LastIndex - store.BaseIndex)).Select(i => store.At(i)).Select(e => $"{e.Index}:{e.Term.Value}:{Encoding.ASCII.GetString(e.Command)}"))
            .Where(x => x.Length > 0));

    /// <summary>
    /// P7-05: the crash-during-write test with compactions. As <see cref="CrashDuringWrites"/>, and in
    /// some writes the log is also compacted to a random index (its state: the covered entries' text,
    /// so a recovery can be compared with what was written), the compaction's file and rename issued
    /// through the world's rename barrier (P7-00) or, for the control, without it. Every recovery must
    /// not refuse, must be a prefix of the log after some write since the last completed one, and must
    /// cover at least the last snapshot whose rename completed.
    /// </summary>
    private static (List<string> Failures, Dictionary<string, int> Counts) CrashDuringCompactions(bool barrier)
    {
        var failures = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(string k) => counts[k] = counts.GetValueOrDefault(k) + 1;
        foreach (var mode in new[] { DiskLoss.Pending, DiskLoss.Torn, DiskLoss.Reordered })
        {
            for (var seed = 1; seed <= 200; seed++)
            {
                var rng = new Random(seed);
                var draws = new Random(seed * 31);
                var disk = new SimDisk();
                var held = new List<Persist>();
                var store = new LogStore(EntryLog.Recover(null));
                var durableSnapshot = 0L;
                var renamedTo = new Dictionary<string, long>(StringComparer.Ordinal);

                // The world (P7-00, P7-05): a rename waits until every write issued before it is durable,
                // and every write after a rename waits until the rename is durable.
                bool RenamePending() => disk.Pending.Any(p => p.Op is PersistRename);
                void Release()
                {
                    while (held.Count > 0 && (held[0] is PersistRename ? disk.Pending.Count == 0 : !RenamePending()))
                    {
                        disk.Issue(held[0], 0);
                        held.RemoveAt(0);
                    }
                }

                void Issue(Persist op)
                {
                    if (barrier && (held.Count > 0 || op is PersistRename || RenamePending()))
                    {
                        held.Add(op);
                        Release();
                    }
                    else
                    {
                        disk.Issue(op, 0);
                    }
                }

                void Complete(int max)
                {
                    for (var k = 0; k < max && (disk.Pending.Count > 0 || held.Count > 0); k++)
                    {
                        if (disk.Pending.Count > 0 && disk.CompleteNext().Op is PersistRename done)
                        {
                            durableSnapshot = Math.Max(durableSnapshot, renamedTo[done.File]);
                        }

                        Release();
                    }
                }

                for (var cycle = 0; cycle < 3; cycle++)
                {
                    var states = new List<string> { Logical(store) };
                    var completed = 0;
                    var lowestTruncate = long.MaxValue;
                    var writes = rng.Next(2, 6);
                    for (var w = 1; w <= writes; w++)
                    {
                        var from = store.LastIndex > store.BaseIndex && rng.Next(3) == 0 ? rng.Next((int)store.BaseIndex + 1, (int)store.LastIndex + 1) : 0;
                        if (from > 0 && store.TruncateFrom(from) is { } t)
                        {
                            Issue(t);
                            lowestTruncate = Math.Min(lowestTruncate, from);
                        }

                        var term = (cycle * 10) + w;
                        Issue(store.Append(Enumerable.Range(0, rng.Next(1, 4)).Select(k => E(term, $"s{seed}c{cycle}w{w}k{k}")).ToList()));
                        if (rng.Next(2) == 0)
                        {
                            var index = rng.Next((int)store.BaseIndex + 1, (int)store.LastIndex + 1);
                            var covered = Logical(store).Split(' ').Take(index);
                            var ops = store.Compact(index, null, Encoding.ASCII.GetBytes(string.Join(" ", covered)));
                            renamedTo[ops[0].File] = index;
                            Count("compactions");
                            foreach (var op in ops)
                            {
                                Issue(op);
                            }
                        }

                        states.Add(Logical(store));
                        if (rng.Next(2) == 0)
                        {
                            Complete(int.MaxValue);
                            completed = w;
                            lowestTruncate = long.MaxValue;
                        }
                    }

                    // Some of what is in flight completes before the crash: a crash can land between a
                    // compaction's file and its rename, or between the rename and what follows it.
                    Complete(rng.Next(disk.Pending.Count + held.Count + 1));
                    Count("crashes");
                    Count(disk.Pending.Any(p => p.Op is PersistRename) ? "rename-in-flight" : "rename-not-in-flight");
                    if (disk.Pending.Any(p => p.Op is PersistAppend a && a.File != EntryLog.FileName))
                    {
                        Count("compaction-file-in-flight");
                    }

                    if (held.Any(op => op is PersistRename))
                    {
                        Count("rename-held");
                    }

                    held.Clear();
                    disk.Crash(mode, () => (ulong)draws.NextInt64());
                    var files = disk.Snapshot();
                    var r = EntryLog.Recover(files.TryGetValue(EntryLog.FileName, out var m) ? m.ToArray() : null);
                    if (r.Path == RecoveryPath.Refused)
                    {
                        failures.Add($"{mode} seed {seed} cycle {cycle}: refused: {r.Detail}");
                        break;
                    }

                    store = new LogStore(r);
                    store.AvoidNames(files.Keys);
                    var got = Logical(store);
                    var gotList = got.Length == 0 ? [] : got.Split(' ');
                    var durable = states[completed].Length == 0 ? [] : states[completed].Split(' ');
                    var kept = (int)Math.Min(durable.Length, lowestTruncate - 1);
                    if (store.BaseIndex < durableSnapshot)
                    {
                        failures.Add($"{mode} seed {seed} cycle {cycle}: recovered a snapshot of {store.BaseIndex}, below the last durable one, of {durableSnapshot}: [{got}]");
                        break;
                    }

                    if (!gotList.Take(kept).SequenceEqual(durable.Take(kept)) || !states.Skip(completed).Any(st =>
                    {
                        var s = st.Length == 0 ? [] : st.Split(' ');
                        return gotList.Length <= s.Length && s.Take(gotList.Length).SequenceEqual(gotList);
                    }))
                    {
                        failures.Add($"{mode} seed {seed} cycle {cycle}: recovered [{got}], a prefix of none of [{string.Join("] [", states.Skip(completed))}]");
                        break;
                    }

                    durableSnapshot = store.BaseIndex;
                    if (store.CutTornTail is { } cut)
                    {
                        disk.Issue(cut, 0);
                        disk.CompleteNext();
                    }
                }
            }
        }

        return (failures, counts);
    }

    /// <summary>
    /// P7-05: every crash during a compaction recovers a prefix of something written and never less
    /// than the last durable snapshot covered, with the barrier. Without it (the P7-00 control) some
    /// crash keeps a rename and loses the file it renames. Vacuity risk: crashes that never land
    /// during a compaction (guarded: crashes with the compaction's file in flight and with the rename
    /// in flight are both counted and required). Sabotages S-snapfile-1, S-snapfile-2.
    /// </summary>
    [Fact]
    public void EveryCrashDuringACompactionRecoversAPrefixAndNeverLessThanTheDurableSnapshot()
    {
        var (failures, counts) = CrashDuringCompactions(barrier: true);
        var (control, _) = CrashDuringCompactions(barrier: false);
        System.IO.File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "entry-log-compaction-crashes.txt"), string.Join("\n",
            "3 loss modes x 200 seeds x 3 crash-and-restart cycles, compactions in about half the writes",
            "counts: " + string.Join(", ", counts.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}")),
            $"without the barrier: {control.Count} failure(s)",
            control.Count == 0 ? "" : "  first: " + control[0]) + "\n");

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(5)));
        foreach (var k in new[] { "compactions", "compaction-file-in-flight", "rename-in-flight" })
        {
            Assert.True(counts.GetValueOrDefault(k) > 0, $"no crash counted as {k}: the test did not reach that case");
        }

        Assert.True(control.Count > 0, "without the barrier no crash lost a compaction's file and kept its rename: the control shows nothing");
    }

    /// <summary>A compacted log recovers from its file as it was, and appends and truncations after the compaction reach the new file.</summary>
    [Fact]
    public void ACompactedLogRecoversAndTakesLaterWrites()
    {
        var disk = new SimDisk();
        var store = new LogStore(EntryLog.Recover(null));
        disk.Issue(store.Append([E(1, "a"), E(1, "b"), E(2, "c"), E(2, "d")]), 0);
        foreach (var op in store.Compact(2, null, Encoding.ASCII.GetBytes("ab")))
        {
            disk.Issue(op, 0);
        }

        disk.Issue(store.TruncateFrom(4)!, 0);
        disk.Issue(store.Append([E(3, "D"), E(3, "e")]), 0);
        while (disk.Pending.Count > 0)
        {
            disk.CompleteNext();
        }

        var r = EntryLog.Recover(disk.Snapshot()[EntryLog.FileName].ToArray());
        Assert.Equal(RecoveryPath.Clean, r.Path);
        Assert.Equal((2L, new Term(1), "ab"), (r.Snapshot!.Index, r.Snapshot.Term, Encoding.ASCII.GetString(r.Snapshot.State)));
        Assert.Equal("3:2:c 4:3:D 5:3:e", Show(r.Entries));
        Assert.Equal(new[] { EntryLog.FileName }, disk.Snapshot().Keys);
        var recovered = new LogStore(r);
        Assert.Equal((5L, new Term(3), new Term(1)), (recovered.LastIndex, recovered.LastTerm, recovered.TermAt(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => recovered.TermAt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => recovered.TruncateFrom(2));
    }

    /// <summary>A compaction's file is named after none of the files the node held at its start.</summary>
    [Fact]
    public void ACompactionsFileIsNamedAfterNoFileTheNodeHeld()
    {
        var store = new LogStore(EntryLog.Recover(null));
        store.Append([E(1, "a"), E(1, "b")]);
        store.AvoidNames([EntryLog.FileName, "entries.0", "entries.7", "entries.x", "termvote.log"]);
        var first = store.Compact(1, null, []);
        var second = store.Compact(2, null, []);
        Assert.Equal("entries.8", first[0].File);
        Assert.Equal("entries.9", second[0].File);
    }

    /// <summary>The control: the same framing, read in order with each record's index ignored.</summary>
    private static EntryLogRecovery IndexFree(byte[]? file)
    {
        var r = EntryLog.Recover(file is null ? null : Renumbered(file));
        return r;
    }

    /// <summary>Rewrites every whole record's index to its position and its previous term to the one before it, as a format without indices (or chaining) would read it.</summary>
    private static byte[] Renumbered(byte[] file)
    {
        var copy = new List<byte>();
        var at = 0;
        var position = 0L;
        var previousTerm = 0L;
        while (at + 4 <= file.Length)
        {
            var length = (file[at] << 24) | (file[at + 1] << 16) | (file[at + 2] << 8) | file[at + 3];
            var size = 4 + length + 4;
            if (length < 24 || at + size > file.Length)
            {
                break;
            }

            var index = 0L;
            for (var i = 0; i < 8; i++)
            {
                index = (index << 8) | file[at + 4 + i];
            }

            var term = 0L;
            for (var i = 0; i < 8; i++)
            {
                term = (term << 8) | file[at + 12 + i];
            }

            var body = file.AsSpan(at + 28, length - 24).ToArray();
            copy.AddRange(EntryLog.Record(++position, new Term(term), new Term(previousTerm), body));
            previousTerm = term;
            at += size;
        }

        copy.AddRange(file.Skip(at));
        return copy.ToArray();
    }

    /// <summary>
    /// P6-15: the entry log's form of the term-vote log's case. A node recovers a torn final entry,
    /// issues the cut and then the entry that replaces it, and a reordered loss keeps the entry and
    /// loses the cut: the entry sits after the torn bytes. Recovery skips them and takes the entry, at
    /// every torn length. Sabotage S-logfile-4.
    /// </summary>
    [Fact]
    public void ATornEntryFollowedByAnEntryWhoseCutWasLostRecoversTheEntry()
    {
        var size = EntryLog.Record(2, new Term(1), new Term(1), Encoding.ASCII.GetBytes("b")).Length;
        for (var keep = 1; keep < size; keep++)
        {
            var disk = new SimDisk();
            var store = new LogStore(EntryLog.Recover(null));
            disk.Issue(store.Append([E(1, "a")]), 0);
            disk.CompleteNext();
            disk.Issue(store.Append([E(1, "b")]), 0);
            disk.Crash(DiskLoss.Torn, () => (ulong)(keep - 1));
            var torn = disk.Snapshot()[EntryLog.FileName].ToArray();
            var restarted = new LogStore(EntryLog.Recover(torn));
            Assert.Equal(1, restarted.LastIndex);
            Assert.NotNull(restarted.CutTornTail);

            disk.Issue(restarted.CutTornTail!, 0);
            disk.Issue(restarted.Append([E(2, "c")]), 0);
            var draws = new Queue<ulong>([0, 1]);
            disk.Crash(DiskLoss.Reordered, draws.Dequeue);
            var file = disk.Snapshot()[EntryLog.FileName].ToArray();
            Assert.Equal(torn.Length + EntryLog.Record(2, new Term(2), new Term(1), Encoding.ASCII.GetBytes("c")).Length, file.Length);

            var r = EntryLog.Recover(file);
            Assert.True(r.Path != RecoveryPath.Refused, $"torn at {keep}: refused: {r.Detail}");
            Assert.Equal("1:1:a 2:2:c", Show(r.Entries));
        }
    }
}
