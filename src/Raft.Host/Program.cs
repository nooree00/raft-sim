using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Raft.Core;

namespace Raft.Host;

/// <summary>
/// `Raft.Host node --id 1 --peers 1=n1:7000,2=n2:7000,3=n3:7000 --client-port 7100 --data /data`
/// runs one node until it is killed, or until a write or sync fails (exit code 3, P10-07). `Raft.Host client --nodes 1=n1:7100,... --clients 3 --seconds 60
/// --timeout-ms 1000 --history /out/history.jsonl` runs the real client and writes its history.
/// `Raft.Host request --node n1:7100 --line Status|` sends one line and prints the reply (the
/// orchestration asks each node its role this way).
/// Core's events go to standard output as JSON lines (phase 9 decision 6).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("usage: raft-host node|client [options]").ConfigureAwait(false);
            return 2;
        }

        var options = Options(args);
        switch (args[0])
        {
            case "node":
                var id = new NodeId(int.Parse(options["--id"], CultureInfo.InvariantCulture));
                var peers = Endpoints(options["--peers"]);
                // The P9-07 positive control, for its one measurement in Compose: never set in a real configuration.
                var raft = Environment.GetEnvironmentVariable("RAFT_ANSWER_AT_APPEND") == "1" ? RaftOptions.Default with { AnswerAtAppend = true } : RaftOptions.Default;
                // P12-03: the hand-off stamps, for a decomposed bench run only (fetched with `Trace|`).
                var trace = Environment.GetEnvironmentVariable("RAFT_HANDOFF_TRACE") == "1" ? new HandOffTrace() : null;
                // P13-02: `Roles|` answered, for a load run's record of the cluster's role changes (bench mode only).
                var roles = Environment.GetEnvironmentVariable("RAFT_BENCH_ROLES") == "1";
                var config = new HostConfig(id, peers, new IPEndPoint(IPAddress.Any, peers[id].Port), new IPEndPoint(IPAddress.Any, int.Parse(options["--client-port"], CultureInfo.InvariantCulture)), options["--data"], raft, Trace: trace, AnswerRoles: roles);
                var host = new NodeHost(config, Console.Out);
                await using (host.ConfigureAwait(false))
                {
                    host.Start();
                    // P10-07: a failed write or sync stops the host, and the process exits with code 3 so an
                    // orchestrator sees a stopped node rather than a silent one.
                    var failure = await host.Stopped.ConfigureAwait(false);
                    await Console.Error.WriteLineAsync("raft-host: stopped after a failed write or sync: " + failure.Message).ConfigureAwait(false);
                }

                return 3;
            case "client":
                var client = new ClientConfig(
                    Endpoints(options["--nodes"]),
                    int.Parse(options.GetValueOrDefault("--clients", "3"), CultureInfo.InvariantCulture),
                    TimeSpan.FromSeconds(double.Parse(options.GetValueOrDefault("--seconds", "30"), CultureInfo.InvariantCulture)),
                    TimeSpan.FromMilliseconds(double.Parse(options.GetValueOrDefault("--timeout-ms", "1000"), CultureInfo.InvariantCulture)),
                    Seed: int.Parse(options.GetValueOrDefault("--seed", "1"), CultureInfo.InvariantCulture));
                var history = new StreamWriter(options["--history"]) { AutoFlush = true };
                await using (history.ConfigureAwait(false))
                {
                    await RealClient.RunAsync(client, e => RealClient.Write(history, e), CancellationToken.None).ConfigureAwait(false);
                }

                return 0;
            case "request":
                var at = options["--node"];
                var colon = at.LastIndexOf(':');
                var reply = await RealClient.SendAsync(new DnsEndPoint(at[..colon], int.Parse(at[(colon + 1)..], CultureInfo.InvariantCulture)), options["--line"], TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                await Console.Out.WriteLineAsync(reply ?? "no-reply").ConfigureAwait(false);
                return reply is null ? 1 : 0;
            case "bench":
                return await BenchAsync(args.Length > 1 ? args[1] : "", Options(args[1..])).ConfigureAwait(false);
            default:
                await Console.Error.WriteLineAsync("unknown mode " + args[0]).ConfigureAwait(false);
                return 2;
        }
    }

    /// <summary>
    /// `bench sync --dir D --count N --bytes B --id ID --out measurements` and `bench rtt --count N
    /// --bytes B --id ID --out measurements` (P10-02): one record each, its configuration read by
    /// <see cref="Measurement.Config"/>.
    /// </summary>
    private static async Task<int> BenchAsync(string what, Dictionary<string, string> o)
    {
        var count = int.Parse(o.GetValueOrDefault("--count", "10000"), CultureInfo.InvariantCulture);
        var bytes = int.Parse(o.GetValueOrDefault("--bytes", "96"), CultureInfo.InvariantCulture);
        var dir = o.GetValueOrDefault("--dir", Path.GetTempPath());
        double[] samples;
        string measure;
        switch (what)
        {
            case "sync":
                Directory.CreateDirectory(dir);
                var writers = int.Parse(o.GetValueOrDefault("--writers", "1"), CultureInfo.InvariantCulture);
                _ = Bench.Sync(dir, Math.Min(count, 200), bytes); // warm-up, discarded
                samples = writers > 1 ? Bench.SyncConcurrent(dir, count, bytes, writers) : Bench.Sync(dir, count, bytes);
                measure = writers > 1
                    ? $"append of {bytes} bytes and sync, {count} times on each of {writers} threads at once, each in its own directory under {dir}"
                    : $"append of {bytes} bytes and sync, {count} times, in {dir}";
                break;
            case "handoff":
                _ = Bench.HandOff(Math.Min(count, 200)); // warm-up, discarded
                samples = Bench.HandOff(count);
                measure = $"an input handed from a thread to a loop blocked on it (an unbounded channel, continuations on the writer), {count} times, a millisecond apart";
                break;
            case "hop":
                _ = Bench.Hop(Math.Min(count, 200), bytes); // warm-up, discarded
                samples = Bench.Hop(count, bytes);
                measure = $"a {bytes}-byte frame written to a loopback connection read on a thread of its own, one way, {count} times, a millisecond apart";
                break;
            case "hop-reader":
                // P13-06: the reader's side of a hop between two Compose containers; the record is written here.
                var (hops, from) = Bench.HopReader(int.Parse(o.GetValueOrDefault("--port", "7300"), CultureInfo.InvariantCulture), count, 200);
                samples = hops;
                measure = $"a {bytes}-byte frame from another container ({o.GetValueOrDefault("--writer", "?")}, {from}) to a reader asleep on a thread of its own in this one ({Environment.MachineName}), one way, timed from the writer's monotonic stamp (one kernel, one clock), {count} times, a millisecond apart, after 200 discarded";
                break;
            case "hop-writer":
                var to = o["--to"];
                var colon = to.LastIndexOf(':');
                var path = Bench.HopWriter(to[..colon], int.Parse(to[(colon + 1)..], CultureInfo.InvariantCulture), count + 200, bytes);
                await Console.Out.WriteLineAsync($"hop-writer: {count + 200} frames, {path}").ConfigureAwait(false);
                return 0;
            case "rtt":
                _ = await Bench.RoundTripAsync(Math.Min(count, 200), bytes, CancellationToken.None).ConfigureAwait(false); // warm-up, discarded
                samples = await Bench.RoundTripAsync(count, bytes, CancellationToken.None).ConfigureAwait(false);
                measure = $"loopback round trip of a {bytes}-byte frame, {count} times";
                break;
            case "allowance":
                // P13-08: the restated target's allowance, the wait at one serial resource, by Lindley's
                // recursion, committed so the target's figures and their sensitivity can be computed again.
                var rate = double.Parse(o["--rate"], CultureInfo.InvariantCulture);
                var service = double.Parse(o["--service-us"], CultureInfo.InvariantCulture);
                var even = o.GetValueOrDefault("--arrivals", "poisson") == "even";
                var seed = int.Parse(o.GetValueOrDefault("--seed", "12"), CultureInfo.InvariantCulture);
                var discard = int.Parse(o.GetValueOrDefault("--discard", "100000"), CultureInfo.InvariantCulture);
                samples = Bench.Allowance(rate, service, even, count, seed, discard);
                measure = FormattableString.Invariant($"the wait at a single server (a model, not the machine): Lindley's recursion over {count} arrivals after {discard} discarded, {(even ? "evenly spaced" : "Poisson")} at {rate} a second, each served for {service} us, seed {seed}; utilisation {rate * service / 1e6:F3}");
                break;
            case "load":
                return await LoadAsync(o).ConfigureAwait(false);
            case "cluster":
                return await ClusterAsync(o).ConfigureAwait(false);
            case "record":
                // A measurement taken by a script (P10-06's soak timings): the results passed in, the
                // configuration read here, as for every other record.
                var timed = o["--results"].Split(',').Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => double.Parse(kv[1], CultureInfo.InvariantCulture), StringComparer.Ordinal);
                var taken = new MeasurementRecord(o["--id"], o.GetValueOrDefault("--task", "P10-06"), o["--measure"], Measurement.Config(o.GetValueOrDefault("--warmup", "none"), o.GetValueOrDefault("--repetition", "1"), o["--load"], o.GetValueOrDefault("--data", Path.GetTempPath())), timed);
                Measurement.Write(o.GetValueOrDefault("--out", "measurements"), taken);
                await Console.Out.WriteLineAsync($"{taken.Id}: " + string.Join(", ", timed.Select(x => $"{x.Key} {x.Value}"))).ConfigureAwait(false);
                return 0;
            default:
                await Console.Error.WriteLineAsync("bench sync|handoff|hop|rtt|allowance|load|record").ConfigureAwait(false);
                return 2;
        }

        var record = new MeasurementRecord(
            o["--id"],
            o.GetValueOrDefault("--task", "P10-02"),
            measure,
            Measurement.Config(Math.Min(count, 200) + " operations, discarded", o.GetValueOrDefault("--repetition", "1"), "none: one operation at a time", dir),
            Bench.Summary(samples));
        Measurement.Write(o.GetValueOrDefault("--out", "measurements"), record);
        await Console.Out.WriteLineAsync($"{record.Id}: " + string.Join(", ", record.Results.Select(r => $"{r.Key} {r.Value}"))).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// `bench load --rate R --seconds S --warmup W [--clients N] (--nodes 1=n1:7100,... | --local DIR)
    /// --id ID --out measurements` (P10-04): one load run, open loop (or closed with --clients) against
    /// a Compose cluster by its client addresses, or against three hosts this process starts on
    /// loopback, whose syncs it counts.
    /// </summary>
    private static async Task<int> LoadAsync(Dictionary<string, string> o)
    {
        var rate = double.Parse(o.GetValueOrDefault("--rate", "0"), CultureInfo.InvariantCulture);
        var seconds = double.Parse(o.GetValueOrDefault("--seconds", "10"), CultureInfo.InvariantCulture);
        var warmup = double.Parse(o.GetValueOrDefault("--warmup", "3"), CultureInfo.InvariantCulture);
        int? clients = o.TryGetValue("--clients", out var cl) ? int.Parse(cl, CultureInfo.InvariantCulture) : null;
        var decompose = o.ContainsKey("--decompose");
        LocalCluster? local = null;
        IClusterProbe? probe = null;
        IReadOnlyDictionary<NodeId, DnsEndPoint> nodes;
        string where;
        if (o.TryGetValue("--local", out var root))
        {
            local = new LocalCluster(root);
            local.Start(traced: decompose, traceSample: int.Parse(o.GetValueOrDefault("--trace-sample", "1"), CultureInfo.InvariantCulture));
            probe = new LocalProbe(local);
            await probe.LeaderAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            nodes = local.Clients;
            where = $"in-process cluster of 3 hosts on loopback, data in {root}, events discarded" + (decompose ? ", hand-offs traced" : "") + ", the load generator in the hosts' process";
        }
        else if (o.TryGetValue("--cluster", out var control))
        {
            // P15-01: the same in-process cluster in a process of its own (`bench cluster`), asked through its
            // control port, so the generator shares nothing with the hosts but the machine.
            var colon = control.LastIndexOf(':');
            probe = await ControlProbe.ConnectAsync(new DnsEndPoint(control[..colon], int.Parse(control[(colon + 1)..], CultureInfo.InvariantCulture)), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            await probe.LeaderAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            nodes = probe.Clients;
            where = $"in-process cluster of 3 hosts on loopback in a process of its own (`bench cluster`, pid {probe.ProcessId}), data in {o.GetValueOrDefault("--cluster-data", "/tmp/cluster")}, events discarded" + (decompose ? ", hand-offs traced" : "") + $", the load generator in another process (pid {Environment.ProcessId})";
            root = o.GetValueOrDefault("--cluster-data", "/tmp/cluster");
        }
        else
        {
            nodes = Endpoints(o["--nodes"]);
            where = "Compose cluster " + o["--nodes"] + ", events to each node's standard output" + (decompose ? ", hand-offs traced" : "");
        }

        try
        {
            var countersBefore = probe is null ? null : await probe.CountersAsync().ConfigureAwait(false);
            var writes = new System.Collections.Concurrent.ConcurrentBag<WriteStamp>();
            var config = new LoadConfig(nodes, rate, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(warmup), ClosedClients: clients, Trace: decompose ? writes.Add : null);
            var wall = Stopwatch.StartNew();
            var started = Stopwatch.GetTimestamp();
            var r = await LoadGenerator.RunAsync(config, CancellationToken.None).ConfigureAwait(false);
            var ended = Stopwatch.GetTimestamp();
            var wallMicros = Bench.Micros(wall.ElapsedTicks);
            var results = Bench.Summary(r.Latencies);
            if (clients is null)
            {
                results["offered_per_s"] = rate;
            }

            results["completed_per_s"] = Math.Round(r.CompletedPerSecond, 1);
            if (r.Lateness.Count > 0)
            {
                // P12-04: the generator's own lateness, each counted write's dispatch after its time.
                var late = r.Lateness.Order().ToList();
                results["generator_late_p50_us"] = Math.Round(Measurement.Percentile(late, 50), 1);
                results["generator_late_p99_us"] = Math.Round(Measurement.Percentile(late, 99), 1);
                results["generator_late_max_us"] = Math.Round(late[^1], 1);
            }

            results["incomplete"] = r.Incomplete;
            results["redirects"] = r.Redirects;

            // P13-02: every host's role changes from the measured window's start to the run's end (its
            // drain included), each election explained by a silence past the election timeout or counted.
            var windowStart = started + (long)(warmup * Stopwatch.Frequency);
            var changes = probe is not null
                ? await probe.RolesAsync().ConfigureAwait(false)
                : await FetchRolesAsync(nodes).ConfigureAwait(false);
            var roles = changes.SelectMany(n => n.Changes.Where(c => c.At >= windowStart && c.At <= ended)
                    .Select(c => new RoleRecord(n.Node, Math.Round(Bench.Micros(c.At - windowStart) / 1000, 1), c.From.ToString(), c.To.ToString(), c.Term, Math.Round(c.SilenceMs, 1), c.TickMs, c.Explained(RaftOptions.Default))))
                .OrderBy(c => c.AtMs).ToList();
            results["role_changes"] = roles.Count;
            results["elections"] = roles.Count(c => c.To == nameof(Role.Candidate) && c.From != nameof(Role.Candidate));
            results["elections_unexplained"] = roles.Count(c => !c.Explained);
            if (probe is not null)
            {
                results["generator_own_process"] = GeneratorOwnProcess(probe);
            }

            if (probe is not null && countersBefore is not null && r.Answered > 0)
            {
                var leader = await probe.LeaderAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var countersAfter = await probe.CountersAsync().ConfigureAwait(false);
                foreach (var (key, value) in LeaderFields(countersBefore, countersAfter, leader, r.Answered, wallMicros, r.Latencies.Count > 0 ? r.Latencies.Average() : 0))
                {
                    results[key] = value;
                }
            }

            // P12-03: where the latency went, segment by segment, from the hand-off stamps: in process from
            // the hosts' traces, in Compose fetched from each node with `Trace|`; both through the same bytes.
            if (decompose && r.Answered > 0)
            {
                var (traces, leader) = probe is not null
                    ? (await probe.TracesAsync().ConfigureAwait(false), await probe.LeaderAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                    : await FetchTracesAsync(nodes).ConfigureAwait(false);
                results["trace_bytes"] = traces.Values.Sum(t => (long)t.Length);
                // P15-01: one write in this many traced (by entry index); the others are not joined.
                results["trace_sample"] = probe?.TraceSample ?? 1;
                var d = Decomposition.Compute(traces.ToDictionary(t => t.Key, t => HandOffTrace.Read(t.Value)), leader, [.. writes]);
                results["decomposed_writes"] = d.Joined;
                results["decomposition_coverage"] = Math.Round((double)d.Joined / Math.Max(1, d.Writes), 4);
                results["decomposition_causality_violations"] = d.Violations;
                results["decomposition_mismatched"] = d.Mismatched;
                results["decomposition_sum_gap"] = Math.Round(d.SumGap, 4);
                foreach (var (name, count) in d.NegativeBy.Where(n => n.Value > 0))
                {
                    results[$"seg_{name}_negative"] = count;
                }

                foreach (var name in Decomposition.Segments)
                {
                    var (mean, p50, p99) = Decomposition.Stats(d.Micros[name]);
                    results[$"seg_{name}_mean_us"] = mean;
                    results[$"seg_{name}_p50_us"] = p50;
                    results[$"seg_{name}_p99_us"] = p99;
                }

                // P14-04's rule for a missed tail, written before the measurement: for the writes over the tail
                // criterion (--tail-over, else the run's own 99th percentile), their excess over the median write,
                // summed segment by segment, and the shares of the persists, the loop's queues and the rest.
                var tail = Decomposition.TailShares(d.Micros, o.TryGetValue("--tail-over", out var over) ? double.Parse(over, CultureInfo.InvariantCulture) : null);
                results["tail_over_us"] = Math.Round(tail.Over, 1);
                results["tail_writes"] = tail.Writes;
                foreach (var (group, share) in tail.Groups)
                {
                    results[$"tail_share_{group}"] = Math.Round(share, 3);
                }

                foreach (var (name, share) in tail.Segments)
                {
                    results[$"tail_share_seg_{name}"] = Math.Round(share, 3);
                }
            }

            var load = clients is null
                ? $"open loop, {rate} writes a second for {seconds} s, scheduled on a thread of its own (sleep, then spin), 64 connections, a write given up after 5 s, plain Put over 64 keys; {where}"
                : $"closed loop, {clients} clients for {seconds} s, plain Put over 64 keys; {where}";
            var recordConfig = Measurement.Config($"{warmup} s, discarded", o.GetValueOrDefault("--repetition", "1"), load, root ?? "/data");
            if (o.TryGetValue("--data-fs", out var dataFs))
            {
                // Measured from a client container, which does not mount the nodes' data: the script states it.
                recordConfig["dataFileSystem"] = dataFs;
            }

            var record = new MeasurementRecord(o["--id"], o.GetValueOrDefault("--task", "P10-04"), "commit latency and throughput", recordConfig, results, roles);
            Measurement.Write(o.GetValueOrDefault("--out", "measurements"), record);
            await Console.Out.WriteLineAsync($"{record.Id}: " + string.Join(", ", record.Results.Select(x => $"{x.Key} {x.Value}"))).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            if (local is not null)
            {
                await local.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// P15-01: 1 when the load generator ran in a process of its own, the cluster's process token against
    /// its own, else 0. Tokens, not process ids: in two containers each process can be process 1 of its own
    /// namespace, and the first separate runs read as shared for that reason. Sabotage S-proc-1 (a
    /// separate mode claimed without comparing them).
    /// </summary>
    public static double GeneratorOwnProcess(IClusterProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return probe.Instance != ProcessToken.Value ? 1 : 0;
    }

    /// <summary>
    /// The leader's fields of a load record (P10-04 to P14-03), from each host's counters read before and
    /// after the run (P15-01: the same reading whichever process the hosts run in): what the leader did in
    /// between, per answered write. Sabotage S-proc-2 (a single reading, not a difference).
    /// </summary>
    public static Dictionary<string, double> LeaderFields(IReadOnlyList<HostCounters> before, IReadOnlyList<HostCounters> after, NodeId leader, long answered, double wallMicros, double meanLatency)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var run = after.Select((c, i) => c.Minus(before[i])).ToList();
        var l = run[leader.Value - 1];
        var results = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["leader_syncs_per_write"] = Math.Round((double)l.Syncs / answered, 3),
        };

        // P11-06: the leader's sync busy fraction over the run, from its own syncs' timings.
        if (l.Syncs > 0)
        {
            results["leader_sync_us"] = Math.Round(l.SyncMicros / l.Syncs, 1);
            results["leader_sync_busy"] = Math.Round(l.SyncMicros / wallMicros, 3);
        }

        results["follower_syncs_per_write"] = Math.Round(run.Where((_, i) => i != leader.Value - 1).Average(c => (double)c.Syncs) / answered, 3);
        results["leader_messages_per_write"] = Math.Round((double)l.Messages / answered, 2);
        results["leader_bytes_per_write"] = Math.Round((double)l.Bytes / answered, 0);

        // P13-03: the leader's loop, per write: the node's handling, the effects' execution (its syncs
        // among them), and how much of the loop's busy time (the wall less its wait) the two cover.
        results["leader_loop_handle_us_per_write"] = Math.Round(l.HandleMicros / answered, 1);
        results["leader_loop_execute_us_per_write"] = Math.Round(l.ExecuteMicros / answered, 1);
        results["leader_loop_inputs_per_write"] = Math.Round((double)l.Inputs / answered, 2);
        // P14-03: the group commit's batches, their mean size in inputs.
        results["leader_loop_inputs_per_batch"] = Math.Round((double)l.Inputs / Math.Max(1, l.Batches), 2);
        results["leader_loop_busy"] = Math.Round((wallMicros - l.WaitMicros) / wallMicros, 3);
        results["leader_loop_parts_cover"] = Math.Round((l.HandleMicros + l.ExecuteMicros) / Math.Max(1, wallMicros - l.WaitMicros), 3);
        if (l.Syncs > 0)
        {
            results["leader_loop_sync_share"] = Math.Round(l.SyncMicros / Math.Max(1, l.HandleMicros + l.ExecuteMicros), 3);
        }

        // P10-05: the time the leader's sends waited behind its own persists, per such effect list,
        // and as a share of the run's mean commit latency.
        if (l.BarrierLists > 0 && meanLatency > 0)
        {
            var perList = l.BarrierMicros / l.BarrierLists;
            results["leader_barrier_us"] = Math.Round(perList, 1);
            results["leader_barrier_share_of_mean"] = Math.Round(perList / meanLatency, 3);
        }

        return results;
    }

    /// <summary>
    /// `bench cluster --local DIR --control-port P [--base-port B] [--decompose 1 [--trace-sample N]]` (P15-01): the in-process cluster in a
    /// process of its own, for a load generator in another to drive (`bench load --cluster
    /// 127.0.0.1:P`); it runs until `Stop|` is asked on its control port.
    /// </summary>
    private static async Task<int> ClusterAsync(Dictionary<string, string> o)
    {
        var cluster = new LocalCluster(o.GetValueOrDefault("--local", "/tmp/cluster"), int.Parse(o.GetValueOrDefault("--base-port", "27000"), CultureInfo.InvariantCulture));
        await using (cluster.ConfigureAwait(false))
        {
            cluster.Start(traced: o.ContainsKey("--decompose"), traceSample: int.Parse(o.GetValueOrDefault("--trace-sample", "1"), CultureInfo.InvariantCulture));
            using var control = new ClusterControl(cluster, int.Parse(o.GetValueOrDefault("--control-port", "27299"), CultureInfo.InvariantCulture));
            control.Start();
            await Console.Out.WriteLineAsync($"bench cluster: pid {Environment.ProcessId}, control port open").ConfigureAwait(false);
            await control.Stopped.ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// A Compose cluster's role changes (P13-02): each node's, fetched with `Roles|`, their ages read
    /// back on this process's clock. A node that does not answer fails the run: a record missing a
    /// node would read a leader change there as none.
    /// </summary>
    private static async Task<List<(int Node, IReadOnlyList<RoleChange> Changes)>> FetchRolesAsync(IReadOnlyDictionary<NodeId, DnsEndPoint> nodes)
    {
        var all = new List<(int, IReadOnlyList<RoleChange>)>();
        foreach (var (id, at) in nodes)
        {
            var reply = await RealClient.SendAsync(at, "Roles|", TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            all.Add((id.Value, reply?.StartsWith("ok|", StringComparison.Ordinal) == true
                ? NodeHost.DecodeRoles(reply[3..], Stopwatch.GetTimestamp())
                : throw new InvalidOperationException($"node {id}: no role changes ({reply ?? "no reply"}); is RAFT_BENCH_ROLES=1 set on the nodes?")));
        }

        return all;
    }

    /// <summary>
    /// A Compose cluster's traces (P12-03): each node's stamps, fetched with `Trace|`, and the node
    /// that says it leads. A node that does not answer, or is not traced, fails the run: a
    /// decomposition missing a node would join nothing through it.
    /// </summary>
    private static async Task<(Dictionary<NodeId, byte[]> Traces, NodeId Leader)> FetchTracesAsync(IReadOnlyDictionary<NodeId, DnsEndPoint> nodes)
    {
        var traces = new Dictionary<NodeId, byte[]>();
        NodeId? leader = null;
        foreach (var (id, at) in nodes)
        {
            var status = await RealClient.SendAsync(at, "Status|", TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            if (status?.StartsWith("ok|Leader|", StringComparison.Ordinal) == true)
            {
                leader = id;
            }

            var reply = await RealClient.SendAsync(at, "Trace|", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            traces[id] = reply?.StartsWith("ok|", StringComparison.Ordinal) == true
                ? Convert.FromBase64String(reply[3..])
                : throw new InvalidOperationException($"node {id}: no trace ({reply ?? "no reply"}); is RAFT_HANDOFF_TRACE=1 set on the nodes?");
        }

        return (traces, leader ?? throw new InvalidOperationException("no node says it leads after the run"));
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i + 1 < args.Length; i += 2)
        {
            o[args[i]] = args[i + 1];
        }

        return o;
    }

    /// <summary>"1=n1:7000,2=n2:7000" as endpoints by node id.</summary>
    private static Dictionary<NodeId, DnsEndPoint> Endpoints(string list)
    {
        var d = new Dictionary<NodeId, DnsEndPoint>();
        foreach (var item in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (id, address) = (item[..item.IndexOf('=', StringComparison.Ordinal)], item[(item.IndexOf('=', StringComparison.Ordinal) + 1)..]);
            var colon = address.LastIndexOf(':');
            d[new NodeId(int.Parse(id, CultureInfo.InvariantCulture))] = new DnsEndPoint(address[..colon], int.Parse(address[(colon + 1)..], CultureInfo.InvariantCulture));
        }

        return d;
    }
}
