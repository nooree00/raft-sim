using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Raft.Core;

namespace Raft.Host;

/// <summary>What a load run does (P10-03, decision 3).</summary>
/// <param name="Rate">Open loop: writes a second, sent on a fixed schedule. Ignored when <paramref name="ClosedClients"/> is set.</param>
/// <param name="Warmup">Writes scheduled before this are sent but not counted.</param>
/// <param name="Connections">Open loop: the persistent connections a write waits for; one write is outstanding on each.</param>
/// <param name="ClosedClients">Closed loop (the comparison): this many clients, each sending its next write when the last is answered, timed from its send.</param>
/// <param name="Timeout">A write with no reply by then is given up (counted incomplete) and its connection reopened: a leader that loses its office drops its pending requests unanswered.</param>
public sealed record LoadConfig(IReadOnlyDictionary<NodeId, DnsEndPoint> Nodes, double Rate, TimeSpan Duration, TimeSpan Warmup, int Connections = 64, int? ClosedClients = null, TimeSpan? Timeout = null);

/// <summary>A load run's result: each counted write's latency in microseconds, and what did not complete.</summary>
public sealed record LoadResult(IReadOnlyList<double> Latencies, int Scheduled, int Incomplete, int Redirects, TimeSpan Counted)
{
    public double CompletedPerSecond => Latencies.Count / Counted.TotalSeconds;
}

/// <summary>
/// The load generator (P10-03, phase 10 decision 3). **Open loop:** writes are scheduled at a fixed
/// rate, each sent when its time comes on whichever of the persistent connections is free (waiting
/// for one if none is), and each latency runs from the scheduled time to the reply, so a stall
/// delays every write scheduled during it (no coordinated omission). **Closed loop**, for the control
/// that shows the difference: each client sends its next write when its last is answered, timed from
/// its own send, as <see cref="RealClient"/> does. Writes are plain `Put|key|value` over 64 keys; a
/// redirect is followed to the node it names. Every sample is kept, in <see cref="Stopwatch"/> ticks.
/// </summary>
public static class LoadGenerator
{
    public static async Task<LoadResult> RunAsync(LoadConfig config, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(config);
        var leader = await LeaderAsync(config.Nodes, cancel).ConfigureAwait(false);
        return config.ClosedClients is { } clients
            ? await ClosedAsync(config, leader, clients, cancel).ConfigureAwait(false)
            : await OpenAsync(config, leader, cancel).ConfigureAwait(false);
    }

    private static async Task<LoadResult> OpenAsync(LoadConfig config, NodeId leader, CancellationToken cancel)
    {
        var pool = Channel.CreateUnbounded<Connection>();
        var connections = new List<Connection>();
        for (var i = 0; i < config.Connections; i++)
        {
            var c = new Connection(config.Nodes, leader, config.Timeout ?? TimeSpan.FromSeconds(5));
            await c.ConnectAsync(cancel).ConfigureAwait(false);
            connections.Add(c);
            pool.Writer.TryWrite(c);
        }

        var latencies = new ConcurrentBag<double>();
        var redirects = 0;
        var incomplete = 0;
        var total = (int)(config.Rate * config.Duration.TotalSeconds);
        var counted = 0;
        var clock = Stopwatch.StartNew();
        var interval = Stopwatch.Frequency / config.Rate;
        var inflight = new List<Task>(total);
        try
        {
            for (var i = 0; i < total; i++)
            {
                var due = (long)(i * interval);
                var wait = due - clock.ElapsedTicks;
                if (wait > Stopwatch.Frequency / 1000)
                {
                    await Task.Delay(TimeSpan.FromTicks(wait * TimeSpan.TicksPerSecond / Stopwatch.Frequency), cancel).ConfigureAwait(false);
                }

                var count = due >= config.Warmup.TotalSeconds * Stopwatch.Frequency;
                if (count)
                {
                    counted++;
                }

                var n = i;
                inflight.Add(Task.Run(
                    async () =>
                    {
                        var c = await pool.Reader.ReadAsync(cancel).ConfigureAwait(false);
                        try
                        {
                            var (ok, followed) = await c.PutAsync(n, cancel).ConfigureAwait(false);
                            Interlocked.Add(ref redirects, followed);
                            if (!count)
                            {
                                return;
                            }

                            if (ok)
                            {
                                latencies.Add(Bench.Micros(clock.ElapsedTicks - due));
                            }
                            else
                            {
                                Interlocked.Increment(ref incomplete);
                            }
                        }
                        finally
                        {
                            pool.Writer.TryWrite(c);
                        }
                    },
                    cancel));
            }

            await Task.WhenAll(inflight).ConfigureAwait(false);
        }
        finally
        {
            foreach (var c in connections)
            {
                c.Dispose();
            }
        }

        return new LoadResult([.. latencies], counted, incomplete, redirects, config.Duration - config.Warmup);
    }

    private static async Task<LoadResult> ClosedAsync(LoadConfig config, NodeId leader, int clients, CancellationToken cancel)
    {
        var latencies = new ConcurrentBag<double>();
        var redirects = 0;
        var incomplete = 0;
        var scheduled = 0;
        var clock = Stopwatch.StartNew();
        var end = (long)(config.Duration.TotalSeconds * Stopwatch.Frequency);
        var warm = (long)(config.Warmup.TotalSeconds * Stopwatch.Frequency);
        var tasks = Enumerable.Range(0, clients).Select(k => Task.Run(
            async () =>
            {
                using var c = new Connection(config.Nodes, leader, config.Timeout ?? TimeSpan.FromSeconds(5));
                await c.ConnectAsync(cancel).ConfigureAwait(false);
                for (var n = k * 1_000_000; clock.ElapsedTicks < end; n++)
                {
                    var sent = clock.ElapsedTicks;
                    var (ok, followed) = await c.PutAsync(n, cancel).ConfigureAwait(false);
                    Interlocked.Add(ref redirects, followed);
                    if (sent < warm)
                    {
                        continue;
                    }

                    Interlocked.Increment(ref scheduled);
                    if (ok)
                    {
                        latencies.Add(Bench.Micros(clock.ElapsedTicks - sent));
                    }
                    else
                    {
                        Interlocked.Increment(ref incomplete);
                    }
                }
            },
            cancel)).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new LoadResult([.. latencies], scheduled, incomplete, redirects, config.Duration - config.Warmup);
    }

    /// <summary>The node that says it leads, asked with `Status|`; waits up to ten seconds for one.</summary>
    private static async Task<NodeId> LeaderAsync(IReadOnlyDictionary<NodeId, DnsEndPoint> nodes, CancellationToken cancel)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            foreach (var (id, at) in nodes)
            {
                var reply = await RealClient.SendAsync(at, "Status|", TimeSpan.FromSeconds(1), cancel).ConfigureAwait(false);
                if (reply is not null && reply.StartsWith("ok|Leader|", StringComparison.Ordinal))
                {
                    return id;
                }
            }

            await Task.Delay(100, cancel).ConfigureAwait(false);
        }

        throw new InvalidOperationException("no node leads");
    }

    /// <summary>One persistent connection, one write outstanding at a time, following redirects.</summary>
    private sealed class Connection(IReadOnlyDictionary<NodeId, DnsEndPoint> nodes, NodeId target, TimeSpan timeout) : IDisposable
    {
        private TcpClient? _tcp;
        private StreamReader? _reader;
        private Stream? _stream;
        private NodeId _target = target;

        public async Task ConnectAsync(CancellationToken cancel)
        {
            Dispose();
            var at = nodes[_target];
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(at.Host, at.Port, cancel).ConfigureAwait(false);
            _stream = _tcp.GetStream();
            _reader = new StreamReader(_stream, Encoding.ASCII);
        }

        /// <summary>One write; whether it was answered `ok`, and how many redirects it followed.</summary>
        public async Task<(bool Ok, int Redirects)> PutAsync(int n, CancellationToken cancel)
        {
            var line = Encoding.ASCII.GetBytes(FormattableString.Invariant($"Put|k{n % 64}|v{n}\n"));
            for (var followed = 0; followed < 5; followed++)
            {
                string? reply;
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    limit.CancelAfter(timeout);
                    try
                    {
                        await _stream!.WriteAsync(line, limit.Token).ConfigureAwait(false);
                        reply = await _reader!.ReadLineAsync(limit.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) when ((e is OperationCanceledException && !cancel.IsCancellationRequested) || e is IOException or SocketException)
                    {
                        // No reply in time, or the connection broke: given up, and the connection reopened,
                        // since a late reply would otherwise answer the next write.
                        await ReconnectAsync(cancel).ConfigureAwait(false);
                        return (false, followed);
                    }
                }

                if (reply is null)
                {
                    return (false, followed);
                }

                if (!reply.StartsWith("redirect|", StringComparison.Ordinal))
                {
                    return (reply == "ok" || reply.StartsWith("ok|", StringComparison.Ordinal), followed);
                }

                var hint = reply["redirect|".Length..];
                if (hint.Length > 1 && int.TryParse(hint.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var to) && nodes.ContainsKey(new NodeId(to)))
                {
                    _target = new NodeId(to);
                }

                await Task.Delay(20, cancel).ConfigureAwait(false);
                await ReconnectAsync(cancel).ConfigureAwait(false);
            }

            return (false, 5);
        }

        /// <summary>Reopens to the current target, or to the next node if it does not answer.</summary>
        private async Task ReconnectAsync(CancellationToken cancel)
        {
            var ids = nodes.Keys.OrderBy(k => k.Value).ToList();
            for (var tries = 0; tries < ids.Count * 3; tries++)
            {
                try
                {
                    await ConnectAsync(cancel).ConfigureAwait(false);
                    return;
                }
                catch (SocketException)
                {
                    _target = ids[(ids.IndexOf(_target) + 1) % ids.Count];
                    await Task.Delay(50, cancel).ConfigureAwait(false);
                }
            }
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _tcp?.Dispose();
            _reader = null;
            _tcp = null;
        }
    }
}
