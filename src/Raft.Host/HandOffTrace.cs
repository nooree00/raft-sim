using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Raft.Host;

/// <summary>
/// P12-03, spec §9: the timestamps a host takes where it hands a write from one thread or socket to
/// the next, in bench mode only. Each stamp is the kernel's monotonic clock (<see
/// cref="Stopwatch.GetTimestamp"/>, comparable across processes on one machine) and a reference to
/// bytes the host already holds; nothing is decoded while the cluster runs, so the trace costs an
/// allocation and an enqueue per hand-off. <see cref="Decomposition"/> reads it after the run. No
/// stamp is taken inside <c>RaftNode</c>.
/// </summary>
public sealed class HandOffTrace
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

    public void Add(Kind kind, long id, object? payload) => _stamps.Enqueue(new Stamp(Stopwatch.GetTimestamp(), kind, id, payload));

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
