using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
/// leader its office, and the stall was measured on a leader no longer stalled): the open-loop 97th
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
        // stay under 5% down to about 30 writes a second. The open side is judged at its 97th percentile,
        // not its 99th: S-bench-1's coordinated omission leaves one delayed write per stall, 4 or 5 of
        // about 400 (one per stall in the window), so at the 99th percentile whether it was caught turned
        // on how the stalls fell in the window, and it survived 2 of 13 times on GitHub (run 37720359149).
        // Measured locally, 4 runs and 6: unpatched 31 to 32 writes over 150 ms and a 97th percentile of
        // 235 to 248 ms; with S-bench-1, 4 writes and 3.0 to 3.9 ms.
        var open = await LoadGenerator.RunAsync(new LoadConfig(Endpoints(c), Rate: 50, Duration: TimeSpan.FromSeconds(10), Warmup: TimeSpan.FromSeconds(2)), Ct);
        var closed = await LoadGenerator.RunAsync(new LoadConfig(Endpoints(c), Rate: 0, Duration: TimeSpan.FromSeconds(20), Warmup: TimeSpan.FromSeconds(2), ClosedClients: 3), Ct);

        Report("bench-stall.txt", FormattableString.Invariant($"open: {open.Latencies.Count} writes, {open.Incomplete} incomplete, p50 {P(open, 50):F0} us, p97 {P(open, 97):F0} us, p99 {P(open, 99):F0} us, over 150 ms {open.Latencies.Count(l => l > 150_000)}; closed: {closed.Latencies.Count} writes, p50 {P(closed, 50):F0} us, p95 {P(closed, 95):F0} us, p99 {P(closed, 99):F0} us"));
        Assert.True(c.Host(leader)!.Role == Role.Leader && open.Incomplete == 0, $"the stalled host lost its office or writes went unanswered ({open.Incomplete}): the stall was not measured on a stalled leader");
        Assert.True(P(open, 97) > 150_000, FormattableString.Invariant($"open loop's 97th percentile {P(open, 97):F0} us does not show a 300-ms stall"));
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
    /// client write is about one delayed sync (its append's), so the measurement reads what it claims,
    /// and every write has such an effect list. One closed-loop client: since P11-02 a write that
    /// arrives while every follower has an append outstanding is sent from an answer's effect list,
    /// which holds no persist, and how often that happens open loop is the machine's timing (39 of 40
    /// writes had a barrier list locally, 28 of 40 on GitHub, run 37737168056). Closed loop, a write is
    /// sent only after the previous one committed, so the follower whose answer committed it has
    /// nothing outstanding and the write's list sends to it behind the persist: a list per write by
    /// construction, as before P11-02.
    /// Sabotage S-bench-3 (the barrier counted at a client response, which a leader's append has none of).
    /// </summary>
    [Fact]
    public async Task TheBarrierMeasuredIsTheLeadersOwnSync()
    {
        var delay = new SyncDelay { Value = TimeSpan.FromMilliseconds(20) };
        await using var c = new HostCluster(RaftOptions.Default with { ElectionTimeoutMin = 1_000, ElectionTimeoutMax = 2_000 }, files: dir => new SlowSyncFileSystem(new DirectoryFileSystem(dir), delay));
        c.StartAll();
        var leader = (await c.LeaderAsync(TimeSpan.FromSeconds(15), Ct))!.Value;

        // A first write can arrive while both followers still have the new leader's no-op outstanding,
        // and is then sent from its answer (90 lists for 91 writes, once in five local runs): a short
        // closed run first, so the counted run starts with the no-op committed.
        await LoadGenerator.RunAsync(new LoadConfig(Endpoints(c), Rate: 0, Duration: TimeSpan.FromMilliseconds(500), Warmup: TimeSpan.Zero, ClosedClients: 1), Ct);
        var before = c.Host(leader)!.Barrier;

        var r = await LoadGenerator.RunAsync(new LoadConfig(Endpoints(c), Rate: 0, Duration: TimeSpan.FromSeconds(4), Warmup: TimeSpan.FromSeconds(1), ClosedClients: 1), Ct);

        var after = c.Host(leader)!.Barrier;
        var lists = after.Lists - before.Lists;
        var perList = lists == 0 ? 0 : (after.Micros - before.Micros) / lists;
        Report("bench-barrier.txt", FormattableString.Invariant($"{lists} barrier lists for {r.Answered} writes, {perList:F0} us each; a delayed sync slept {delay.MeanSleptMicros:F0} us"));
        Assert.True(lists >= r.Answered, $"{lists} effect lists with a send behind a persist, for {r.Answered} writes answered: fewer than one a write, so the measurement does not cover the writes");
        Assert.InRange(perList / delay.MeanSleptMicros, 0.9, 2.5);
    }

    /// <summary>
    /// P12-03's control: a delay planted in one hand-off, every follower's peer message held between
    /// its read from the socket and its hand-off to the loop, must appear in that segment
    /// (follower-queue) by its planted amount within 20%, and in no other segment by more than a
    /// quarter of it (the generator's own lateness aside, which is before any host). One traced cluster
    /// alternates windows with the delay off and on, three of each, each window's stamps joined alone.
    /// Measured locally, two runs: follower-queue rose 528 and 524 us, every other host segment under
    /// 60 us either way. Sabotage S-lat-1 (the follower's read stamped after the
    /// delay, so it lands in the segment before, network-out).
    /// </summary>
    [Fact]
    public async Task APlantedHandOffDelayAppearsInItsSegmentOnly()
    {
        var planted = TimeSpan.FromMicroseconds(PlantedMicros);
        await using var c = new HostCluster(RaftOptions.Default with { ElectionTimeoutMin = 1_000, ElectionTimeoutMax = 2_000 }, traced: true);
        c.StartAll();
        var leader = (await c.LeaderAsync(TimeSpan.FromSeconds(15), Ct))!.Value;
        var plain = Decomposition.Segments.ToDictionary(s => s, _ => new List<double>(), StringComparer.Ordinal);
        var delayed = Decomposition.Segments.ToDictionary(s => s, _ => new List<double>(), StringComparer.Ordinal);
        var writes = 0;
        var joined = 0;
        var left = 0;
        for (var round = 0; round < 6; round++)
        {
            var on = round % 2 == 1;
            foreach (var f in c.Nodes.Where(n => n != leader))
            {
                c.Host(f)!.PeerHandOffDelay = on ? planted : TimeSpan.Zero;
            }

            foreach (var n in c.Nodes)
            {
                c.Trace(n).Drain();
            }

            var stamps = new System.Collections.Concurrent.ConcurrentBag<WriteStamp>();
            await LoadGenerator.RunAsync(new LoadConfig(Endpoints(c), Rate: ControlRate, Duration: TimeSpan.FromSeconds(4), Warmup: TimeSpan.FromSeconds(1), Trace: stamps.Add), Ct);
            var d = Decomposition.Compute(c.Nodes.ToDictionary(n => n, n => c.Trace(n).Drain()), leader, [.. stamps]);
            writes += d.Writes;
            joined += d.Joined;
            left += d.Mismatched + d.Violations;
            foreach (var s in Decomposition.Segments)
            {
                (on ? delayed : plain)[s].AddRange(d.Micros[s]);
            }
        }

        var rise = Decomposition.Segments.ToDictionary(s => s, s => Decomposition.Stats(delayed[s]).P50 - Decomposition.Stats(plain[s]).P50, StringComparer.Ordinal);
        Report("bench-handoff.txt", FormattableString.Invariant($"{joined} of {writes} writes joined, {left} left out; median rise with {PlantedMicros} us planted: ") + string.Join(", ", rise.Select(r => FormattableString.Invariant($"{r.Key} {r.Value:F0}"))));
        Assert.True(joined >= 0.95 * writes && left == 0, $"{joined} of {writes} writes joined, {left} left out: the control did not measure the writes it ran");
        Assert.InRange(rise["follower-queue"] / PlantedMicros, 0.8, 1.2);
        // Not the generator's own segment: it is two of the generator's stamps, before the write reaches
        // any host, so no host's delay can be attributed to it; and it is the instrument's noise, which
        // moved by 98 and 112 us between the modes in two local runs, near this bound by chance alone.
        Assert.All(rise.Where(r => r.Key is not "follower-queue" and not "generator-late"), r => Assert.True(r.Value < 0.25 * PlantedMicros, FormattableString.Invariant($"{r.Key} rose {r.Value:F0} us with {PlantedMicros} us planted in follower-queue")));
    }

    /// <summary>
    /// P12-04's control: the generator dispatches each write at its time and never before it, at 3,125
    /// writes a second, against a server that answers every write at once, so the lateness is the
    /// generator's alone (against the in-process cluster the tail was set by the collector's pauses,
    /// which stop the three hosts in the same process too: a 99th percentile of 1.6 to 3.9 ms in this
    /// test process). It asserts that no write is dispatched early and that the median lateness is under
    /// 100 µs; the 99th percentile is reported, not asserted. Measured locally beside 0, 2, 3 and 4 busy
    /// processes on four processors: the median 0.6 µs with up to three and 0.9 ms with four; the 99th
    /// percentile 18 and 34 µs alone, 52 µs beside two and 3.1 ms beside three, and the sabotage harness
    /// runs four workers on GitHub's four. The 99th percentile is held by the records instead (every
    /// load record carries it, taken with the machine to itself).
    /// Sabotage S-bench-4 (the generator waits with Task.Delay again, sending some writes early).
    /// </summary>
    [Fact]
    public async Task TheGeneratorDispatchesEachWriteAtItsTime()
    {
        await using var server = new OkServer();

        var r = await LoadGenerator.RunAsync(new LoadConfig(new Dictionary<NodeId, DnsEndPoint> { [new NodeId(1)] = server.Endpoint }, Rate: 3_125, Duration: TimeSpan.FromSeconds(4), Warmup: TimeSpan.FromSeconds(1)), Ct);

        var late = r.Lateness.Order().ToList();
        var p50 = Measurement.Percentile(late, 50);
        Report("bench-lateness.txt", FormattableString.Invariant($"{late.Count} writes dispatched, {r.Latencies.Count} answered: lateness min {late[0]:F1} us, p50 {p50:F1} us, p99 {Measurement.Percentile(late, 99):F1} us, max {late[^1]:F1} us, {late.Count(l => l < 0)} early"));
        Assert.True(late.Count == r.Scheduled && r.Latencies.Count == r.Scheduled, $"{late.Count} lateness samples and {r.Latencies.Count} answers for {r.Scheduled} writes: not every write was measured");
        Assert.True(late[0] >= 0, FormattableString.Invariant($"{late.Count(l => l < 0)} writes dispatched before their time, the earliest by {-late[0]:F1} us"));
        Assert.True(p50 < 100, FormattableString.Invariant($"the generator's median lateness is {p50:F1} us"));
    }

    /// <summary>A server that answers `Status|` as a leader and every other line `ok` at once: the generator measured alone.</summary>
    private sealed class OkServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;

        public OkServer()
        {
            _listener.Start();
            _accept = AcceptAsync();
        }

        public DnsEndPoint Endpoint => new("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port);

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\n", AutoFlush = true };
                try
                {
                    while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    {
                        await writer.WriteAsync(line == "Status|" ? "ok|Leader|1\n" : "ok\n");
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accept;
            _stop.Dispose();
        }
    }

    private const double PlantedMicros = 500;
    private const double ControlRate = 300;
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
