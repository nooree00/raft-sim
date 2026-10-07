using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Raft.Core;
using Raft.Kv;

namespace Raft.Host;

/// <summary>Where a host listens and whom it talks to (P9-04).</summary>
/// <param name="Peers">Every member's peer address by name, this host's own included; a name is resolved at every dial, so a peer whose name does not resolve yet is dialled again later.</param>
/// <param name="PeerListen">Where this host accepts its peers.</param>
/// <param name="Client">Where this host accepts clients.</param>
/// <param name="TickMilliseconds">How often the clock's tick is delivered; one tick unit is one millisecond.</param>
/// <param name="FileSystem">The data directory as files, for a test or a measurement to wrap (a slowed sync, P10-03; a failing one, P10-07); the real directory when absent.</param>
public sealed record HostConfig(NodeId Id, IReadOnlyDictionary<NodeId, DnsEndPoint> Peers, IPEndPoint PeerListen, IPEndPoint Client, string DataDirectory, RaftOptions Options, int TickMilliseconds = 10, Func<string, IFileSystem>? FileSystem = null);

/// <summary>
/// One Raft node in a process (P9-04, spec §4's host): a single thread runs the node, fed by a
/// monotonic clock, frames from its peers and requests from clients, and executes its effects on
/// real files (<see cref="DiskExecutor"/>). Peers are dialled and redialled; a message to a peer
/// that cannot be reached is dropped, which Raft tolerates as a lost message. Clients send one
/// request per line and get its reply on the same connection; `Status|` is answered by the host.
/// Core's events go to <see cref="Events"/> as JSON lines (phase 9 decision 6).
/// </summary>
public sealed class NodeHost : IAsyncDisposable
{
    /// <summary>The largest frame a host accepts: a full batch of the largest command, with room for its fixed fields.</summary>
    private readonly int _maxFrame;

    private readonly HostConfig _config;
    private readonly TextWriter _events;
    private readonly Channel<Input> _inputs = Channel.CreateUnbounded<Input>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<NodeId, Channel<byte[]>> _outbound = new();
    private readonly ConcurrentDictionary<long, StreamWriter> _pending = new();
    private readonly List<Task> _tasks = [];
    private readonly List<TcpListener> _listeners = [];
    private readonly ConcurrentBag<TcpClient> _connections = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _eventLock = new();
    private RaftNode? _node;
    private DiskExecutor? _executor;
    private long _requests;
    private volatile bool _holdOutbound;
    private long _stallPeriod;
    private long _stallPause;
    private long _stalledWindow = -1;
    private volatile int _role;
    private long _term;

    public NodeHost(HostConfig config, TextWriter events)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _events = events;
        _maxFrame = (int)Math.Min(int.MaxValue, ((long)config.Options.MaxEntriesPerAppend * (config.Options.MaxCommandBytes + 16)) + 1024);
    }

    /// <summary>
    /// A test's hook (P9-07, phase 9 decision 8): while set, every frame to a peer is dropped, as if
    /// the network had cut this host off from its peers but not from its clients.
    /// </summary>
    public bool HoldOutbound
    {
        get => _holdOutbound;
        set => _holdOutbound = value;
    }

    /// <summary>
    /// A measurement's hook (P10-03, the planted stall): the loop stops handling inputs for
    /// <paramref name="pause"/> at the start of every <paramref name="period"/>, as a long collection
    /// or a descheduled thread would stop it. Inputs queue meanwhile; nothing is lost.
    /// </summary>
    public void StallEvery(TimeSpan period, TimeSpan pause)
    {
        _stallPeriod = period.Ticks;
        _stallPause = pause.Ticks;
    }

    /// <summary>The node's role after the last input it handled.</summary>
    public Role Role => (Role)_role;

    /// <summary>The highest term the node has announced in an event (candidate, leader, stepping down).</summary>
    public long Term => Interlocked.Read(ref _term);

    public void Start()
    {
        var files = _config.FileSystem?.Invoke(_config.DataDirectory) ?? new DirectoryFileSystem(_config.DataDirectory);
        var peers = new List<NodeId>();
        var members = new List<NodeId>();
        foreach (var id in _config.Peers.Keys)
        {
            members.Add(id);
            if (id != _config.Id)
            {
                peers.Add(id);
            }
        }

        members.Sort((a, b) => a.Value.CompareTo(b.Value));
        _node = new RaftNode(new NodeContext(_config.Id, peers, new SystemRandom(), DiskExecutor.Load(files), members), _config.Options, new KvStateMachine());
        _executor = new DiskExecutor(files, Send, Respond, Emit);

        var peerListener = new TcpListener(_config.PeerListen);
        var clientListener = new TcpListener(_config.Client);
        peerListener.Start();
        clientListener.Start();
        _listeners.Add(peerListener);
        _listeners.Add(clientListener);
        foreach (var peer in peers)
        {
            var queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
            _outbound[peer] = queue;
            _tasks.Add(Task.Run(() => DialAsync(peer, queue.Reader)));
        }

        _tasks.Add(Task.Run(() => AcceptAsync(peerListener, ServePeerAsync)));
        _tasks.Add(Task.Run(() => AcceptAsync(clientListener, ServeClientAsync)));
        _tasks.Add(Task.Run(TickAsync));
        _tasks.Add(Task.Factory.StartNew(Loop, TaskCreationOptions.LongRunning));

        // The host's own event, not Core's: an incarnation begins (P9-08 reads restarts from it).
        Emit(new Emit("host-start", []));
    }

    /// <summary>
    /// Stops at once, as a killed process does: no input is handled after this returns, sockets are
    /// closed, and nothing is flushed that was not already synced.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _inputs.Writer.TryComplete();
        foreach (var l in _listeners)
        {
            l.Stop();
        }

        foreach (var c in _connections)
        {
            c.Dispose();
        }

        try
        {
            await Task.WhenAll(_tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private void Loop()
    {
        var reader = _inputs.Reader;
        while (!_stop.IsCancellationRequested)
        {
            Input input;
            try
            {
                if (!reader.WaitToReadAsync(_stop.Token).AsTask().GetAwaiter().GetResult() || !reader.TryRead(out input!))
                {
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_stop.IsCancellationRequested)
            {
                return;
            }

            Stall();
            _executor!.Execute(_node!.Handle(input));
            _role = (int)_node.Role;
        }
    }

    /// <summary>The planted stall (<see cref="StallEvery"/>): once per period, the loop sleeps out the pause.</summary>
    private void Stall()
    {
        var period = Interlocked.Read(ref _stallPeriod);
        if (period <= 0)
        {
            return;
        }

        var now = _clock.Elapsed.Ticks;
        var window = now / period;
        var into = now % period;
        if (window > _stalledWindow && into < Interlocked.Read(ref _stallPause))
        {
            _stalledWindow = window;
            Thread.Sleep(TimeSpan.FromTicks(Interlocked.Read(ref _stallPause) - into));
        }
    }

    private async Task TickAsync()
    {
        var last = _clock.ElapsedMilliseconds;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_config.TickMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                var now = _clock.ElapsedMilliseconds;
                if (now > last)
                {
                    _inputs.Writer.TryWrite(new Tick(now - last));
                    last = now;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Send(Send s)
    {
        if (!_holdOutbound && _outbound.TryGetValue(s.To, out var queue))
        {
            queue.Writer.TryWrite(Frames.Encode(s.Payload.Span));
        }
    }

    private void Respond(ClientResponse r)
    {
        if (_pending.TryRemove(r.RequestId, out var writer))
        {
            try
            {
                lock (writer)
                {
                    writer.Write(Encoding.ASCII.GetString(r.Payload.Span) + "\n");
                    writer.Flush();
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void Emit(Emit e)
    {
        foreach (var f in e.Fields)
        {
            if (f.Key == "term" && long.TryParse(f.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
            {
                long seen;
                do
                {
                    seen = Interlocked.Read(ref _term);
                }
                while (t > seen && Interlocked.CompareExchange(ref _term, t, seen) != seen);
            }
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in e.Fields)
        {
            fields[f.Key] = f.Value;
        }

        var line = JsonSerializer.Serialize(new HostEvent(_clock.ElapsedMilliseconds, _config.Id.ToString(), e.Name, fields));
        lock (_eventLock)
        {
            _events.WriteLine(line);
            _events.Flush();
        }
    }

    /// <summary>Dials <paramref name="peer"/>, says who this host is, and writes its frames; redials after a failure, dropping what was queued.</summary>
    private async Task DialAsync(NodeId peer, ChannelReader<byte[]> frames)
    {
        var hello = Frames.Encode(BitConverter.GetBytes(_config.Id.Value));
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true };
                _connections.Add(client);
                await client.ConnectAsync(_config.Peers[peer].Host, _config.Peers[peer].Port, _stop.Token).ConfigureAwait(false);
                var stream = client.GetStream();
                await stream.WriteAsync(hello, _stop.Token).ConfigureAwait(false);
                while (await frames.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
                {
                    while (frames.TryRead(out var frame))
                    {
                        await stream.WriteAsync(frame, _stop.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
            {
                while (frames.TryRead(out _))
                {
                }

                try
                {
                    await Task.Delay(50, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task AcceptAsync(TcpListener listener, Func<TcpClient, Task> serve)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _connections.Add(client);
            _ = Task.Run(() => serve(client));
        }
    }

    /// <summary>A peer's frames as inputs, after its hello names it; a frame whose checksum fails closes the connection.</summary>
    private async Task ServePeerAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var hello = await Frames.ReadAsync(stream, 4, _stop.Token).ConfigureAwait(false);
                if (hello is not { Length: 4 })
                {
                    return;
                }

                var from = new NodeId(BitConverter.ToInt32(hello));
                while (await Frames.ReadAsync(stream, _maxFrame, _stop.Token).ConfigureAwait(false) is { } payload)
                {
                    _inputs.Writer.TryWrite(new Receive(from, payload));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException or InvalidDataException)
            {
            }
        }
    }

    /// <summary>A client's requests, one per line, each answered on the same connection.</summary>
    private async Task ServeClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\n" };
                while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                {
                    if (line == "Status|")
                    {
                        lock (writer)
                        {
                            writer.Write($"ok|{Role}|{Term.ToString(CultureInfo.InvariantCulture)}\n");
                            writer.Flush();
                        }

                        continue;
                    }

                    var id = Interlocked.Increment(ref _requests);
                    _pending[id] = writer;
                    _inputs.Writer.TryWrite(new ClientRequest(id, Encoding.ASCII.GetBytes(line)));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private sealed record HostEvent(long T, string Node, string Event, IReadOnlyDictionary<string, string> Fields);

    /// <summary>The host's randomness for election timeouts: the operating system's, not a seed (spec §4: Core takes it injected).</summary>
    private sealed class SystemRandom : IRandomSource
    {
        public ulong NextUInt64() => (ulong)Random.Shared.NextInt64() ^ ((ulong)Random.Shared.Next() << 63);
    }
}
