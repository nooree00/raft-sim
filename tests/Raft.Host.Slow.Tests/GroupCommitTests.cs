using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Raft.Core;
using Raft.Host;
using Raft.Host.Tests;
using Xunit;

namespace Raft.Host.Slow.Tests;

/// <summary>
/// P14-03's control: under a burst of concurrent writes the leader syncs fewer times than it writes,
/// because its loop takes the inputs already queued and their persists share one sync
/// (<see cref="NodeHost.BatchLimit"/>). Counted, not timed: three nodes in process, 64 closed-loop
/// clients for two seconds after half a second's warm-up, the leader's syncs over the writes it
/// answered. Without the batching the leader syncs once a write or more (its appends, its term
/// records); the bound is half that. Sabotage S-group-1 (the loop takes one input at a time). The
/// report is written before the assertion, so the margin audit reads both sides (P14-07). The cluster's
/// election timeout is 1 to 2 s, as the bench controls' are (P16-08): on the default 150 to 300 ms, S-group-1's
/// baseline in a harness shard answered 21 writes of the burst (run 39062045985), the burst most likely
/// left without a leader by a pause on a crowded runner; the control counts syncs, not elections.
/// </summary>
public sealed class GroupCommitTests
{
    private const double Bound = 0.5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnderABurstTheLeaderSyncsFewerTimesThanItWrites()
    {
        var counters = new ConcurrentDictionary<string, CountingFileSystem>(StringComparer.Ordinal);
        await using var c = new HostCluster(RaftOptions.Default with { ElectionTimeoutMin = 1_000, ElectionTimeoutMax = 2_000 }, files: d => counters.GetOrAdd(Path.GetFileName(d), _ => new CountingFileSystem(new DirectoryFileSystem(d))));
        c.StartAll();
        var leader = (await c.LeaderAsync(TimeSpan.FromSeconds(15), Ct))!.Value;
        var syncs = counters[leader.ToString()];
        var endpoints = c.Nodes.ToDictionary(n => n, n => new DnsEndPoint("127.0.0.1", c.ClientEndpoint(n).Port));
        var before = syncs.Syncs;
        var r = await LoadGenerator.RunAsync(new LoadConfig(endpoints, Rate: 0, Duration: TimeSpan.FromSeconds(2.5), Warmup: TimeSpan.FromSeconds(0.5), ClosedClients: 64), Ct);
        var perWrite = (double)(syncs.Syncs - before) / Math.Max(1, r.Answered);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "bench-group.txt"),
            string.Create(CultureInfo.InvariantCulture, $"{r.Answered} writes answered by 64 closed-loop clients; the leader synced {syncs.Syncs - before} times, {perWrite:F4} syncs per write (bound {Bound})\n"));
        Assert.True(r.Answered >= 500, $"only {r.Answered} writes answered: the burst measured too little");
        Assert.True(perWrite < Bound, string.Create(CultureInfo.InvariantCulture, $"the leader synced {perWrite:F2} times a write under 64 concurrent clients, against {Bound}: its loop is not grouping its persists"));
    }
}
