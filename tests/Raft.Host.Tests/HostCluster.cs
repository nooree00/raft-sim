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

namespace Raft.Host.Tests;

/// <summary>
/// Three hosts in one test process, on loopback ports and temporary directories (P9-04, P9-05). A
/// host can be killed (disposed at once, its files left as they are) and started again from its
/// directory. Events from every host are collected in one list.
/// </summary>
internal sealed class HostCluster : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "raft-hosts-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<NodeId, IPEndPoint> _peers = [];
    private readonly Dictionary<NodeId, IPEndPoint> _clients = [];
    private readonly Dictionary<NodeId, NodeHost?> _hosts = [];
    private readonly RaftOptions _options;
    private readonly Func<string, IFileSystem>? _files;

    public HostCluster(RaftOptions? options = null, int size = 3, Func<string, IFileSystem>? files = null)
    {
        _options = options ?? RaftOptions.Default;
        _files = files;
        for (var i = 1; i <= size; i++)
        {
            var id = new NodeId(i);
            _peers[id] = new IPEndPoint(IPAddress.Loopback, FreePort());
            _clients[id] = new IPEndPoint(IPAddress.Loopback, FreePort());
            _hosts[id] = null;
        }
    }

    public IReadOnlyList<NodeId> Nodes => [.. _hosts.Keys];

    public EventLog Events { get; } = new();

    public IPEndPoint ClientEndpoint(NodeId n) => _clients[n];

    public NodeHost? Host(NodeId n) => _hosts[n];

    public void Start(NodeId n)
    {
        var peers = _peers.ToDictionary(p => p.Key, p => new DnsEndPoint("127.0.0.1", p.Value.Port));
        var host = new NodeHost(new HostConfig(n, peers, _peers[n], _clients[n], Path.Combine(_root, n.ToString()), _options, FileSystem: _files), Events);
        host.Start();
        _hosts[n] = host;
    }

    public void StartAll()
    {
        foreach (var n in Nodes)
        {
            Start(n);
        }
    }

    /// <summary>Stops a host at once, as SIGKILL would: its files stay as they are.</summary>
    public async Task KillAsync(NodeId n)
    {
        if (_hosts[n] is { } h)
        {
            _hosts[n] = null;
            await h.DisposeAsync();
        }
    }

    /// <summary>The single host that reports itself leader, waiting up to <paramref name="within"/>.</summary>
    public async Task<NodeId?> LeaderAsync(TimeSpan within, CancellationToken cancel)
    {
        var until = DateTime.UtcNow + within;
        while (DateTime.UtcNow < until)
        {
            var leaders = _hosts.Where(h => h.Value?.Role == Role.Leader).Select(h => h.Key).ToList();
            if (leaders.Count == 1)
            {
                return leaders[0];
            }

            await Task.Delay(20, cancel);
        }

        return null;
    }

    /// <summary>Sends one request to <paramref name="n"/> on a new connection and reads its reply, or null within the timeout.</summary>
    public async Task<string?> RequestAsync(NodeId n, string request, TimeSpan timeout, CancellationToken cancel)
    {
        using var client = new TcpClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(_clients[n], limit.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request + "\n"), limit.Token);
            using var reader = new StreamReader(stream, Encoding.ASCII);
            return await reader.ReadLineAsync(limit.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or IOException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var n in Nodes)
        {
            await KillAsync(n);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // Below Linux's ephemeral range (32768 to 60999), so no outgoing connection can take a port
    // between its choice and the host's bind: a port asked of the system (bind to 0, release, bind
    // again later) was taken in that gap by a parallel test's client socket (P9-09, "Address already
    // in use"). Each test process holds its own block of 100 ports, claimed by an exclusive lock on a
    // file in the temporary directory (released by the system when the process exits): a node killed
    // by one process's test frees its ports while its peers keep redialling them, and without the
    // block another process could take one and receive another cluster's frames (P10-03: controls
    // failed in the harness only beside S-kill-1's process). Inside the block each port is handed out
    // once, and one in use is skipped.
    private static readonly List<FileStream> Locks = [];
    private static readonly int Block = ClaimBlock();
    private static int _nextPort = -1;

    private static int ClaimBlock()
    {
        var dir = Path.Combine(Path.GetTempPath(), "raft-host-ports");
        Directory.CreateDirectory(dir);
        for (var b = 0; b < 120; b++)
        {
            try
            {
                Locks.Add(new FileStream(Path.Combine(dir, b.ToString(System.Globalization.CultureInfo.InvariantCulture)), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                return 20_000 + (b * 100);
            }
            catch (IOException)
            {
            }
        }

        throw new InvalidOperationException("no free block of test ports: 120 test processes hold one each");
    }

    private static int FreePort()
    {
        for (var tries = 0; tries < 100; tries++)
        {
            var port = Block + (Interlocked.Increment(ref _nextPort) % 100);
            try
            {
                using var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                return port;
            }
            catch (SocketException)
            {
            }
        }

        throw new InvalidOperationException($"no free port in this process's block from {Block}");
    }
}

/// <summary>Every host's events, one JSON line each, in the order they were written.</summary>
internal sealed class EventLog : TextWriter
{
    private readonly List<string> _lines = [];
    private readonly StringBuilder _current = new();

    public override Encoding Encoding => Encoding.UTF8;

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>A whole line at once: three hosts write here, each under its own lock, so a line must not be assembled character by character.</summary>
    public override void WriteLine(string? value)
    {
        lock (_lines)
        {
            _lines.Add(value ?? "");
        }
    }

    public override void Write(char value)
    {
        lock (_lines)
        {
            if (value == '\n')
            {
                _lines.Add(_current.ToString());
                _current.Clear();
            }
            else if (value != '\r')
            {
                _current.Append(value);
            }
        }
    }
}
