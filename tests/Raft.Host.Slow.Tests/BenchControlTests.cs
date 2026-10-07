using System;
using System.Collections.Generic;
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
/// P10-03: the load generator measures what it claims, shown by two planted effects of known shape on
/// the in-process cluster. (a) A stall of the leader's loop, 300 ms every 2 s, in a cluster whose
/// election timeout is 1 to 2 s (with the default 150 to 300 ms, the breakdown's 200-ms stall cost the
/// leader its office, and the stall was measured on a leader no longer stalled): the open-loop 99th
/// percentile must show it, and a closed-loop run of the same cluster must not (the representation
/// matters, decision 3). (b) Every sync slowed by about 20 ms: the median commit latency must rise by
/// about two of the delays actually slept, the model's amount (the leader's sync and a follower's, in
/// series). Both clusters have the long election timeout, so a busy machine does not elect anew mid-run, and
/// both are bounded for a busy machine: the harness runs four test processes at once. Vacuity risk: a generator
/// that waits for replies, or times from its own send, hides the stall (S-bench-1); one insensitive to
/// the system's cost passes everything (S-bench-2).
/// </summary>
[Collection(nameof(BenchControlTests))]
public sealed class BenchControlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<NodeId, DnsEndPoint> Endpoints(HostCluster c) =>
        c.Nodes.ToDictionary(n => n, n => new DnsEndPoint("127.0.0.1", c.ClientEndpoint(n).Port));

    private static double P(LoadResult r, double p) => Measurement.Percentile([.. r.Latencies.Order()], p);

    private static void Report(string name, string text) =>
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, name), text + "\n");

    /// <summary>Control (a): the planted stall is in the open-loop tail and absent from the closed-loop one. Sabotage S-bench-1.</summary>
    [Fact]
    public async Task APlantedStallShowsOpenLoopAndHidesClosedLoop()
    {
        await using var c = new HostCluster(RaftOptions.Default with { ElectionTimeoutMin = 1_000, ElectionTimeoutMax = 2_000 });
        c.StartAll();
        var leader = (await c.LeaderAsync(TimeSpan.FromSeconds(15), Ct))!.Value;
        c.Host(leader)!.StallEvery(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(300));

        // 50 writes a second, not 200: on GitHub's runners, with other workers on the machine, a stall's
        // backlog at 200/s did not drain (the phase-10 collapse) and writes went unanswered. The share of
        // writes a stall delays past 150 ms (its first half, 7.5% of the time) does not depend on the rate;
        // the closed side is judged at its 95th percentile: a stall holds three closed writes, which on
        // GitHub's runner came to over 1% of the closed run's writes (its 99th percentile 193 ms) and
        // stay under 5% down to about 30 writes a second.
        var open = await LoadGenerator.RunAsync(new LoadConfig(Endpoints(c), Rate: 50, Duration: TimeSpan.FromSeconds(10), Warmup: TimeSpan.FromSeconds(2)), Ct);
        var closed = await LoadGenerator.RunAsync(new LoadConfig(Endpoints(c), Rate: 0, Duration: TimeSpan.FromSeconds(20), Warmup: TimeSpan.FromSeconds(2), ClosedClients: 3), Ct);

        Report("bench-stall.txt", FormattableString.Invariant($"open: {open.Latencies.Count} writes, {open.Incomplete} incomplete, p50 {P(open, 50):F0} us, p99 {P(open, 99):F0} us; closed: {closed.Latencies.Count} writes, p50 {P(closed, 50):F0} us, p95 {P(closed, 95):F0} us, p99 {P(closed, 99):F0} us"));
        Assert.True(c.Host(leader)!.Role == Role.Leader && open.Incomplete == 0, $"the stalled host lost its office or writes went unanswered ({open.Incomplete}): the stall was not measured on a stalled leader");
        Assert.True(P(open, 99) > 150_000, FormattableString.Invariant($"open loop's 99th percentile {P(open, 99):F0} us does not show a 300-ms stall"));
        Assert.True(P(closed, 95) < 75_000, FormattableString.Invariant($"closed loop's 95th percentile {P(closed, 95):F0} us: the comparison is not closed-loop"));
    }

    /// <summary>
    /// Control (b): a 20-ms delay on every sync raises the median commit latency by about two syncs.
    /// One cluster alternates windows with the delay off and on, three of each, so a machine busy
    /// with other work slows both modes alike (decision 4: a comparison is interleaved, on one
    /// machine; two clusters run one after the other under a harness's builds did not agree).
    /// Sabotage S-bench-2.
    /// </summary>
    [Fact]
    public async Task APlantedSlowdownMovesTheMedianByTheModelsAmount()
    {
        var delay = new SyncDelay();
        await using var c = new HostCluster(RaftOptions.Default with { ElectionTimeoutMin = 1_000, ElectionTimeoutMax = 2_000 }, files: dir => new SlowSyncFileSystem(new DirectoryFileSystem(dir), delay));
        c.StartAll();
        await c.LeaderAsync(TimeSpan.FromSeconds(15), Ct);
        var plain = new List<double>();
        var slowed = new List<double>();
        for (var round = 0; round < 6; round++)
        {
            var on = round % 2 == 1;
            delay.Value = on ? TimeSpan.FromMilliseconds(20) : TimeSpan.Zero;
            var r = await LoadGenerator.RunAsync(new LoadConfig(Endpoints(c), Rate: 20, Duration: TimeSpan.FromSeconds(4), Warmup: TimeSpan.FromSeconds(1)), Ct);
            (on ? slowed : plain).AddRange(r.Latencies);
        }

        var rise = Measurement.Percentile([.. slowed.Order()], 50) - Measurement.Percentile([.. plain.Order()], 50);
        var slept = delay.MeanSleptMicros;
        Report("bench-slowdown.txt", FormattableString.Invariant($"median {Measurement.Percentile([.. plain.Order()], 50):F0} us with the delay off, {Measurement.Percentile([.. slowed.Order()], 50):F0} us with it on ({plain.Count} and {slowed.Count} writes, interleaved): a rise of {rise:F0} us, {rise / slept:F2} times the {slept:F0} us each delayed sync actually slept"));
        // Two syncs in series is 2 times the delay, and writes queued behind a slowed sync add a little
        // (at 20 writes a second the leader is about 40% busy). The delay is 20 ms because a 2-ms one
        // vanished on a machine busy with the harness's other workers (a ratio of 0.47 there, 3.16 in
        // another run): on a starved machine a short sleep overlaps the wait for a processor that
        // happens anyway. The lower bound is the control: an insensitive generator gives about 0
        // (S-bench-2). The upper bound only catches a rise counted twice.
        Assert.InRange(rise / slept, 1.5, 5.0);
    }

    /// <summary>
    /// P10-05's vacuity guard: with every sync delayed and no other delay, the leader's barrier per
    /// client write is about one delayed sync (its append's), so the measurement reads what it claims.
    /// Sabotage S-bench-3 (the barrier counted at a client response, which a leader's append has none of).
    /// </summary>
    [Fact]
    public async Task TheBarrierMeasuredIsTheLeadersOwnSync()
    {
        var delay = new SyncDelay { Value = TimeSpan.FromMilliseconds(20) };
        await using var c = new HostCluster(RaftOptions.Default with { ElectionTimeoutMin = 1_000, ElectionTimeoutMax = 2_000 }, files: dir => new SlowSyncFileSystem(new DirectoryFileSystem(dir), delay));
        c.StartAll();
        var leader = (await c.LeaderAsync(TimeSpan.FromSeconds(15), Ct))!.Value;
        var before = c.Host(leader)!.Barrier;

        var r = await LoadGenerator.RunAsync(new LoadConfig(Endpoints(c), Rate: 10, Duration: TimeSpan.FromSeconds(4), Warmup: TimeSpan.FromSeconds(1)), Ct);

        var after = c.Host(leader)!.Barrier;
        var lists = after.Lists - before.Lists;
        var perList = lists == 0 ? 0 : (after.Micros - before.Micros) / lists;
        Report("bench-barrier.txt", FormattableString.Invariant($"{lists} barrier lists for {r.Answered} writes, {perList:F0} us each; a delayed sync slept {delay.MeanSleptMicros:F0} us"));
        Assert.True(lists >= r.Answered, $"{lists} effect lists with a send behind a persist, for {r.Answered} writes answered");
        Assert.InRange(perList / delay.MeanSleptMicros, 0.9, 2.5);
    }
}

/// <summary>
/// The bench controls run alone in their process: beside the other classes' clusters, the slowdown's
/// rise fell to a fifth of its size in the harness (P10-03), and a control of timing is not
/// meaningful on a machine its own process is crowding.
/// </summary>
[CollectionDefinition(nameof(BenchControlTests), DisableParallelization = true)]
public sealed class BenchControlsAlone;

/// <summary>The delay a <see cref="SlowSyncFileSystem"/> adds, switchable while the cluster runs, and the time it actually slept: a busy machine oversleeps.</summary>
internal sealed class SyncDelay
{
    private long _sleptTicks;
    private long _sleeps;

    public TimeSpan Value { get; set; }

    public double MeanSleptMicros => _sleeps == 0 ? double.NaN : Bench.Micros(Interlocked.Read(ref _sleptTicks)) / Interlocked.Read(ref _sleeps);

    public void Sleep()
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        Thread.Sleep(Value);
        Interlocked.Add(ref _sleptTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref _sleeps);
    }
}

/// <summary>Every sync delayed by a set time: a slowdown of known size for control (b).</summary>
internal sealed class SlowSyncFileSystem(IFileSystem inner, SyncDelay delay) : IFileSystem
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
        if (delay.Value > TimeSpan.Zero)
        {
            delay.Sleep();
        }

        inner.Sync(name);
    }

    public void SyncDirectory() => inner.SyncDirectory();
}
