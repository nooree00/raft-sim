using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Host;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P9-03: the disk executor on real files. Vacuity risk: a test on real files cannot see a missing
/// fsync, since the page cache survives the test; guarded by a recording file system that sees each
/// write, each sync and each release, in order. Sabotage S-hostdisk-1 (a send released before the
/// persist before it is synced).
/// </summary>
public sealed class DiskExecutorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raft-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Records every call, in order, and passes it to the real directory.</summary>
    private sealed class Recording(IFileSystem inner, List<string> log) : IFileSystem
    {
        public IReadOnlyList<string> List() => inner.List();

        public byte[]? Read(string name) => inner.Read(name);

        public bool Exists(string name) => inner.Exists(name);

        public void Append(string name, ReadOnlySpan<byte> data)
        {
            log.Add("write " + name);
            inner.Append(name, data);
        }

        public void WriteAt(string name, long offset, ReadOnlySpan<byte> data)
        {
            log.Add("write " + name);
            inner.WriteAt(name, offset, data);
        }

        public void Truncate(string name, long length)
        {
            log.Add("write " + name);
            inner.Truncate(name, length);
        }

        public void Rename(string source, string target)
        {
            log.Add($"rename {source} {target}");
            inner.Rename(source, target);
        }

        public void Delete(string name)
        {
            log.Add("delete " + name);
            inner.Delete(name);
        }

        public void Sync(string name)
        {
            log.Add("sync " + name);
            inner.Sync(name);
        }

        public void SyncDirectory()
        {
            log.Add("sync-dir");
            inner.SyncDirectory();
        }
    }

    private static ReadOnlyMemory<byte> B(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>
    /// Every release (a send, a client response, an event) follows the sync of every persist before
    /// it in the list, and every rename is followed by a directory sync before anything after it.
    /// Sabotage S-hostdisk-1.
    /// </summary>
    [Fact]
    public void NothingLeavesBeforeThePersistsBeforeItAreDurable()
    {
        var log = new List<string>();
        var files = new Recording(new DirectoryFileSystem(_dir), log);
        var executor = new DiskExecutor(files, s => log.Add("send " + s.To), c => log.Add("respond " + c.RequestId), e => log.Add("emit " + e.Name));
        executor.Execute(
        [
            new PersistAppend("term.log", B("a")),
            new Send(new NodeId(2), B("m1")),
            new PersistAppend("entries.log", B("b")),
            new PersistTruncate("entries.log", 0),
            new ClientResponse(7, B("ok")),
            new PersistWriteAt("tmp.1", 0, B("snap")),
            new PersistRename("tmp.1", "entries.log"),
            new Emit("compact", []),
            new PersistDelete("term.log"),
            new Send(new NodeId(3), B("m2")),
        ]);

        var pending = new HashSet<string>(StringComparer.Ordinal);
        var directory = false;
        foreach (var step in log)
        {
            var p = step.Split(' ');
            switch (p[0])
            {
                case "write":
                    pending.Add(p[1]);
                    break;
                case "sync":
                    pending.Remove(p[1]);
                    break;
                case "rename":
                    pending.Remove(p[2]);
                    directory = true;
                    break;
                case "delete":
                    directory = true;
                    break;
                case "sync-dir":
                    directory = false;
                    break;
                default:
                    Assert.True(pending.Count == 0 && !directory, $"'{step}' released with {string.Join(", ", pending)} unsynced{(directory ? " and the directory unsynced" : "")}: {string.Join(" / ", log)}");
                    break;
            }
        }

        Assert.Contains("send n3", log);
        Assert.Equal(B("snap").ToArray(), new DirectoryFileSystem(_dir).Read("entries.log"));
        Assert.False(new DirectoryFileSystem(_dir).Exists("term.log"));
    }

    /// <summary>
    /// A node's persists, executed on real files, are what a restarted node recovers: a candidate's
    /// term and vote, and its log, read back through the same recovery the simulator's nodes use.
    /// </summary>
    [Fact]
    public void ARestartedNodeRecoversWhatItsPersistsWrote()
    {
        var files = new DirectoryFileSystem(_dir);
        var executor = new DiskExecutor(files, _ => { }, _ => { }, _ => { });
        var node = new RaftNode(new NodeContext(new NodeId(1), [new NodeId(2), new NodeId(3)], new Random1(), DiskExecutor.Load(files), null));
        for (var t = 0; t < 400 && node.Role != Role.Candidate; t += 10)
        {
            executor.Execute(node.Handle(new Tick(10)));
        }

        Assert.Equal(Role.Candidate, node.Role);
        var restarted = DiskExecutor.Load(new DirectoryFileSystem(_dir));
        var state = TermVoteLog.Recover(restarted[TermVoteLog.FileName].ToArray()).State;
        Assert.Equal(new Term(1), state.Term);
        Assert.Equal(new NodeId(1), state.VotedFor);
    }

    private sealed class Random1 : IRandomSource
    {
        public ulong NextUInt64() => 1;
    }
}
