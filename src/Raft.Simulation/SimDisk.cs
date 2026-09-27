using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Core;

namespace Raft.Simulation;

/// <summary>
/// One node's disk (docs/design/node-interface.md §4): durable file contents, and writes in flight.
/// A write is issued at one time and completes — becomes durable — at a later one; until then a crash
/// can lose it. Writes complete in issue order (later tasks add the fault modes).
/// </summary>
public sealed class SimDisk
{
    private readonly SortedDictionary<string, byte[]> _durable = new(StringComparer.Ordinal);
    private readonly List<PendingWrite> _pending = [];

    public long IssuedCount { get; private set; }

    public long CompletedCount { get; private set; }

    public IReadOnlyList<PendingWrite> Pending => _pending;

    public sealed record PendingWrite(long Seq, Persist Op, long CompleteAt);

    /// <summary>Durable contents, copied, as a restarted node sees them.</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Snapshot() =>
        _durable.ToDictionary(kv => kv.Key, kv => new ReadOnlyMemory<byte>((byte[])kv.Value.Clone()), StringComparer.Ordinal);

    public PendingWrite Issue(Persist op, long completeAt)
    {
        var w = new PendingWrite(++IssuedCount, op, completeAt);
        _pending.Add(w);
        return w;
    }

    /// <summary>Completes the oldest pending write (writes complete in order).</summary>
    public PendingWrite CompleteNext()
    {
        var w = _pending[0];
        _pending.RemoveAt(0);
        Apply(w.Op);
        CompletedCount = w.Seq;
        return w;
    }

    /// <summary>On a crash: pending writes are lost.</summary>
    public int LosePending()
    {
        var n = _pending.Count;
        _pending.Clear();
        CompletedCount = IssuedCount;
        return n;
    }

    public ulong Digest()
    {
        var h = Mix.Hash("disk");
        foreach (var (name, bytes) in _durable)
        {
            h = Mix.Combine(h, Mix.Hash(name));
            h = Mix.Combine(h, HashBytes(bytes));
        }

        return h;
    }

    public static ulong HashBytes(ReadOnlySpan<byte> bytes)
    {
        var h = 0xcbf29ce484222325UL;
        foreach (var b in bytes)
        {
            h = (h ^ b) * 0x100000001b3UL;
        }

        return h;
    }

    private void Apply(Persist op)
    {
        switch (op)
        {
            case PersistAppend a:
                _durable[a.File] = [.. _durable.GetValueOrDefault(a.File, []), .. a.Data.Span];
                break;
            case PersistWriteAt w:
                var old = _durable.GetValueOrDefault(w.File, []);
                var length = Math.Max(old.Length, w.Offset + w.Data.Length);
                var buf = new byte[length];
                old.CopyTo(buf, 0);
                w.Data.Span.CopyTo(buf.AsSpan((int)w.Offset));
                _durable[w.File] = buf;
                break;
            case PersistRename r:
                if (_durable.Remove(r.File, out var content))
                {
                    _durable[r.To] = content;
                }

                break;
            case PersistDelete d:
                _durable.Remove(d.File);
                break;
            default:
                throw new ArgumentException($"unknown persist {op.GetType().Name}");
        }
    }
}
