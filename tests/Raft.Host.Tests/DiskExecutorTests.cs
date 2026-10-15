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

    /// <summary>Every send, response and event in the log comes after the writes before it are synced, and after the directory once it changed.</summary>
    private static void AssertNothingLeavesEarly(List<string> log)
    {
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
    }

    /// <summary>A file system whose sync of one file fails, as a disk error would (P10-07).</summary>
    private sealed class FailingSync(IFileSystem inner, string failing) : IFileSystem
    {
        public IReadOnlyList<string> List() => inner.List();

        public byte[]? Read(string name) => inner.Read(name);

        public bool Exists(string name) => inner.Exists(name);

        public void Append(string name, ReadOnlySpan<byte> data) => inner.Append(name, data);

        public void WriteAt(string name, long offset, ReadOnlySpan<byte> data) => inner.WriteAt(name, offset, data);

        public void Truncate(string name, long length) => inner.Truncate(name, length);

        public void Rename(string source, string target) => inner.Rename(source, target);

        public void Delete(string name) => inner.Delete(name);

        public void Sync(string name)
        {
            if (name == failing)
            {
                throw new IOException($"sync of {name} failed");
            }

            inner.Sync(name);
        }

        public void SyncDirectory() => inner.SyncDirectory();
    }

    private (DiskExecutor Executor, List<string> Log) Batched(Func<IFileSystem, IFileSystem>? wrap = null)
    {
        var log = new List<string>();
        IFileSystem files = new Recording(new DirectoryFileSystem(_dir), log);
        files = wrap?.Invoke(files) ?? files;
        return (new DiskExecutor(files, s => log.Add("send " + s.To), c => log.Add("respond " + c.RequestId), e => log.Add("emit " + e.Name)), log);
    }

    /// <summary>
    /// P15-03, decision 2's hypothesis as a check: an output no persist precedes in its batch (class C) may
    /// leave at the batch's start under node-interface §4, and leaves before the batch's sync. Run at
    /// `ac1bc6e` (phase 14's host), it failed: `write entries.log / sync entries.log / sync-dir / respond 7
    /// / send n2 / send n3`. It lands with P15-05's remedy. Sabotage S-early-1.
    /// </summary>
    [Fact]
    public void AnOutputNoPersistPrecedesInItsBatchLeavesBeforeTheBatchsSync()
    {
        var (executor, log) = Batched();
        executor.ExecuteBatch(
        [
            [new ClientResponse(7, B("ok")), new Send(new NodeId(2), B("m0"))],
            [new PersistAppend("entries.log", B("a")), new Send(new NodeId(3), B("m1"))],
        ]);

        var firstSync = log.FindIndex(l => l.StartsWith("sync", StringComparison.Ordinal));
        Assert.True(firstSync >= 0 && log.IndexOf("respond 7") is >= 0 and var r && r < firstSync && log.IndexOf("send n2") is >= 0 and var s && s < firstSync, string.Join(" / ", log));
        AssertNothingLeavesEarly(log);
    }

    /// <summary>
    /// P15-05: in a batch, outputs before its first persist leave as the executor reaches them; those after
    /// it still wait for the batch's syncs. Sabotage S-early-2 (an output after a persist released early).
    /// </summary>
    [Fact]
    public void OnlyTheOutputsBeforeABatchsFirstPersistLeaveBeforeItsSyncs()
    {
        var (executor, log) = Batched();
        executor.ExecuteBatch(
        [
            [new ClientResponse(7, B("ok"))],
            [new PersistAppend("entries.log", B("a")), new Send(new NodeId(2), B("m1"))],
            [new ClientResponse(8, B("ok")), new Emit("x", [])],
        ]);

        AssertNothingLeavesEarly(log);
        Assert.Equal(["respond 7", "write entries.log", "sync entries.log", "sync-dir", "send n2", "respond 8", "emit x"], log);
    }

    /// <summary>P14-03: a batch of lists holds every send, response and event until the batch's persists are durable. Sabotage S-group-2.</summary>
    [Fact]
    public void ABatchReleasesNothingBeforeItsSyncs()
    {
        var (executor, log) = Batched();
        executor.ExecuteBatch(
        [
            [new PersistAppend("entries.log", B("a")), new Send(new NodeId(2), B("m1"))],
            [new PersistAppend("entries.log", B("b")), new ClientResponse(7, B("ok"))],
            [new PersistWriteAt("term.log", 0, B("t")), new Emit("vote", []), new Send(new NodeId(3), B("m2"))],
        ]);

        AssertNothingLeavesEarly(log);
        Assert.Equal(["send n2", "respond 7", "emit vote", "send n3"], log.Where(l => !l.StartsWith("write", StringComparison.Ordinal) && !l.StartsWith("sync", StringComparison.Ordinal)));
    }

    /// <summary>
    /// P14-03: a batch syncs each file it wrote once, in the order the files were first written, so a
    /// term record written before an append is durable before it, as executing the lists one at a time
    /// made it. Sabotage S-group-3.
    /// </summary>
    [Fact]
    public void ABatchSyncsEachFileOnceInTheOrderTheyWereFirstWritten()
    {
        var files = new DirectoryFileSystem(_dir);
        files.Append("term.log", B("0").Span);
        files.Append("entries.log", B("0").Span);
        var (executor, log) = Batched();
        executor.ExecuteBatch(
        [
            [new PersistWriteAt("term.log", 0, B("t")), new Send(new NodeId(2), B("m1"))],
            [new PersistAppend("entries.log", B("a")), new Send(new NodeId(3), B("m2"))],
            [new PersistAppend("entries.log", B("b")), new ClientResponse(7, B("ok"))],
        ]);

        Assert.Equal(["sync term.log", "sync entries.log"], log.Where(l => l.StartsWith("sync ", StringComparison.Ordinal)));
        Assert.Equal(B("0ab").ToArray(), new DirectoryFileSystem(_dir).Read("entries.log"));
    }

    /// <summary>P14-03 with P10-07: a batch whose sync fails releases none of what it held. Sabotage S-group-4.</summary>
    [Fact]
    public void ABatchWhoseSyncFailsReleasesNothing()
    {
        var (executor, log) = Batched(files => new FailingSync(files, "entries.log"));
        Assert.Throws<IOException>(() => executor.ExecuteBatch(
        [
            [new PersistAppend("entries.log", B("a")), new Send(new NodeId(2), B("m1"))],
            [new PersistAppend("entries.log", B("b")), new ClientResponse(7, B("ok"))],
        ]));

        Assert.DoesNotContain(log, l => l.StartsWith("send", StringComparison.Ordinal) || l.StartsWith("respond", StringComparison.Ordinal));
    }

    /// <summary>
    /// P14-03 with P7-05's two-way barrier: a rename inside a batch is a boundary, so the writes before
    /// it are durable before the file it renames replaces anything. Sabotage S-group-5.
    /// </summary>
    [Fact]
    public void ARenameInABatchComesAfterTheSyncsOfTheWritesBeforeIt()
    {
        var (executor, log) = Batched();
        executor.ExecuteBatch(
        [
            [new PersistAppend("a.log", B("x")), new Send(new NodeId(2), B("m1"))],
            [new PersistWriteAt("tmp.1", 0, B("snap")), new PersistRename("tmp.1", "snap")],
        ]);

        var synced = log.IndexOf("sync a.log");
        var renamed = log.IndexOf("rename tmp.1 snap");
        Assert.True(synced >= 0 && renamed > synced, $"the rename came before a.log's sync: {string.Join(" / ", log)}");
        AssertNothingLeavesEarly(log);
    }

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

        AssertNothingLeavesEarly(log);

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
