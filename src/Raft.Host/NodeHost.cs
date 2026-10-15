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
/// <param name="AnswerRoles">Whether `Roles|` is answered (P13-02: a Compose node in a load run, RAFT_BENCH_ROLES=1); the role changes are kept either way.</param>
public sealed record HostConfig(NodeId Id, IReadOnlyDictionary<NodeId, DnsEndPoint> Peers, IPEndPoint PeerListen, IPEndPoint Client, string DataDirectory, RaftOptions Options, int TickMilliseconds = 10, Func<string, IFileSystem>? FileSystem = null, HandOffTrace? Trace = null, bool AnswerRoles = false);

/// <summary>
/// One change of a node's role (P13-02), with the host's evidence for why: how long it had read no
/// frame a leader sends (an AppendEntries or an InstallSnapshot, told by the wire form's type byte),
/// so for an election its silence from the leader, read where the bytes arrive, before its loop; and
/// the size of the clock tick it was handling (a tick past the election timeout is the clock's own
/// thread paused, a whole-process stall, not the leader's alone).
/// </summary>
public sealed record RoleChange(long At, Role From, Role To, long Term, double SilenceMs, long TickMs)
{
    /// <summary>
    /// An election the host can explain: the node had heard nothing from a leader for the shortest
    /// election timeout, to within one tick of the clock. The timer counts whole ticks of wall time,
    /// and the tick that fires it can include up to one tick from before the last heartbeat was handled,
    /// so an election at the shortest timeout can show a silence a tick short of it (S-lead-1's control
    /// failed so, once in about sixty runs). The tolerance is the clock's interval, not the size of the
    /// tick that fired: a long tick is the clock's own thread paused, which explains nothing.
    /// </summary>
    public bool Explained(RaftOptions options, int tickMilliseconds = 10) => To != Role.Candidate || From == Role.Candidate || SilenceMs + tickMilliseconds >= options.ElectionTimeoutMin;
}

/// <summary>
/// One Raft node in a process (P9-04, spec §4's host): a single thread runs the node, fed by a
/// monotonic clock, frames from its peers and requests from clients, and executes its effects on
/// real files (<see cref="DiskExecutor"/>). Peers are dialled and redialled; a message to a peer
/// that cannot be reached is dropped, which Raft tolerates as a lost message. Clients send one
/// request per line and get its reply on the same connection; `Status|` is answered by the host, and
/// so is `Trace|` (its hand-off stamps, when a bench run traces it, P12-03).
/// Core's events go to <see cref="Events"/> as JSON lines (phase 9 decision 6).
/// </summary>
public sealed class NodeHost : IAsyncDisposable
{
    /// <summary>The largest frame a host accepts: a full batch of the largest command, with room for its fixed fields.</summary>
    private readonly int _maxFrame;

    private readonly HostConfig _config;
    private readonly TextWriter _events;
    // P12-05: the loop's wait for an input completes on the thread that writes the input, which then
    // wakes the loop itself. With asynchronous continuations the completion went through the thread
    // pool, a second wake between a reader and the loop: once connections were read on threads of their
    // own, the leader's queue for client requests rose from 40 to 87 µs at its median.
    private readonly Channel<Input> _inputs = Channel.CreateUnbounded<Input>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = true });
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
    private readonly TaskCompletionSource<Exception> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile Exception? _failure;
    private long _sentMessages;
    private long _sentBytes;
    private long _stallPeriod;
    private long _stallPause;
    private long _stalledWindow = -1;
    private long _peerHandOffDelayTicks;
    private long _handOffSpunTicks;
    private long _handOffSpins;
    private volatile int _role;
    private long _term;
    private long _stallOnceTicks;
    private long _lastLeaderRead = Stopwatch.GetTimestamp();
    private readonly ConcurrentQueue<RoleChange> _roleChanges = new();
    private long _loopWaitTicks;
    private long _loopHandleTicks;
    private long _loopExecuteTicks;
    private long _loopInputs;
    private long _loopBatches;
    private readonly long[] _heldCounts = new long[6];
    private readonly long[] _heldTicks = new long[6];

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

    /// <summary>
    /// A control's hook (P13-02): the loop stops once, for <paramref name="pause"/>, before the next
    /// input it takes, as a descheduled or collecting leader would, so its followers' timers fire.
    /// </summary>
    public void StallOnce(TimeSpan pause) => Interlocked.Exchange(ref _stallOnceTicks, pause.Ticks);

    /// <summary>
    /// The loop's time so far (P13-03), from its own timers: blocked waiting for an input; in the node's
    /// handling of inputs; executing their effects (the syncs among them); and the inputs taken. Its
    /// busy time read the other way, the run's wall time less the wait, is what the two parts must cover.
    /// </summary>
    public (double WaitMicros, double HandleMicros, double ExecuteMicros, long Inputs) LoopTimes =>
        (Bench.Micros(Interlocked.Read(ref _loopWaitTicks)), Bench.Micros(Interlocked.Read(ref _loopHandleTicks)), Bench.Micros(Interlocked.Read(ref _loopExecuteTicks)), Interlocked.Read(ref _loopInputs));

    /// <summary>The batches the loop has executed (P14-03): the inputs over this is the batch's mean size.</summary>
    public long LoopBatches => Interlocked.Read(ref _loopBatches);

    /// <summary>
    /// P15-03: the outputs the loop has released, by kind and class (sends A, B, C, then responses A, B, C;
    /// <see cref="OutputClasses"/>), and the microseconds each class was held in all.
    /// </summary>
    public (long[] Counts, double[] Micros) HeldOutputs =>
        ([.. _heldCounts.Select((_, i) => Interlocked.Read(ref _heldCounts[i]))], [.. _heldTicks.Select((_, i) => Bench.Micros(Interlocked.Read(ref _heldTicks[i])))]);

    /// <summary>Every change of the node's role since the host started, in order (P13-02).</summary>
    public IReadOnlyList<RoleChange> RoleChanges => [.. _roleChanges];

    /// <summary>
    /// A measurement's hook (P12-03, the decomposition's control): every peer message waits this long,
    /// never less, between its read from the socket and its hand-off to the loop, so a delay of known
    /// size and place can be planted in one segment.
    /// </summary>
    public TimeSpan PeerHandOffDelay
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _peerHandOffDelayTicks));
        set => Interlocked.Exchange(ref _peerHandOffDelayTicks, value.Ticks);
    }

    /// <summary>
    /// The planted hand-off delays so far, and the time they actually took: a thread taken off its
    /// processor mid-spin ends its spin late, so on a busy machine the delay planted is longer than
    /// the one set (the control's judge, as for the slowed sync's, P10-03).
    /// </summary>
    public (long Spins, double Micros) PeerHandOffSpun => (Interlocked.Read(ref _handOffSpins), Bench.Micros(Interlocked.Read(ref _handOffSpunTicks)));

    /// <summary>The persist barrier's cost so far (P10-05): see <see cref="DiskExecutor.Barrier"/>.</summary>
    public (long Lists, double Micros) Barrier => _executor?.Barrier ?? (0, 0);

    /// <summary>The messages and bytes this host has sent its peers (P10-04: the replication cost per write, against the offered rate).</summary>
    public (long Messages, long Bytes) Sent => (Interlocked.Read(ref _sentMessages), Interlocked.Read(ref _sentBytes));

    /// <summary>Why the host stopped itself (a failed write or sync, P10-07), or null while it runs.</summary>
    public Exception? Failure => _failure;

    /// <summary>Completes, with the failure, when the host stops itself (P10-07).</summary>
    public Task<Exception> Stopped => _stopped.Task;

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
        if (_config.Trace is { } trace)
        {
            _executor.Durable = (file, data) =>
            {
                if (file == EntryLog.FileName)
                {
                    trace.Add(HandOffTrace.Kind.Durable, 0, data);
                }
            };
        }

        var peerListener = new TcpListener(_config.PeerListen);
        var clientListener = new TcpListener(_config.Client);
        peerListener.Start();
        clientListener.Start();
        _listeners.Add(peerListener);
        _listeners.Add(clientListener);
        foreach (var peer in peers)
        {
            // P12-05: the writer's continuation runs on the thread that queues the frame, the loop, so a
            // send is written to the socket before the loop takes its next input, unless the socket is
            // full, when the write completes later and the loop goes on. Through the pool, the hand-off
            // from loop to writer was the leader's wait to send and the follower's to answer (69 and 64 µs
            // at their medians at 625 writes a second in process, P12-04).
            var queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = true });
            _outbound[peer] = queue;
            _tasks.Add(Task.Run(() => DialAsync(peer, queue.Reader)));
        }

        _tasks.Add(Task.Run(() => AcceptAsync(peerListener, ServePeer)));
        _tasks.Add(Task.Run(() => AcceptAsync(clientListener, ServeClient)));
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

    /// <summary>
    /// The inputs the loop takes at once, after the one it waited for (P14-03, the group commit): every
    /// input already queued, up to this many, run through the node in order and their effect lists
    /// executed as one batch with one sync (<see cref="DiskExecutor.ExecuteBatch"/>). An idle loop takes
    /// batches of one.
    /// </summary>
    public const int BatchLimit = 64;

    private void Loop()
    {
        var reader = _inputs.Reader;
        var batch = new List<Input>(BatchLimit);
        var lists = new List<IReadOnlyList<Effect>>(BatchLimit);
        var handled = new List<long>(BatchLimit);
        while (!_stop.IsCancellationRequested)
        {
            Input input;
            var waiting = Stopwatch.GetTimestamp();
            try
            {
                var ready = reader.WaitToReadAsync(_stop.Token).AsTask().GetAwaiter().GetResult();
                Interlocked.Add(ref _loopWaitTicks, Stopwatch.GetTimestamp() - waiting);
                if (!ready || !reader.TryRead(out input!))
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

            batch.Clear();
            batch.Add(input);
            while (batch.Count < BatchLimit && reader.TryRead(out var more))
            {
                batch.Add(more);
            }

            Stall();
            var once = Interlocked.Exchange(ref _stallOnceTicks, 0);
            if (once > 0)
            {
                Thread.Sleep(TimeSpan.FromTicks(once));
            }

            if (_config.Trace is { } trace)
            {
                foreach (var taken in batch)
                {
                    switch (taken)
                    {
                        case ClientRequest c:
                            trace.Add(HandOffTrace.Kind.ClientTaken, c.RequestId, null);
                            break;
                        case Receive r:
                            trace.Add(HandOffTrace.Kind.PeerTaken, r.From.Value, Decomposition.ArrayOf(r.Payload));
                            break;
                    }
                }
            }

            var role = (Role)_role;
            try
            {
                var handling = Stopwatch.GetTimestamp();
                lists.Clear();
                handled.Clear();
                foreach (var next in batch)
                {
                    var effects = _node!.Handle(next);
                    lists.Add(effects);
                    handled.Add(Stopwatch.GetTimestamp());
                    if (_node.Role != role)
                    {
                        // P13-02: the change and the host's evidence, read on the loop, for a load run's record;
                        // per input, so a batch with two changes records both. The term is the one the change's
                        // event announces: the batch's events are emitted only after its sync.
                        var now = Stopwatch.GetTimestamp();
                        _roleChanges.Enqueue(new RoleChange(now, role, _node.Role, TermIn(effects) ?? Interlocked.Read(ref _term), Bench.Micros(now - Interlocked.Read(ref _lastLeaderRead)) / 1000, next is Tick t ? t.Elapsed : 0));
                        role = _node.Role;
                    }
                }

                // P15-03: each output's emission, at its list's handling, with what it waits for.
                if (_config.Trace is { } emitted)
                {
                    OutputClasses.Stamp(emitted, lists, handled);
                }

                var executing = Stopwatch.GetTimestamp();
                _executor!.ExecuteBatch(lists);
                var released = Stopwatch.GetTimestamp();
                OutputClasses.Count(lists, handled, released, _heldCounts, _heldTicks);
                Interlocked.Add(ref _loopHandleTicks, executing - handling);
                Interlocked.Add(ref _loopExecuteTicks, released - executing);
                Interlocked.Add(ref _loopInputs, batch.Count);
                Interlocked.Increment(ref _loopBatches);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // P10-07, decision 7: a sync that fails leaves the file's state unknown (a retried sync can
                // report success over lost pages), so the host stops at once. Nothing the batch held back
                // is sent or answered after the failure (P14-03).
                FailStop(e);
                return;
            }

            // Published after the batch's effects ran, so a leader's announcement is written before a
            // reader can see its role (P14-01).
            _role = (int)role;
        }
    }

    /// <summary>The term an effect list announces (a role change's event carries it), read before the list runs.</summary>
    private static long? TermIn(IReadOnlyList<Effect> effects)
    {
        foreach (var e in effects)
        {
            if (e is Emit emit)
            {
                foreach (var f in emit.Fields)
                {
                    if (f.Key == "term" && long.TryParse(f.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
                    {
                        return t;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Stops the host after a failed write or sync (P10-07): records why, stops accepting peers and
    /// clients, closes every connection, and lets <see cref="Stopped"/> complete. Called on the loop's
    /// own thread, so it waits for nothing.
    /// </summary>
    private void FailStop(Exception failure)
    {
        _failure = failure;
        Emit(new Emit("host-stop", [new("reason", failure.Message)]));
        _stop.Cancel();
        _inputs.Writer.TryComplete();
        foreach (var l in _listeners)
        {
            l.Stop();
        }

        foreach (var c in _connections)
        {
            c.Dispose();
        }

        _stopped.TrySetResult(failure);
    }

    /// <summary>
    /// The planted hand-off delay (<see cref="PeerHandOffDelay"/>): never shorter than planted, slept to
    /// within 80 µs of its end and spun the rest, as the generator waits (<see cref="Pacing.WaitUntil"/>),
    /// so the hand-off control's 2 ms on two followers' readers at 300 messages a second holds about a
    /// twentieth of a processor, not 1.2 of one (the planted amount was raised at `7f4b02c`).
    /// </summary>
    private void DelayHandOff()
    {
        var ticks = Interlocked.Read(ref _peerHandOffDelayTicks);
        if (ticks <= 0)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        Pacing.Prepare();
        Pacing.WaitUntil(start + (ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond));
        Interlocked.Add(ref _handOffSpunTicks, Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref _handOffSpins);
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
        Interlocked.Increment(ref _sentMessages);
        Interlocked.Add(ref _sentBytes, s.Payload.Length);
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
                    // Stamped as the bytes are handed to the socket: a client can read them before the write returns.
                    _config.Trace?.Add(HandOffTrace.Kind.Responded, r.RequestId, null);
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
                        // Stamped as the frame is handed to the socket: the peer can read it before the write returns.
                        _config.Trace?.Add(HandOffTrace.Kind.Sent, peer.Value, frame);
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

    private async Task AcceptAsync(TcpListener listener, Action<TcpClient> serve)
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

            // P12-05: each connection is read on a thread of its own, blocking, so the kernel wakes the
            // reader when bytes arrive. Read on the pool, a line waited for the pool to run its
            // continuation: the client's hop to the leader was 112 µs at its median at 625 writes a
            // second in process, the largest host segment after the generator's change (P12-04).
            new Thread(() => serve(client)) { IsBackground = true, Name = $"raft-{_config.Id}-read" }.Start();
        }
    }

    /// <summary>A peer's frames as inputs, after its hello names it; a frame whose checksum fails closes the connection.</summary>
    private void ServePeer(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var hello = Frames.Read(stream, 4);
                if (hello is not { Length: 4 })
                {
                    return;
                }

                var from = new NodeId(BitConverter.ToInt32(hello));
                while (!_stop.IsCancellationRequested && Frames.Read(stream, _maxFrame) is { } payload)
                {
                    if (FromLeader(payload))
                    {
                        Interlocked.Exchange(ref _lastLeaderRead, Stopwatch.GetTimestamp());
                    }

                    _config.Trace?.Add(HandOffTrace.Kind.PeerRead, from.Value, payload);
                    DelayHandOff();
                    _inputs.Writer.TryWrite(new Receive(from, payload));
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidDataException)
            {
            }
        }
    }

    /// <summary>A client's requests, one per line, each answered on the same connection.</summary>
    private void ServeClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\n" };
                while (!_stop.IsCancellationRequested && reader.ReadLine() is { } line)
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

                    if (line == "Roles|")
                    {
                        // P13-02: a Compose node's role changes, for a load run's record; each as its age now.
                        var reply = _config.AnswerRoles ? "ok|" + EncodeRoles(RoleChanges, Stopwatch.GetTimestamp()) : "error|roles not answered";
                        lock (writer)
                        {
                            writer.Write(reply + "\n");
                            writer.Flush();
                        }

                        continue;
                    }

                    if (line == "Trace|")
                    {
                        // P12-03: a Compose node's hand-off stamps, for the bench to join after a run.
                        var reply = _config.Trace is { } trace ? "ok|" + Convert.ToBase64String(HandOffTrace.Write(trace.Snapshot())) : "error|not traced";
                        lock (writer)
                        {
                            writer.Write(reply + "\n");
                            writer.Flush();
                        }

                        continue;
                    }

                    var id = Interlocked.Increment(ref _requests);
                    _pending[id] = writer;
                    var command = Encoding.ASCII.GetBytes(line);
                    _config.Trace?.Add(HandOffTrace.Kind.ClientRead, id, command);
                    _inputs.Writer.TryWrite(new ClientRequest(id, command));
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Whether a frame is one only a leader sends: the wire form's type byte (docs/design/node-interface.md
    /// §6) is 3 for AppendEntries and 5 for InstallSnapshot. Read here, not decoded: one byte, on the
    /// reader's thread, for the role record's silence (P13-02); a test holds it to the codec.
    /// </summary>
    public static bool FromLeader(ReadOnlySpan<byte> payload) => payload.Length > 0 && payload[0] is 3 or 5;

    /// <summary>`Roles|`'s answer: each change as "age-ms,from,to,term,silence-ms,tick-ms", separated by ';' (P13-02).</summary>
    public static string EncodeRoles(IEnumerable<RoleChange> changes, long now) =>
        string.Join(";", changes.Select(c => string.Create(CultureInfo.InvariantCulture, $"{Bench.Micros(now - c.At) / 1000:F1},{c.From},{c.To},{c.Term},{c.SilenceMs:F1},{c.TickMs}")));

    /// <summary>The inverse of <see cref="EncodeRoles"/>: each change, its time given back on the reader's clock as <paramref name="now"/> less its age.</summary>
    public static IReadOnlyList<RoleChange> DecodeRoles(string text, long now) =>
        [.. text.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(item => item.Split(','))
            .Select(p => new RoleChange(now - (long)(double.Parse(p[0], CultureInfo.InvariantCulture) * Stopwatch.Frequency / 1000), Enum.Parse<Role>(p[1]), Enum.Parse<Role>(p[2]), long.Parse(p[3], CultureInfo.InvariantCulture), double.Parse(p[4], CultureInfo.InvariantCulture), long.Parse(p[5], CultureInfo.InvariantCulture)))];

    private sealed record HostEvent(long T, string Node, string Event, IReadOnlyDictionary<string, string> Fields);

    /// <summary>The host's randomness for election timeouts: the operating system's, not a seed (spec §4: Core takes it injected).</summary>
    private sealed class SystemRandom : IRandomSource
    {
        public ulong NextUInt64() => (ulong)Random.Shared.NextInt64() ^ ((ulong)Random.Shared.Next() << 63);
    }
}
