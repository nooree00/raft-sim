using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Raft.Checker;
using Raft.Core;
using Raft.Core.Tests;
using Raft.Host;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P10-07, phase 10 decision 7 (the register's row from phase 9): fsync failure injected into the
/// real host through its file-system interface. A sync that fails leaves the file's state unknown, so
/// the host stops at once (fail-stop), acknowledges nothing after it, and recovers from its files when
/// started again. A crash that discards every write after each file's last successful sync is what a
/// power cut leaves, which SIGKILL (phase 9) never produced. Each runs under the real client's load,
/// with the checker on the history. Vacuity risks: a host that catches the failure and goes on looks
/// healthy (S-fsync-1); a discard that drops nothing is a SIGKILL again (S-fsync-2).
/// </summary>
[Collection(nameof(BenchControlTests))]
public sealed class FsyncFailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RaftOptions Slow = RaftOptions.Default with { ElectionTimeoutMin = 600, ElectionTimeoutMax = 1_200 };

    /// <summary>Runs the real client against a cluster, does <paramref name="act"/> 2 s in, and checks the history.</summary>
    private static async Task<(List<HistoryEntry> History, long ActedAt)> UnderLoadAsync(HostCluster c, Func<NodeId, Task> act)
    {
        var leader = (await c.LeaderAsync(TimeSpan.FromSeconds(15), Ct))!.Value;
        var clock = Stopwatch.StartNew();
        var entries = new List<HistoryEntry>();
        var config = new ClientConfig(c.Nodes.ToDictionary(n => n, n => new DnsEndPoint("127.0.0.1", c.ClientEndpoint(n).Port)), Clients: 3, Duration: TimeSpan.FromSeconds(8), Timeout: TimeSpan.FromMilliseconds(500));
        var load = RealClient.RunAsync(config, entries.Add, Ct, clock);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        var actedAt = RealClient.Micros(clock);
        await act(leader);
        await load;

        var history = ClientHistory.From(HostHistory.ToClientLog(entries));
        Assert.True(history.Unexplained.Count == 0, "unexplained replies: " + string.Join("; ", history.Unexplained.Take(5)));
        Assert.True(History.Problems(history.History).Count == 0, "history problems: " + string.Join("; ", History.Problems(history.History).Take(5)));
        var verdict = WglChecker.Check(history.History, 32_000_000);
        Assert.True(verdict.IsLinearizable, $"{verdict.Verdict} at key {verdict.Key}");
        Assert.Contains(entries, e => e.Invoke > actedAt + 2_000_000 && e.Response is not null && e.Reply == "ok");
        return (entries, actedAt);
    }

    private static bool Accepts(IPEndPoint at)
    {
        try
        {
            using var tcp = new TcpClient();
            tcp.Connect(at);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>A node whose sync fails stops: it accepts no connection afterwards and answers nothing it had not answered. Sabotage S-fsync-1.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANodeWhoseSyncFailsStopsAndAnswersNothingAfter(bool leader)
    {
        var files = new ConcurrentDictionary<string, InjectedFileSystem>(StringComparer.Ordinal);
        await using var c = new HostCluster(Slow, files: dir => files.GetOrAdd(dir, d => new InjectedFileSystem(new DirectoryFileSystem(d))));
        c.StartAll();
        NodeId failed = default;
        var (history, failedAt) = await UnderLoadAsync(c, async l =>
        {
            failed = leader ? l : c.Nodes.First(n => n != l);
            files.Single(f => Path.GetFileName(f.Key) == failed.ToString()).Value.FailSyncs = true;
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            Assert.False(Accepts(c.ClientEndpoint(failed)), $"{failed} still accepts client connections 3 s after its sync failed: it did not stop");
        });

        // Nothing answered `ok` by the failed node was asked of it after the failure (and its network delay).
        Assert.DoesNotContain(history, e => e.Node == failed.Value && e.Reply.StartsWith("ok", StringComparison.Ordinal) && e.Invoke > failedAt + 50_000);
    }

    /// <summary>
    /// A crash that discards every write after each file's last successful sync, then a restart from
    /// what remains: the checker accepts the history, and the discard dropped something. Sabotage S-fsync-2.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACrashThatLosesUnsyncedWritesRecoversWithTheHistoryAccepted(bool leader)
    {
        var files = new ConcurrentDictionary<string, InjectedFileSystem>(StringComparer.Ordinal);
        await using var c = new HostCluster(Slow, files: dir => files.GetOrAdd(dir, d => new InjectedFileSystem(new DirectoryFileSystem(d)) { SyncLag = TimeSpan.FromMilliseconds(3) }));
        c.StartAll();
        long discarded = 0;
        await UnderLoadAsync(c, async l =>
        {
            var victim = leader ? l : c.Nodes.First(n => n != l);
            var fs = files.Single(f => Path.GetFileName(f.Key) == victim.ToString()).Value;
            // The power fails just after a write and before its sync: from then no write or sync reaches
            // the disk, so the kill's wait for the effect list in flight cannot make it durable. Then the
            // process dies, then the disk is what its last syncs left.
            Assert.True(await fs.PowerCutAfterNextWrite().WaitAsync(TimeSpan.FromSeconds(10), Ct), $"{victim} wrote nothing in 10 s under load");
            await c.KillAsync(victim);
            discarded = fs.Crash();
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            c.Start(victim);
        });

        Assert.True(discarded > 0, "the crash discarded no write: it was a process kill again, which keeps every write");
    }
}

/// <summary>
/// The host's data directory with faults (P10-07): a sync that throws, and a crash that keeps of each
/// file only what its last successful sync made durable (a file never synced is lost; a rename or a
/// delete is durable once the directory is synced after it). <see cref="SyncLag"/> sleeps before each
/// sync, so a kill under load can land between a write and its sync.
/// </summary>
internal sealed class InjectedFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _durable = new(StringComparer.Ordinal);

    private volatile bool _dead;
    private TaskCompletionSource<bool>? _cut;

    public bool FailSyncs { get; set; }

    public TimeSpan SyncLag { get; init; }

    public IReadOnlyList<string> List() => inner.List();

    public byte[]? Read(string name) => inner.Read(name);

    public bool Exists(string name) => inner.Exists(name);

    public void Append(string name, ReadOnlySpan<byte> data)
    {
        if (!_dead)
        {
            inner.Append(name, data);
            CutIfArmed();
        }
    }

    public void WriteAt(string name, long offset, ReadOnlySpan<byte> data)
    {
        if (!_dead)
        {
            inner.WriteAt(name, offset, data);
            CutIfArmed();
        }
    }

    public void Truncate(string name, long length)
    {
        if (!_dead)
        {
            inner.Truncate(name, length);
        }
    }

    public void Rename(string source, string target)
    {
        if (_dead)
        {
            return;
        }

        inner.Rename(source, target);
        lock (_gate)
        {
            if (_durable.Remove(source, out var image))
            {
                _durable[target] = image;
            }
        }
    }

    public void Delete(string name)
    {
        if (_dead)
        {
            return;
        }

        inner.Delete(name);
        lock (_gate)
        {
            _durable.Remove(name);
        }
    }

    public void Sync(string name)
    {
        if (SyncLag > TimeSpan.Zero)
        {
            Thread.Sleep(SyncLag);
        }

        if (FailSyncs)
        {
            throw new IOException("injected: fsync failed");
        }

        lock (_gate)
        {
            if (_dead)
            {
                return;
            }

            inner.Sync(name);
            _durable[name] = inner.Read(name) ?? [];
        }
    }

    public void SyncDirectory()
    {
        if (!_dead)
        {
            inner.SyncDirectory();
        }
    }

    /// <summary>
    /// The power fails just after the next write reaches the file, before anything syncs it: no later
    /// write, rename, delete or sync reaches the disk (each returns as if it had). Completes when it has.
    /// </summary>
    public Task<bool> PowerCutAfterNextWrite()
    {
        var cut = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _cut, cut);
        return cut.Task;
    }

    private void CutIfArmed()
    {
        if (Interlocked.Exchange(ref _cut, null) is { } cut)
        {
            lock (_gate)
            {
                _dead = true;
            }

            cut.TrySetResult(true);
        }
    }

    /// <summary>What a power cut leaves: each file reverted to its last synced bytes, a file never synced removed. Returns the bytes lost.</summary>
    public long Crash()
    {
        lock (_gate)
        {
            long lost = 0;
            foreach (var name in inner.List())
            {
                var now = inner.Read(name) ?? [];
                if (_durable.TryGetValue(name, out var image))
                {
                    lost += Math.Max(0, now.Length - image.Length);
                    inner.Truncate(name, 0);
                    inner.WriteAt(name, 0, image);
                    inner.Sync(name);
                }
                else
                {
                    lost += now.Length;
                    inner.Delete(name);
                }
            }

            inner.SyncDirectory();
            _dead = false;
            return lost;
        }
    }
}
