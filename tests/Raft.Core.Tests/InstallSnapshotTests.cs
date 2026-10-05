using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Kv;
using Raft.Simulation;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P7-07: InstallSnapshot (Figure 13, phase 7 decision 4). A follower is fed a leader's chunks by
/// hand, reordered, duplicated, lost and stale, and its disk is read back after each; then a
/// follower cut off across two compactions catches up in a cluster. Vacuity risks: chunks that
/// always arrive in order and once never meet the cases (guarded: each construction names its
/// disorder); a follower that never falls behind a snapshot never receives one (guarded: the
/// cluster construction asserts the install). Sabotages S-install-1, S-install-2.
/// </summary>
public sealed class InstallSnapshotTests
{
    private const int Chunk = 8;
    private static readonly Term T1 = new(1);

    /// <summary>A follower fed by hand, its writes applied to a disk of its own as they are issued.</summary>
    private sealed class Follower
    {
        private sealed class ZeroRandom : IRandomSource
        {
            public ulong NextUInt64() => 0;
        }

        public RaftNode Node { get; } = new(new NodeContext(N2, [N1, N3], new ZeroRandom(), new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)), RaftOptions.Default with { SnapshotThreshold = 100 }, new KvStateMachine());

        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public List<Message> Replies { get; } = [];

        public void Receive(Message m)
        {
            foreach (var e in Node.Handle(new Receive(N1, MessageCodec.Encode(m))))
            {
                switch (e)
                {
                    case PersistAppend a:
                        Files[a.File] = [.. Files.GetValueOrDefault(a.File, []), .. a.Data.Span];
                        break;
                    case PersistWriteAt w:
                        var at = Files.GetValueOrDefault(w.File, []);
                        var grown = new byte[Math.Max(at.Length, w.Offset + w.Data.Length)];
                        at.CopyTo(grown, 0);
                        w.Data.Span.CopyTo(grown.AsSpan((int)w.Offset));
                        Files[w.File] = grown;
                        break;
                    case PersistTruncate t when Files.TryGetValue(t.File, out var cur) && cur.Length > t.Length:
                        Files[t.File] = cur[..(int)t.Length];
                        break;
                    case PersistRename r:
                        Files[r.To] = Files.Remove(r.File, out var renamed) ? renamed : [];
                        break;
                    case Send s:
                        Replies.Add(MessageCodec.Decode(s.Payload.ToArray())!);
                        break;
                }
            }
        }

        public EntryLogRecovery Log => EntryLog.Recover(Files.GetValueOrDefault(EntryLog.FileName));
    }

    /// <summary>A key-value state holding <paramref name="value"/> under one key, as a snapshot's bytes.</summary>
    private static byte[] State(string value)
    {
        var kv = new KvStateMachine();
        kv.Apply(Encoding.ASCII.GetBytes("Put|k|" + value));
        return kv.Snapshot().ToArray();
    }

    private static byte[] Record(long index, string value) => EntryLog.SnapshotRecord(index, T1, null, State(value));

    private static List<InstallSnapshot> Chunks(long index, byte[] record) =>
        Enumerable.Range(0, (record.Length + Chunk - 1) / Chunk)
            .Select(k => new InstallSnapshot(T1, N1, index, T1, k * Chunk, record.Skip(k * Chunk).Take(Chunk).ToArray(), (k + 1) * Chunk >= record.Length))
            .ToList();

    private static LogEntry Put(string k, string v) => new(T1, Encoding.ASCII.GetBytes($"Put|{k}|{v}"));

    /// <summary>
    /// The prediction's construction: a chunk of an earlier snapshot, delayed, arrives after a newer
    /// snapshot's first chunks. The newer snapshot must install on its last chunk, with its own bytes
    /// on disk.
    /// </summary>
    [Fact]
    public void AStaleChunkOfAnEarlierSnapshotIsNotWrittenIntoANewerOne()
    {
        var f = new Follower();
        var older = Chunks(3, Record(3, "state-of-the-older-snapshot"));
        var newer = Chunks(6, Record(6, "state-of-the-newer-snapshot"));
        // Bytes 20 to 28 of a record hold its index: the older snapshot's chunk there differs from the newer one's.
        Assert.NotEqual(older[3].Data, newer[3].Data);
        f.Receive(older[0]);
        f.Receive(newer[0]);
        f.Receive(newer[1]);
        f.Receive(newer[2]);
        f.Receive(newer[3]);
        f.Receive(older[3]);
        foreach (var c in newer.Skip(4))
        {
            f.Receive(c);
        }

        Assert.Equal(new InstallSnapshotResponse(T1, 6, Record(6, "state-of-the-newer-snapshot").Length, true), f.Replies[^1]);
        Assert.Equal(6L, f.Log.Snapshot!.Index);
        Assert.Equal(State("state-of-the-newer-snapshot"), f.Log.Snapshot.State);
    }

    /// <summary>
    /// Chunks duplicated, one lost and sent again, one ahead of a gap: the gap's chunk is dropped and
    /// the reply says where the held bytes end, and the snapshot installs once every byte arrived, its
    /// record on disk whole. Sabotage S-install-2.
    /// </summary>
    [Fact]
    public void ChunksDuplicatedLostAndOutOfOrderInstallTheSnapshotWhole()
    {
        var f = new Follower();
        var record = Record(5, "a-state-long-enough-for-several-chunks");
        var chunks = Chunks(5, record);
        Assert.True(chunks.Count >= 4);
        f.Receive(chunks[0]);
        f.Receive(chunks[0]);
        f.Receive(chunks[2]);
        Assert.Equal(new InstallSnapshotResponse(T1, 5, Chunk, false), f.Replies[^1]);
        f.Receive(chunks[1]);
        f.Receive(chunks[1]);
        foreach (var c in chunks.Skip(2))
        {
            f.Receive(c);
        }

        Assert.True(f.Replies[^1] is InstallSnapshotResponse { Done: true, LastIncludedIndex: 5 }, f.Replies[^1].ToString());
        Assert.Equal(record, f.Files[EntryLog.FileName]);
        Assert.Equal([EntryLog.FileName, TermVoteLog.FileName], f.Files.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Figure 13: a follower whose log holds the snapshot's last entry keeps the entries after it,
    /// among them entries it knows are committed. Sabotage S-install-1.
    /// </summary>
    [Fact]
    public void AFollowerHoldingTheSnapshotsLastEntryKeepsTheEntriesAfterIt()
    {
        var f = new Follower();
        var entries = Enumerable.Range(1, 8).Select(i => Put("k" + i, "v" + i)).ToList();
        f.Receive(new AppendEntries(T1, N1, 0, Term.Zero, entries, 7));
        Assert.Equal(8, f.Log.Entries.Count);

        var state = new KvStateMachine();
        foreach (var e in entries.Take(6))
        {
            state.Apply(e.Command);
        }

        foreach (var c in Chunks(6, EntryLog.SnapshotRecord(6, T1, null, state.Snapshot().ToArray())))
        {
            f.Receive(c);
        }

        Assert.True(f.Replies[^1] is InstallSnapshotResponse { Done: true }, f.Replies[^1].ToString());
        Assert.Equal(6, f.Log.Snapshot!.Index);
        Assert.Equal([7L, 8L], f.Log.Entries.Select(e => e.Index));
    }

    /// <summary>
    /// A follower whose log disagrees with the snapshot at its last entry discards its log: the
    /// snapshot is committed and its entries are not.
    /// </summary>
    [Fact]
    public void AFollowerWhoseLogDisagreesWithTheSnapshotDiscardsIt()
    {
        var f = new Follower();
        f.Receive(new AppendEntries(T1, N1, 0, Term.Zero, Enumerable.Range(1, 8).Select(i => Put("k" + i, "v" + i)).ToList(), 2));
        var t2 = new Term(2);
        var record = EntryLog.SnapshotRecord(6, t2, null, new KvStateMachine().Snapshot().ToArray());
        foreach (var c in Chunks(6, record).Select(c => c with { Term = t2, LastIncludedTerm = t2 }))
        {
            f.Receive(c);
        }

        Assert.True(f.Replies[^1] is InstallSnapshotResponse { Done: true }, f.Replies[^1].ToString());
        Assert.Equal((6L, t2), (f.Log.Snapshot!.Index, f.Log.Snapshot.Term));
        Assert.Empty(f.Log.Entries);
    }

    /// <summary>
    /// In a cluster: n3, cut off while the others compact twice, is sent the snapshot when it comes
    /// back, installs it, and then takes the entries after it by AppendEntries; a read at n3's term as
    /// leader sees what the snapshot covered.
    /// </summary>
    [Fact]
    public void AFollowerCutOffAcrossTwoCompactionsCatchesUpByInstallingTheSnapshot()
    {
        var c = new ManualCluster(RaftOptions.Default with { SnapshotThreshold = 3, SnapshotChunkBytes = 16 });
        c.Stand(N1);
        c.Drop(N1, N3);
        c.Settle(N1, N2);
        for (var i = 0; i < 6; i++)
        {
            c.Client(N1, $"Put|k{i}|v{i}");
            c.Drop(N1, N3);
            c.Tick(N1, 50);
            c.Drop(N1, N3);
            c.Settle(N1, N2);
        }

        Assert.True(c.SnapshotOf(N1)?.Index >= 6, "n1 did not compact twice");
        Assert.Null(c.SnapshotOf(N3));
        for (var i = 0; i < 40; i++)
        {
            c.Tick(N1, 50);
            c.Settle(All);
        }

        Assert.Equal(c.SnapshotOf(N1)?.Index, c.SnapshotOf(N3)?.Index);
        Assert.Contains(c.Observations.OfType<EmittedObservation>(), o => o.Node == N3 && o.Event.Name == "restore");
        var (elections, log) = c.Histories();
        foreach (var r in LogInvariants.All(log))
        {
            Assert.True(r.Holds, $"{r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
        }

        Assert.True(ElectionInvariants.ElectionSafety(elections, 10_000).Holds);
    }
}
