using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Raft.Core;

namespace Raft.Host;

/// <summary>One attempt as the client saw it (P9-06): times in microseconds from the client process's one clock; no response when it gave up.</summary>
public sealed record HistoryEntry(int Client, long RequestId, int Node, string Request, long Invoke, long? Response, string Reply);

/// <summary>What the real client does (P9-06).</summary>
/// <param name="Nodes">Each node's client endpoint, by name.</param>
/// <param name="Timeout">How long an attempt waits for its reply before it is given up (and left without a response).</param>
public sealed record ClientConfig(IReadOnlyDictionary<NodeId, DnsEndPoint> Nodes, int Clients, TimeSpan Duration, TimeSpan Timeout, int Keys = 6, int Retries = 3, int Seed = 1);

/// <summary>
/// The real client (P9-06, phase 9 decisions 3 and 4): logical clients in one process, each
/// sequential, each with a session (`Register`, then `Session|id|seq|command`), sending the
/// simulator's workload mix (P4-07) over a few keys. A redirect is followed to the node it names
/// (after a pause when it names none); a reply that does not come within the timeout is given up,
/// the attempt left without a response, and the same bytes sent again to another node up to
/// <see cref="ClientConfig.Retries"/> times, as `SessionWorkload` does. A session refused
/// `unknown-session|` (evicted, P9-01) is registered again. Every attempt is recorded, stamped from
/// one <see cref="Stopwatch"/> before the request is written and after the reply is read: no node's
/// clock ever enters the history (findings, pattern 1).
/// </summary>
public static class RealClient
{
    /// <param name="clock">The history's one clock; a test passes its own, to stamp its own events (a kill) on the same scale.</param>
    public static async Task RunAsync(ClientConfig config, Action<HistoryEntry> record, CancellationToken stop, Stopwatch? clock = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        clock ??= Stopwatch.StartNew();
        var ids = 0L;
        var gate = new object();
        void Record(HistoryEntry e)
        {
            lock (gate)
            {
                record(e);
            }
        }

        var tasks = new List<Task>();
        for (var c = 0; c < config.Clients; c++)
        {
            var client = c;
            tasks.Add(Task.Run(() => LogicalAsync(config, client, clock, () => Interlocked.Increment(ref ids), Record, stop), CancellationToken.None));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task LogicalAsync(ClientConfig config, int client, Stopwatch clock, Func<long> nextId, Action<HistoryEntry> record, CancellationToken stop)
    {
        var random = new Random(HashCode.Combine(config.Seed, client));
        var nodes = new List<NodeId>(config.Nodes.Keys);
        var target = nodes[random.Next(nodes.Count)];
        long? session = null;
        var sequence = 0L;
        var op = 0;
        var end = clock.Elapsed + config.Duration;

        // One attempt: the reply, or null when it was given up. The node may change by redirect.
        async Task<string?> AttemptAsync(string request)
        {
            var id = nextId();
            var invoke = Micros(clock);
            var reply = await SendAsync(config.Nodes[target], request, config.Timeout, stop).ConfigureAwait(false);
            record(new HistoryEntry(client, id, target.Value, request, invoke, reply is null ? null : Micros(clock), reply ?? ""));
            return reply;
        }

        // An operation: attempts until a reply that is not a redirect, or the retries are spent.
        async Task<string?> OperationAsync(string request)
        {
            var timeouts = 0;
            for (var redirects = 0; redirects < 20 && !stop.IsCancellationRequested; redirects++)
            {
                var reply = await AttemptAsync(request).ConfigureAwait(false);
                if (reply is null)
                {
                    if (++timeouts > config.Retries)
                    {
                        return null;
                    }

                    target = nodes[random.Next(nodes.Count)];
                    continue;
                }

                if (!reply.StartsWith("redirect|", StringComparison.Ordinal))
                {
                    return reply;
                }

                var hint = reply["redirect|".Length..];
                if (hint.Length > 1 && hint[0] == 'n' && int.TryParse(hint.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var to) && config.Nodes.ContainsKey(new NodeId(to)))
                {
                    target = new NodeId(to);
                }
                else
                {
                    // No leader known: the cluster is electing. Try another node after a pause, not at once.
                    target = nodes[random.Next(nodes.Count)];
                    await Task.Delay(50, stop).ConfigureAwait(false);
                }
            }

            return null;
        }

        try
        {
            while (!stop.IsCancellationRequested && clock.Elapsed < end)
            {
                if (session is null)
                {
                    var registered = await OperationAsync("Register|").ConfigureAwait(false);
                    if (registered is not null && registered.StartsWith("ok|", StringComparison.Ordinal) && long.TryParse(registered.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var s))
                    {
                        session = s;
                        sequence = 0;
                    }

                    continue;
                }

                op++;
                var key = "k" + random.Next(config.Keys).ToString(CultureInfo.InvariantCulture);
                var value = FormattableString.Invariant($"c{client}s{op}");
                var command = random.Next(5) switch
                {
                    0 => "Put|" + key + "|" + value,
                    1 => "Append|" + key + "|" + value,
                    2 => "Get|" + key,
                    3 => "Delete|" + key,
                    _ => "Cas|" + key + "|-|" + value,
                };
                if (command.StartsWith("Get|", StringComparison.Ordinal))
                {
                    await OperationAsync(command).ConfigureAwait(false);
                    continue;
                }

                sequence++;
                var reply = await OperationAsync(FormattableString.Invariant($"Session|{session}|{sequence}|{command}")).ConfigureAwait(false);
                if (reply == "unknown-session|")
                {
                    session = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The clock's reading in microseconds, the history's unit.</summary>
    public static long Micros(Stopwatch clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
    }

    /// <summary>One request on a new connection; its reply line, or null if none came within <paramref name="timeout"/>.</summary>
    private static async Task<string?> SendAsync(DnsEndPoint node, string request, TimeSpan timeout, CancellationToken stop)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(node.Host, node.Port, limit.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request + "\n"), limit.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.ASCII);
            return await reader.ReadLineAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            // Refused or reset: no reply. Waiting out the timeout keeps a dead node from being hammered.
            try
            {
                await Task.Delay(timeout, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return null;
        }
    }

    /// <summary>The history as JSON lines, one attempt per line.</summary>
    public static void Write(TextWriter to, HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(to);
        to.WriteLine(JsonSerializer.Serialize(entry));
    }

    public static List<HistoryEntry> Read(TextReader from)
    {
        ArgumentNullException.ThrowIfNull(from);
        var entries = new List<HistoryEntry>();
        while (from.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                entries.Add(JsonSerializer.Deserialize<HistoryEntry>(line) ?? throw new InvalidDataException("an empty history line"));
            }
        }

        return entries;
    }
}
