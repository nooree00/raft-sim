using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Core;
using Raft.Simulation;

namespace Raft.Core.Tests;

/// <summary>
/// P7-09: what compaction did in an execution, each an effect read from the disk of the node it
/// happened to (phase 6's lesson, the isolation detector: a dimension counted from the party that
/// decided it measures the decision, not the effect). An install is counted when the follower's
/// disk took the rename of a file its chunks were written to, never when a leader sent a chunk.
/// Sabotage S-cov-13.
/// </summary>
internal static class CompactionCoverage
{
    /// <summary>The dimensions every soak measures (P7-09, P7-11).</summary>
    public static readonly string[] Dimensions =
    [
        "log-compacted", "snapshot-installed-by-a-follower", "restarted-from-a-snapshot", "crash-with-a-compaction-in-flight",
        "install-kept-the-suffix", "install-discarded-the-suffix",
    ];

    /// <summary>A snapshot whose configuration is joint: only a workload that changes membership can produce it.</summary>
    public const string Joint = "compaction-during-joint-consensus";

    /// <summary>The <see cref="Dimensions"/> (and <see cref="Joint"/>) one execution hit. A crash counts a compaction in flight when the node had decided one of its writes and the write was not durable.</summary>
    public static HashSet<string> Of(IReadOnlyList<Observation> observations)
    {
        var hit = new HashSet<string>(StringComparer.Ordinal);
        var disks = new Dictionary<NodeId, Dictionary<string, byte[]>>();
        var chunked = new Dictionary<NodeId, HashSet<string>>();
        var pending = new Dictionary<NodeId, List<Persist>>();
        Dictionary<string, byte[]> Disk(NodeId n) => disks.TryGetValue(n, out var d) ? d : disks[n] = new(StringComparer.Ordinal);
        HashSet<string> Chunked(NodeId n) => chunked.TryGetValue(n, out var c) ? c : chunked[n] = new(StringComparer.Ordinal);
        List<Persist> Pending(NodeId n) => pending.TryGetValue(n, out var p) ? p : pending[n] = [];

        foreach (var o in observations)
        {
            switch (o)
            {
                case IssuedObservation i when IsCompaction(i.Op):
                    Pending(i.Node).Add(i.Op);
                    if (i.Op is PersistWriteAt w)
                    {
                        Chunked(i.Node).Add(w.File);
                    }

                    break;
                case DurableObservation { Completed: { } op } d:
                    Pending(d.Node).Remove(op);
                    var disk = Disk(d.Node);
                    if (op is PersistRename r && r.To == EntryLog.FileName)
                    {
                        var before = EntryLog.Recover(disk.GetValueOrDefault(EntryLog.FileName));
                        var after = EntryLog.Recover(disk.GetValueOrDefault(r.File, []));
                        if (after.Snapshot is { } s)
                        {
                            if (s.Configuration is { IsJoint: true })
                            {
                                hit.Add(Joint);
                            }

                            if (!Chunked(d.Node).Contains(r.File))
                            {
                                hit.Add("log-compacted");
                            }
                            else
                            {
                                hit.Add("snapshot-installed-by-a-follower");
                                var held = (before.Snapshot?.Index ?? 0) + before.Entries.Count;
                                if (after.Entries.Count > 0)
                                {
                                    hit.Add("install-kept-the-suffix");
                                }
                                else if (held > s.Index || (before.Entries.FirstOrDefault(e => e.Index == s.Index) is { } at && at.Term != s.Term))
                                {
                                    hit.Add("install-discarded-the-suffix");
                                }
                            }
                        }
                    }

                    Apply(disk, op);
                    break;
                case DurableObservation { Completed: null } d:
                    if (d.Content is { } content)
                    {
                        Disk(d.Node)[d.File] = content.ToArray();
                    }
                    else
                    {
                        Disk(d.Node).Remove(d.File);
                    }

                    break;
                case CrashObservation c:
                    if (Pending(c.Node).Count > 0)
                    {
                        hit.Add("crash-with-a-compaction-in-flight");
                    }

                    Pending(c.Node).Clear();
                    break;
                case StartObservation s when EntryLog.Recover(Disk(s.Node).GetValueOrDefault(EntryLog.FileName)).Snapshot is not null:
                    hit.Add("restarted-from-a-snapshot");
                    break;
            }
        }

        return hit;
    }

    /// <summary>A write that only a compaction or an install makes: to a file that will replace the log, or the rename that replaces it.</summary>
    private static bool IsCompaction(Persist op) =>
        op is PersistRename { To: EntryLog.FileName } || (op is PersistAppend or PersistWriteAt && op.File.StartsWith(LogStore.TempPrefix, StringComparison.Ordinal) && op.File != EntryLog.FileName);

    private static void Apply(Dictionary<string, byte[]> files, Persist op)
    {
        switch (op)
        {
            case PersistAppend a:
                files[a.File] = [.. files.GetValueOrDefault(a.File, []), .. a.Data.Span];
                break;
            case PersistWriteAt w:
                var old = files.GetValueOrDefault(w.File, []);
                var grown = new byte[Math.Max(old.Length, w.Offset + w.Data.Length)];
                old.CopyTo(grown, 0);
                w.Data.Span.CopyTo(grown.AsSpan((int)w.Offset));
                files[w.File] = grown;
                break;
            case PersistTruncate t when files.TryGetValue(t.File, out var cur) && cur.Length > t.Length:
                files[t.File] = cur[..(int)t.Length];
                break;
            case PersistRename r:
                files[r.To] = files.Remove(r.File, out var moved) ? moved : [];
                break;
            case PersistDelete del:
                files.Remove(del.File);
                break;
        }
    }
}
