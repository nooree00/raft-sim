using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Raft.Core;
using Raft.Host;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P13-04: a node's effect lists run off its loop, on one thread, in the order handed over; nothing
/// runs after a failed write or sync; and the loop takes its next input while a sync is held. The
/// barrier within a list is DiskExecutorTests' (S-hostdisk-1); these hold it across lists. Sabotages
/// S-loop-1 (two threads), S-loop-2 (lists run after a failure), S-loop-3 (the loop waits for each list).
/// </summary>
public sealed class EffectThreadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<Effect> List(int k, bool persist) =>
        persist ? [new PersistAppend("entries.log", Encoding.ASCII.GetBytes($"{k}")), new Send(new NodeId(2), Encoding.ASCII.GetBytes($"{k}"))] : [new ClientResponse(k, Encoding.ASCII.GetBytes($"{k}"))];

    private static int Number(IReadOnlyList<Effect> list) => int.Parse(Encoding.ASCII.GetString(list[0] switch { PersistAppend p => p.Data.Span, ClientResponse r => r.Payload.Span, _ => throw new InvalidOperationException() }), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Every list's release (a send, a response) comes after the persist of every list handed over
    /// before it is durable, with every second persist slowed so a list behind it could overtake it
    /// on a second thread. Sabotage S-loop-1.
    /// </summary>
    [Fact]
    public async Task NoListReleasesBeforeThePersistsOfTheListsBeforeItAreDurable()
    {
        var log = new List<(string Step, int List)>();
        var persists = 0;
        void Execute(IReadOnlyList<Effect> list)
        {
            var k = Number(list);
            foreach (var e in list)
            {
                if (e is PersistAppend && Interlocked.Increment(ref persists) % 2 == 0)
                {
                    Thread.Sleep(5);
                }

                lock (log)
                {
                    log.Add((e is PersistAppend ? "durable" : "release", k));
                }
            }
        }

        var q = new EffectThread(Execute, _ => { });
        await using (q)
        {
            for (var k = 1; k <= 200; k++)
            {
                Assert.True(q.Enqueue(List(k, persist: k % 2 == 1)));
            }

            for (var end = DateTime.UtcNow.AddSeconds(10); q.Times.Lists < 200 && DateTime.UtcNow < end;)
            {
                await Task.Delay(10, Ct);
            }
        }

        Assert.Equal(200, q.Times.Lists);
        var durable = new HashSet<int>();
        foreach (var (step, k) in log)
        {
            if (step == "durable")
            {
                durable.Add(k);
                continue;
            }

            var waiting = Enumerable.Range(1, k - 1).Where(j => j % 2 == 1 && !durable.Contains(j)).ToList();
            Assert.True(waiting.Count == 0, $"list {k} released while the persists of lists {string.Join(", ", waiting.Take(5))} handed over before it were not durable");
        }
    }

    /// <summary>A failed sync stops the queue: the failing list's later effects and every list queued behind it never run, and the host is told once. Sabotage S-loop-2.</summary>
    [Fact]
    public async Task NothingRunsAfterAFailedSync()
    {
        var ran = new List<int>();
        var failures = new List<Exception>();
        using var gate = new ManualResetEventSlim(false);
        void Execute(IReadOnlyList<Effect> list)
        {
            var k = Number(list);
            if (k == 1)
            {
                gate.Wait(TimeSpan.FromSeconds(10));
            }

            if (k == 3)
            {
                throw new IOException("sync failed");
            }

            lock (ran)
            {
                ran.Add(k);
            }
        }

        var q = new EffectThread(Execute, e => { lock (failures) { failures.Add(e); } });
        await using (q)
        {
            for (var k = 1; k <= 10; k++)
            {
                Assert.True(q.Enqueue(List(k, persist: true)));
            }

            gate.Set();
            for (var end = DateTime.UtcNow.AddSeconds(10); failures.Count == 0 && DateTime.UtcNow < end;)
            {
                await Task.Delay(10, Ct);
            }

            await Task.Delay(200, Ct);
            Assert.False(q.Enqueue(List(11, persist: true)));
        }

        Assert.Equal([1, 2], ran);
        Assert.Single(failures);
    }

    /// <summary>A data directory whose syncs can be held open, to see what the loop does meanwhile.</summary>
    private sealed class HeldSyncs(IFileSystem inner, SemaphoreSlim entered, ManualResetEventSlim release, Func<bool> holding) : IFileSystem
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
            if (holding())
            {
                entered.Release();
                release.Wait(TimeSpan.FromSeconds(30));
            }

            inner.Sync(name);
        }

        public void SyncDirectory() => inner.SyncDirectory();
    }

    /// <summary>
    /// The control (decision 2's vacuity risk): the leader's sync is held open for 300 ms and its loop
    /// must go on taking its inputs (its clock's ticks, every 10 ms, and its followers' answers)
    /// meanwhile; the write answers once the sync returns. Sabotage S-loop-3.
    /// </summary>
    [Fact]
    public async Task TheLoopTakesItsInputsWhileASyncIsHeld()
    {
        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);
        string? held = null;
        // The leader's heartbeats wait behind the held persist too (node-interface §4), so the election
        // timeouts are raised past the hold, or the followers would elect another leader meanwhile.
        var options = RaftOptions.Default with { ElectionTimeoutMin = 1000, ElectionTimeoutMax = 2000 };
        await using var c = new HostCluster(options, files: d => new HeldSyncs(new DirectoryFileSystem(d), entered, release, () => Volatile.Read(ref held) == Path.GetFileName(d)));
        c.StartAll();
        var leader = await c.LeaderAsync(TimeSpan.FromSeconds(10), Ct) ?? throw new InvalidOperationException("no leader");
        Volatile.Write(ref held, leader.ToString());
        var write = c.RequestAsync(leader, "Put|k1|v1", TimeSpan.FromSeconds(10), Ct);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5), Ct), "the write's sync never started");
        var before = c.Host(leader)!.LoopTimes.Inputs;
        await Task.Delay(300, Ct);
        var during = c.Host(leader)!.LoopTimes.Inputs - before;
        Volatile.Write(ref held, null);
        release.Set();

        Assert.True(during >= 10, $"the loop took {during} inputs in the 300 ms its effects' sync was held: it waited for the sync");
        Assert.StartsWith("ok", await write, StringComparison.Ordinal);
    }
}
