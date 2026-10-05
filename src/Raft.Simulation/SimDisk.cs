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
    /// <summary>
    /// What the last completed write changed, file by file, for the positive control to undo. A list:
    /// a rename changes two files (P2-03: an undo record of one file left the destination behind).
    /// </summary>
    private List<(string File, byte[]? Before)>? _lastCompleted;
    private long _lastCompleteAt;

    public long IssuedCount { get; private set; }

    public long CompletedCount { get; private set; }

    public IReadOnlyList<PendingWrite> Pending => _pending;

    public sealed record PendingWrite(long Seq, Persist Op, long CompleteAt);

    /// <summary>Durable contents, copied, as a restarted node sees them.</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Snapshot() =>
        _durable.ToDictionary(kv => kv.Key, kv => new ReadOnlyMemory<byte>((byte[])kv.Value.Clone()), StringComparer.Ordinal);

    /// <summary>
    /// Issues a write. Writes complete in issue order, so a write never completes before one issued
    /// earlier (a per-write latency draw alone would allow that, and the later write would then
    /// never complete).
    /// </summary>
    public PendingWrite Issue(Persist op, long earliestCompletion)
    {
        _lastCompleteAt = Math.Max(earliestCompletion, _lastCompleteAt);
        var w = new PendingWrite(++IssuedCount, op, _lastCompleteAt);
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

    /// <summary>
    /// A crash: what becomes of the writes in flight (docs/design/node-interface.md §4), and, for the
    /// positive control only, of the last completed one. Returns a description for the trace.
    /// </summary>
    public IReadOnlyList<(string Key, object Value)> Crash(DiskLoss mode, Func<ulong> draw)
    {
        var pending = _pending.ToList();
        _pending.Clear();
        CompletedCount = IssuedCount;
        switch (mode)
        {
            case DiskLoss.Pending:
                return [("lost", pending.Count)];
            case DiskLoss.Torn:
                if (pending.Count > 0 && Data(pending[0].Op) is { Length: >= 2 } data)
                {
                    var keep = 1 + (int)(draw() % (ulong)(data.Length - 1));
                    Apply(pending[0].Op switch
                    {
                        PersistAppend a => a with { Data = data[..keep] },
                        PersistWriteAt w => w with { Data = data[..keep] },
                        var other => other,
                    });
                    return [("torn", pending[0].Seq), ("kept", keep), ("of", data.Length), ("lost", pending.Count - 1)];
                }

                return [("torn", "none"), ("lost", pending.Count)];
            case DiskLoss.Reordered:
                var survivors = pending.Where(_ => (draw() & 1) == 1).ToList();
                foreach (var w in survivors)
                {
                    Apply(w.Op);
                }

                return [("survived", Seqs(survivors)), ("pending", Seqs(pending))];
            case DiskLoss.LoseSynced:
                if (_lastCompleted is { } last)
                {
                    foreach (var (file, before) in last)
                    {
                        if (before is null)
                        {
                            _durable.Remove(file);
                        }
                        else
                        {
                            _durable[file] = before;
                        }
                    }

                    _lastCompleted = null;
                    return [("lostsynced", string.Join(',', last.Select(x => x.File))), ("lost", pending.Count)];
                }

                return [("lostsynced", "none"), ("lost", pending.Count)];
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static string Seqs(List<PendingWrite> ws) => ws.Count == 0 ? "none" : string.Join(',', ws.Select(w => w.Seq));

    private static ReadOnlyMemory<byte>? Data(Persist op) => op switch
    {
        PersistAppend a => a.Data,
        PersistWriteAt w => w.Data,
        _ => null,
    };

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
        byte[]? Before(string file) => _durable.TryGetValue(file, out var b) ? b : null;
        _lastCompleted = new List<(string File, byte[]? Before)> { (op.File, Before(op.File)) };
        if (op is PersistRename rn)
        {
            _lastCompleted.Add((rn.To, Before(rn.To)));
        }

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
                // A rename whose source data never became durable leaves an empty file under the
                // real name (P7-00): a real file system can make the rename durable without the data,
                // and the model may be stricter than reality, never laxer. Until P7-00 this was a
                // no-op, a state no real crash is limited to.
                _durable[r.To] = _durable.Remove(r.File, out var content) ? content : [];
                break;
            case PersistTruncate t:
                if (_durable.TryGetValue(t.File, out var cur) && cur.Length > t.Length)
                {
                    _durable[t.File] = cur[..(int)t.Length];
                }

                break;
            case PersistDelete d:
                _durable.Remove(d.File);
                break;
            default:
                throw new ArgumentException("unknown persist " + op.GetType().Name);
        }
    }
}
