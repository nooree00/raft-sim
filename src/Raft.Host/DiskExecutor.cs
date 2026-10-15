using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Raft.Core;

namespace Raft.Host;

/// <summary>
/// Executes a node's effects in order on real files (P9-03; node-interface §4): each persist is
/// written and made durable before the next effect is looked at, so a send, a client response or an
/// event leaves only after every persist before it in the list is durable. A rename, a create and a
/// delete also sync the directory (spec §8). A host never runs effects of one input concurrently.
/// </summary>
public sealed class DiskExecutor(IFileSystem files, Action<Send> send, Action<ClientResponse> respond, Action<Emit> emit)
{
    private long _barrierTicks;
    private long _barrierLists;

    /// <summary>
    /// The persist barrier's cost (P10-05, decision 6): over every effect list in which a send
    /// followed one or more persists, how many such lists, and the time those persists took before the
    /// first send was released. A leader's client write is one such list (its append, then its sends),
    /// so the leader's mean is what the barrier adds to each commit.
    /// </summary>
    public (long Lists, double Micros) Barrier => (Interlocked.Read(ref _barrierLists), Bench.Micros(Interlocked.Read(ref _barrierTicks)));

    /// <summary>Called with each append's file and bytes once they are durable (P12-03: the decomposition's persist stamps), or not at all when unset.</summary>
    public Action<string, ReadOnlyMemory<byte>>? Durable { get; set; }

    /// <summary>The files a node starts from: every file in the directory, by name.</summary>
    public static Dictionary<string, ReadOnlyMemory<byte>> Load(IFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var loaded = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var name in files.List())
        {
            loaded[name] = files.Read(name) ?? [];
        }

        return loaded;
    }

    /// <summary>
    /// P14-03, the group commit: the effect lists of several inputs executed as one batch. Every persist
    /// is written in the order the node emitted it, without a sync; then each file written is synced
    /// once, in the order the files were first written (a term record before an append stays durable
    /// before it), and the directory after a file was created; then every send, response and event held
    /// is released in emitted order. Outputs before the batch's first persist are not held (P15-05): no
    /// persist the node emitted before them is still to be made durable, so node-interface §4 lets them
    /// leave as the executor reaches them, before the batch's writes (P15-03 found these holds, with those
    /// behind an earlier list's persist, 0.73 of the leader's growth in commit-respond and leader-to-send
    /// from 1,250 to 3,125 writes a second). A rename, a delete or a truncate is a boundary: the writes before it
    /// are synced first and it keeps its own syncs, as in <see cref="Execute"/> (the snapshot's two-way
    /// barrier, P7-05). Holding a send for a later list's persist is within node-interface §4, which
    /// says when a message may leave, not that it must. A failed write or sync throws before anything
    /// held is released (P10-07). A batch of one list is executed as <see cref="Execute"/> executes it,
    /// so an idle loop waits for nothing it waited for before.
    /// </summary>
    public void ExecuteBatch(IReadOnlyList<IReadOnlyList<Effect>> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        if (lists.Count == 1)
        {
            Execute(lists[0]);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var held = new List<Effect>();
        var dirty = new List<string>();
        var durable = new List<(string File, ReadOnlyMemory<byte> Data)>();
        var created = false;
        var persisted = false;
        var sendAfterPersist = false;
        void Flush()
        {
            foreach (var file in dirty)
            {
                files.Sync(file);
            }

            if (created)
            {
                files.SyncDirectory();
            }

            foreach (var (file, data) in durable)
            {
                Durable?.Invoke(file, data);
            }

            (created, dirty, durable) = (false, [], []);
        }

        void Wrote(string file)
        {
            if (!dirty.Contains(file))
            {
                dirty.Add(file);
            }
        }

        foreach (var effects in lists)
        {
            foreach (var effect in effects)
            {
                switch (effect)
                {
                    case PersistAppend a:
                        created |= !files.Exists(a.File);
                        files.Append(a.File, a.Data.Span);
                        Wrote(a.File);
                        durable.Add((a.File, a.Data));
                        persisted = true;
                        break;
                    case PersistWriteAt w:
                        created |= !files.Exists(w.File);
                        files.WriteAt(w.File, w.Offset, w.Data.Span);
                        Wrote(w.File);
                        persisted = true;
                        break;
                    case PersistTruncate t:
                        Flush();
                        files.Truncate(t.File, t.Length);
                        files.Sync(t.File);
                        persisted = true;
                        break;
                    case PersistRename r:
                        Flush();
                        files.Sync(r.File);
                        files.Rename(r.File, r.To);
                        files.SyncDirectory();
                        persisted = true;
                        break;
                    case PersistDelete d:
                        Flush();
                        files.Delete(d.File);
                        files.SyncDirectory();
                        persisted = true;
                        break;
                    default:
                        if (!persisted)
                        {
                            // P15-05: behind no persist in its batch (class C); sabotage S-early-1, S-early-2.
                            Release(effect);
                            break;
                        }

                        sendAfterPersist |= effect is Send;
                        held.Add(effect);
                        break;
                }
            }
        }

        Flush();
        if (sendAfterPersist)
        {
            // P10-05's barrier time, per batch: how long its sends waited behind the batch's persists.
            Interlocked.Add(ref _barrierTicks, Stopwatch.GetTimestamp() - start);
            Interlocked.Increment(ref _barrierLists);
        }

        foreach (var effect in held)
        {
            Release(effect);
        }
    }

    private void Release(Effect effect)
    {
        switch (effect)
        {
            case Send s:
                send(s);
                break;
            case ClientResponse c:
                respond(c);
                break;
            case Emit e:
                emit(e);
                break;
        }
    }

    public void Execute(IReadOnlyList<Effect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        var start = Stopwatch.GetTimestamp();
        var persisted = false;
        var released = false;
        for (var i = 0; i < effects.Count; i++)
        {
            if (!released && persisted && effects[i] is Send)
            {
                released = true;
                Interlocked.Add(ref _barrierTicks, Stopwatch.GetTimestamp() - start);
                Interlocked.Increment(ref _barrierLists);
            }

            persisted |= effects[i] is PersistAppend or PersistWriteAt or PersistTruncate or PersistRename or PersistDelete;
            switch (effects[i])
            {
                case PersistAppend a:
                    var created = !files.Exists(a.File);
                    files.Append(a.File, a.Data.Span);
                    files.Sync(a.File);
                    if (created)
                    {
                        files.SyncDirectory();
                    }

                    Durable?.Invoke(a.File, a.Data);

                    break;
                case PersistWriteAt w:
                    var fresh = !files.Exists(w.File);
                    files.WriteAt(w.File, w.Offset, w.Data.Span);
                    files.Sync(w.File);
                    if (fresh)
                    {
                        files.SyncDirectory();
                    }

                    break;
                case PersistTruncate t:
                    files.Truncate(t.File, t.Length);
                    files.Sync(t.File);
                    break;
                case PersistRename r:
                    files.Sync(r.File);
                    files.Rename(r.File, r.To);
                    files.SyncDirectory();
                    break;
                case PersistDelete d:
                    files.Delete(d.File);
                    files.SyncDirectory();
                    break;
                case Send s:
                    send(s);
                    break;
                case ClientResponse c:
                    respond(c);
                    break;
                case Emit e:
                    emit(e);
                    break;
            }
        }
    }
}
