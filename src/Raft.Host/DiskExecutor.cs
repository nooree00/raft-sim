using System;
using System.Collections.Generic;
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

    public void Execute(IReadOnlyList<Effect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        for (var i = 0; i < effects.Count; i++)
        {
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
