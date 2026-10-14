using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Raft.Core;

namespace Raft.Host;

/// <summary>
/// The counters a load record reads from each host (P15-01), whichever process the hosts run in: its
/// syncs and their time, the messages and bytes it sent, its persist barrier, and its loop's timers.
/// </summary>
public sealed record HostCounters(long Syncs, double SyncMicros, long Messages, long Bytes, long BarrierLists, double BarrierMicros,
    double WaitMicros, double HandleMicros, double ExecuteMicros, long Inputs, long Batches)
{
    /// <summary>This reading less an earlier one: what the host did between them.</summary>
    public HostCounters Minus(HostCounters before)
    {
        ArgumentNullException.ThrowIfNull(before);
        return new(Syncs - before.Syncs, SyncMicros - before.SyncMicros, Messages - before.Messages, Bytes - before.Bytes, BarrierLists - before.BarrierLists,
            BarrierMicros - before.BarrierMicros, WaitMicros - before.WaitMicros, HandleMicros - before.HandleMicros, ExecuteMicros - before.ExecuteMicros,
            Inputs - before.Inputs, Batches - before.Batches);
    }

    /// <summary>One host's counters as `Counters|` carries them: comma-separated, invariant.</summary>
    public string Encode() => string.Join(",", new double[] { Syncs, SyncMicros, Messages, Bytes, BarrierLists, BarrierMicros, WaitMicros, HandleMicros, ExecuteMicros, Inputs, Batches }
        .Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

    public static HostCounters Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var v = text.Split(',').Select(p => double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        if (v.Length != 11)
        {
            throw new FormatException($"host counters need 11 values: {text}");
        }

        return new((long)v[0], v[1], (long)v[2], (long)v[3], (long)v[4], v[5], v[6], v[7], v[8], (long)v[9], (long)v[10]);
    }

    /// <summary>A host's counters now.</summary>
    public static HostCounters Of(NodeHost host, CountingFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(files);
        var loop = host.LoopTimes;
        return new(files.Syncs, files.SyncMicros, host.Sent.Messages, host.Sent.Bytes, host.Barrier.Lists, host.Barrier.Micros, loop.WaitMicros, loop.HandleMicros, loop.ExecuteMicros, loop.Inputs, host.LoopBatches);
    }
}

/// <summary>
/// What a load run asks of the cluster it drives (P15-01): in this process (<see cref="LocalProbe"/>) or
/// in a process of its own (<see cref="ControlProbe"/>, the generator then sharing nothing with the hosts
/// but the machine).
/// </summary>
public interface IClusterProbe
{
    /// <summary>The process the hosts run in, by its id in its own namespace (for the record).</summary>
    int ProcessId { get; }

    /// <summary>
    /// The hosts' process by a token drawn when it started (<see cref="ProcessToken"/>): unlike a process id,
    /// unique across the containers a separate run uses, each with a namespace in which its process is 1.
    /// </summary>
    string Instance { get; }

    /// <summary>One write in this many traced, when the hosts trace (P15-01).</summary>
    int TraceSample { get; }

    IReadOnlyDictionary<NodeId, DnsEndPoint> Clients { get; }

    Task<IReadOnlyList<HostCounters>> CountersAsync();

    Task<NodeId> LeaderAsync(TimeSpan within);

    /// <summary>Each host's role changes, their times on this process's clock.</summary>
    Task<List<(int Node, IReadOnlyList<RoleChange> Changes)>> RolesAsync();

    /// <summary>Each host's hand-off trace, in its written form.</summary>
    Task<Dictionary<NodeId, byte[]>> TracesAsync();
}

/// <summary>
/// This process's token, drawn when it started (P15-01): two processes compare tokens, not ids, because
/// in two containers each process can be process 1 of its own namespace (the first separate runs read so).
/// </summary>
public static class ProcessToken
{
    public static string Value { get; } = Guid.NewGuid().ToString("N");
}

/// <summary>An in-process cluster's probe: the generator shares the hosts' process.</summary>
public sealed class LocalProbe(LocalCluster cluster) : IClusterProbe
{
    public int ProcessId => Environment.ProcessId;

    public string Instance => ProcessToken.Value;

    public int TraceSample => cluster.TraceSample;

    public IReadOnlyDictionary<NodeId, DnsEndPoint> Clients => cluster.Clients;

    public Task<IReadOnlyList<HostCounters>> CountersAsync() => Task.FromResult(cluster.Counters());

    public Task<NodeId> LeaderAsync(TimeSpan within) => cluster.LeaderAsync(within, CancellationToken.None);

    public Task<List<(int Node, IReadOnlyList<RoleChange> Changes)>> RolesAsync() =>
        Task.FromResult(cluster.Hosts.Select((h, i) => (Node: i + 1, Changes: h.RoleChanges)).ToList());

    public Task<Dictionary<NodeId, byte[]>> TracesAsync() =>
        Task.FromResult(cluster.Traces.ToDictionary(t => t.Key, t => HandOffTrace.Write(t.Value.Snapshot())));
}

/// <summary>
/// A cluster in a process of its own (`bench cluster`), asked through its control port, one request per
/// line (<see cref="ClusterControl"/>).
/// </summary>
public sealed class ControlProbe : IClusterProbe
{
    private readonly DnsEndPoint _control;

    private ControlProbe(DnsEndPoint control, int processId, string instance, int traceSample, IReadOnlyDictionary<NodeId, DnsEndPoint> clients) =>
        (_control, ProcessId, Instance, TraceSample, Clients) = (control, processId, instance, traceSample, clients);

    public int TraceSample { get; }

    public int ProcessId { get; }

    public string Instance { get; }

    public IReadOnlyDictionary<NodeId, DnsEndPoint> Clients { get; }

    /// <summary>Connects to the control port, waiting up to <paramref name="within"/> for the cluster to listen.</summary>
    public static async Task<ControlProbe> ConnectAsync(DnsEndPoint control, TimeSpan within)
    {
        var end = DateTime.UtcNow + within;
        while (true)
        {
            var reply = await RealClient.SendAsync(control, "Nodes|", TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            if (reply?.StartsWith("ok|", StringComparison.Ordinal) == true)
            {
                var parts = reply.Split('|');
                var clients = parts[4].Split(',').Select(e => e.Split('=')).ToDictionary(
                    e => new NodeId(int.Parse(e[0], CultureInfo.InvariantCulture)),
                    e => new DnsEndPoint(control.Host, int.Parse(e[1][(e[1].LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture)));
                return new ControlProbe(control, int.Parse(parts[1], CultureInfo.InvariantCulture), parts[2], int.Parse(parts[3], CultureInfo.InvariantCulture), clients);
            }

            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException($"no cluster answers on {control.Host}:{control.Port}");
            }

            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private async Task<string> AskAsync(string line, TimeSpan? timeout = null)
    {
        var reply = await RealClient.SendAsync(_control, line, timeout ?? TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        return reply?.StartsWith("ok|", StringComparison.Ordinal) == true
            ? reply[3..]
            : throw new InvalidOperationException($"the cluster's control port answered '{line}' with '{reply ?? "nothing"}'");
    }

    public async Task<IReadOnlyList<HostCounters>> CountersAsync() =>
        [.. (await AskAsync("Counters|").ConfigureAwait(false)).Split(';').Select(HostCounters.Decode)];

    public async Task<NodeId> LeaderAsync(TimeSpan within)
    {
        var end = DateTime.UtcNow + within;
        while (true)
        {
            var reply = await RealClient.SendAsync(_control, "Leader|", TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            if (reply?.StartsWith("ok|", StringComparison.Ordinal) == true)
            {
                return new NodeId(int.Parse(reply[3..], CultureInfo.InvariantCulture));
            }

            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("no host leads");
            }

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    public async Task<List<(int Node, IReadOnlyList<RoleChange> Changes)>> RolesAsync()
    {
        var all = new List<(int, IReadOnlyList<RoleChange>)>();
        foreach (var id in Clients.Keys.OrderBy(n => n.Value))
        {
            all.Add((id.Value, NodeHost.DecodeRoles(await AskAsync($"Roles|{id.Value}").ConfigureAwait(false), Stopwatch.GetTimestamp())));
        }

        return all;
    }

    public async Task<Dictionary<NodeId, byte[]>> TracesAsync()
    {
        var traces = new Dictionary<NodeId, byte[]>();
        foreach (var id in Clients.Keys)
        {
            traces[id] = Convert.FromBase64String(await AskAsync($"Trace|{id.Value}", TimeSpan.FromMinutes(2)).ConfigureAwait(false));
        }

        return traces;
    }

    /// <summary>Stops the cluster's process.</summary>
    public Task StopAsync() => RealClient.SendAsync(_control, "Stop|", TimeSpan.FromSeconds(10), CancellationToken.None);
}

/// <summary>
/// `bench cluster`'s control port (P15-01): the in-process cluster's hosts asked from another process,
/// one request per line, each answered on its connection: `Nodes|` (this process's id, its token, the
/// trace's sample and the hosts' client ports), `Counters|`, `Leader|`, `Roles|n`, `Trace|n` and `Stop|`.
/// </summary>
public sealed class ClusterControl(LocalCluster cluster, int port) : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, port);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when `Stop|` is asked.</summary>
    public Task Stopped => _stopped.Task;

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (!_stopped.Task.IsCompleted)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\n" };
                while (reader.ReadLine() is { } line)
                {
                    writer.Write(Answer(line) + "\n");
                    writer.Flush();
                    if (line == "Stop|")
                    {
                        _stopped.TrySetResult();
                        return;
                    }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>One request's answer.</summary>
    internal string Answer(string line)
    {
        var (verb, arg) = line.IndexOf('|', StringComparison.Ordinal) is var i and >= 0 ? (line[..(i + 1)], line[(i + 1)..]) : (line, "");
        int Node() => int.Parse(arg, CultureInfo.InvariantCulture);
        return verb switch
        {
            "Nodes|" => $"ok|{Environment.ProcessId}|{ProcessToken.Value}|{cluster.TraceSample}|" + string.Join(",", cluster.Clients.OrderBy(c => c.Key.Value).Select(c => $"{c.Key.Value}={c.Value.Host}:{c.Value.Port}")),
            "Counters|" => "ok|" + string.Join(";", cluster.Counters().Select(c => c.Encode())),
            "Leader|" => cluster.Hosts.Select((h, n) => (h, n)).FirstOrDefault(x => x.h.Role == Role.Leader) is { h: not null } l ? $"ok|{l.n + 1}" : "error|no leader",
            "Roles|" => "ok|" + NodeHost.EncodeRoles(cluster.Hosts[Node() - 1].RoleChanges, Stopwatch.GetTimestamp()),
            "Trace|" => cluster.Traces.TryGetValue(new NodeId(Node()), out var t) ? "ok|" + Convert.ToBase64String(HandOffTrace.Write(t.Snapshot())) : "error|not traced",
            "Stop|" => "ok|stopping",
            _ => "error|unknown request",
        };
    }

    public void Dispose() => _listener.Stop();
}
