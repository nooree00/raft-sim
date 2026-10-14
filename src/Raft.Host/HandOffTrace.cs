using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Raft.Core;

namespace Raft.Host;

/// <summary>
/// P12-03, spec §9: the timestamps a host takes where it hands a write from one thread or socket to
/// the next, in bench mode only. Each stamp is the kernel's monotonic clock (<see
/// cref="Stopwatch.GetTimestamp"/>, comparable across processes on one machine) and a reference to
/// bytes the host already holds; nothing is decoded while the cluster runs, so the trace costs an
/// allocation and an enqueue per hand-off. <see cref="Decomposition"/> reads it after the run. No
/// stamp is taken inside <c>RaftNode</c>.
/// With <paramref name="sample"/> above 1 (P15-01) the trace keeps one write in that many, by entry
/// index, so that what it holds does not grow the hosts' heap with every message of the run: client
/// stamps, and the durable appends, appends, first-covering answers and takes that concern an index the
/// sample keeps. A sampled write decomposes exactly as it would in a full trace; the others are not joined.
/// </summary>
public sealed class HandOffTrace(int sample = 1)
{
    /// <summary>Where a stamp was taken.</summary>
    public enum Kind
    {
        /// <summary>A client's line read from its socket (Id: the request id; Payload: the line's bytes).</summary>
        ClientRead,

        /// <summary>A client request taken by the loop (Id: the request id).</summary>
        ClientTaken,

        /// <summary>A peer's frame read from its socket (Id: the peer; Payload: the message's bytes).</summary>
        PeerRead,

        /// <summary>A peer's message taken by the loop (Id: the peer; Payload: the same array as its read).</summary>
        PeerTaken,

        /// <summary>A frame written to a peer's socket (Id: the peer; Payload: the frame).</summary>
        Sent,

        /// <summary>An append to the entry log made durable (Payload: the appended records).</summary>
        Durable,

        /// <summary>A response written and flushed to its client (Id: the request id).</summary>
        Responded,
    }

    /// <summary>One stamp: when, where, which request or peer, and the bytes it concerns.</summary>
    public sealed record Stamp(long At, Kind Kind, long Id, object? Payload);

    private readonly ConcurrentQueue<Stamp> _stamps = new();
    private readonly ConcurrentDictionary<byte[], byte> _keptReads = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<(Kind Kind, long Peer), long> _answered = new();

    /// <summary>One write in this many is traced, by entry index (1: every write).</summary>
    public int Sample { get; } = Math.Max(1, sample);

    public void Add(Kind kind, long id, object? payload)
    {
        if (Sample == 1 || Keeps(kind, id, payload))
        {
            _stamps.Enqueue(new Stamp(Stopwatch.GetTimestamp(), kind, id, payload));
        }
    }

    /// <summary>A stamp taken elsewhere (a recorded trace replayed through the sample), kept as <see cref="Add"/> would keep it.</summary>
    public void Offer(Stamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (Sample == 1 || Keeps(stamp.Kind, stamp.Id, stamp.Payload))
        {
            _stamps.Enqueue(stamp);
        }
    }

    /// <summary>
    /// Whether the sample keeps a stamp (P15-01): every client stamp (a write's index is not known when its
    /// line is read, and the three are small); a durable append, or an append, that carries an index the
    /// sample keeps; a successful answer that is the first from its peer to cover one (as the
    /// decomposition's join reads answers); and the loop's take of a read the sample kept. Sabotage S-proc-3.
    /// </summary>
    private bool Keeps(Kind kind, long id, object? payload)
    {
        switch (kind)
        {
            case Kind.ClientRead or Kind.ClientTaken or Kind.Responded:
                return true;
            case Kind.Durable:
                return payload is ReadOnlyMemory<byte> records && DurableKeeps(records.Span);
            case Kind.PeerTaken:
                return payload is byte[] array && _keptReads.TryRemove(array, out _);
            case Kind.PeerRead:
                var read = payload as byte[];
                var keep = MessageKeeps(kind, id, read);
                if (keep && read is not null)
                {
                    _keptReads[read] = 0;
                }

                return keep;
            case Kind.Sent:
                return payload is byte[] { Length: >= 8 } frame && MessageKeeps(kind, id, frame[4..^4]);
            default:
                return false;
        }
    }

    /// <summary>Whether an index in (low, high] is one the sample keeps.</summary>
    private bool Covers(long low, long high) => high > low && high / Sample > low / Sample;

    private bool MessageKeeps(Kind kind, long peer, byte[]? message)
    {
        if (message is null)
        {
            return false;
        }

        switch (MessageCodec.Decode(message))
        {
            case AppendEntries ae when ae.Entries.Count > 0:
                return Covers(ae.PrevLogIndex, ae.PrevLogIndex + ae.Entries.Count);
            case AppendEntriesResponse { Success: true } r:
                var high = _answered.GetValueOrDefault((kind, peer));
                if (r.MatchIndex <= high)
                {
                    return false;
                }

                _answered[(kind, peer)] = r.MatchIndex;
                return Covers(high, r.MatchIndex);
            default:
                return false;
        }
    }

    /// <summary>Whether an entry-log append holds a record whose index the sample keeps (EntryLog's framing: length, index, ...).</summary>
    private bool DurableKeeps(ReadOnlySpan<byte> data)
    {
        for (var off = 0; off + 12 <= data.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data[off..]);
            if (length < 24 || off + 4 + length + 4 > data.Length)
            {
                return false;
            }

            var index = BinaryPrimitives.ReadInt64BigEndian(data[(off + 4)..]);
            if (index > 0 && index % Sample == 0)
            {
                return true;
            }

            off += 4 + length + 4;
        }

        return false;
    }

    /// <summary>Every stamp so far, in the order they were taken (within a thread; across threads by their clock).</summary>
    public IReadOnlyList<Stamp> Snapshot() => [.. _stamps];

    /// <summary>
    /// Every stamp so far, removed: one run's stamps, when a cluster serves several (the generator
    /// numbers each run's writes from 0, and the join names a write by its number).
    /// </summary>
    public IReadOnlyList<Stamp> Drain()
    {
        var taken = new List<Stamp>();
        while (_stamps.TryDequeue(out var s))
        {
            taken.Add(s);
        }

        return taken;
    }

    /// <summary>
    /// Stamps as bytes, for a trace that leaves its process (a Compose node's, fetched with `Trace|`;
    /// the in-process bench's goes through the same pair, so both paths join what <see cref="Read"/>
    /// returns). An array referenced by two stamps (a peer message's read and its take) is written once
    /// and comes back as one array, which is how the join pairs them.
    /// </summary>
    public static byte[] Write(IReadOnlyList<Stamp> stamps)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        using var buffer = new MemoryStream();
        using (var w = new BinaryWriter(buffer))
        {
            var arrays = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
            w.Write(stamps.Count);
            foreach (var s in stamps)
            {
                w.Write(s.At);
                w.Write((byte)s.Kind);
                w.Write(s.Id);
                switch (s.Payload)
                {
                    case byte[] a when arrays.TryGetValue(a, out var seen):
                        w.Write((byte)2);
                        w.Write(seen);
                        break;
                    case byte[] a:
                        arrays[a] = arrays.Count;
                        w.Write((byte)1);
                        w.Write(a.Length);
                        w.Write(a);
                        break;
                    case ReadOnlyMemory<byte> m:
                        w.Write((byte)3);
                        w.Write(m.Length);
                        w.Write(m.Span);
                        break;
                    default:
                        w.Write((byte)0);
                        break;
                }
            }
        }

        return buffer.ToArray();
    }

    /// <summary>The stamps <see cref="Write"/> wrote, arrays shared as they were.</summary>
    public static IReadOnlyList<Stamp> Read(byte[] bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes));
        var arrays = new List<byte[]>();
        var stamps = new List<Stamp>(r.ReadInt32());
        for (var i = stamps.Capacity; i > 0; i--)
        {
            var at = r.ReadInt64();
            var kind = (Kind)r.ReadByte();
            var id = r.ReadInt64();
            // Each arm typed object: their common type would be ReadOnlyMemory, and every array would come back as one.
            object? payload = r.ReadByte() switch
            {
                0 => null,
                1 => (object)Added(arrays, r.ReadBytes(r.ReadInt32())),
                2 => (object)arrays[r.ReadInt32()],
                3 => (object)new ReadOnlyMemory<byte>(r.ReadBytes(r.ReadInt32())),
                var t => throw new InvalidDataException($"a trace payload tagged {t}"),
            };
            stamps.Add(new Stamp(at, kind, id, payload));
        }

        return stamps;
    }

    private static byte[] Added(List<byte[]> arrays, byte[] array)
    {
        arrays.Add(array);
        return array;
    }
}
