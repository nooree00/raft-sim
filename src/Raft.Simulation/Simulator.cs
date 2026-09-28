using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Core;

namespace Raft.Simulation;

/// <summary>
/// Runs nodes under a logical clock (spec §7). One event queue ordered by (time, sequence number) —
/// never by the heap alone, which is not stable. Nodes are driven only through the Core interface:
/// ticks, deliveries, and their ordered effects, with the persist barrier enforced per node.
/// </summary>
public sealed class Simulator
{
    private readonly SimulationConfig _config;
    private readonly NodeFactory _factory;
    private readonly Streams _streams;
    private readonly Agenda _queue = new();
    private readonly List<Host> _hosts = [];
    private readonly FaultSchedule _schedule;
    private readonly List<LinkFault> _armed = [];
    private readonly HashSet<(NodeId From, NodeId To)> _blocked = [];
    private readonly Dictionary<(NodeId From, NodeId To), Action> _held = [];
    private readonly Dictionary<(NodeId From, NodeId To), long> _lastDelivery = [];
    private readonly Dictionary<NodeId, int> _isolated = [];
    private readonly List<CrashWhenInFlight> _armedCrashes = [];
    private readonly List<CrashAfterWrite> _armedAfterWrite = [];
    private bool _fifo;
    private long _now;
    private long _messageSeq;

    public Simulator(SimulationConfig config, NodeFactory factory, ulong seed, FaultSchedule? schedule = null)
    {
        _config = config;
        _factory = factory;
        _streams = new Streams(seed);
        _schedule = schedule ?? FaultSchedule.Empty;
    }

    public Trace Trace { get; } = new();

    /// <summary>Record <see cref="Observations"/> (P3-04). Off by default: an unobserved run is unchanged.</summary>
    public bool Observe { get; init; }

    private readonly List<Observation> _observations = [];
    private readonly Dictionary<NodeId, Dictionary<string, byte[]>> _observedDisk = [];

    public IReadOnlyList<Observation> Observations => _observations;

    /// <summary>Records every durable file of the node whose content changed since it was last recorded.</summary>
    private void ObserveDisk(Host h)
    {
        if (!_observedDisk.TryGetValue(h.Id, out var seen))
        {
            _observedDisk[h.Id] = seen = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }

        var now = h.Disk.Snapshot();
        foreach (var (file, content) in now.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var bytes = content.ToArray();
            if (!seen.TryGetValue(file, out var before) || !Enumerable.SequenceEqual(before, bytes))
            {
                seen[file] = bytes;
                _observations.Add(new DurableObservation(_now, h.Id, file, bytes));
            }
        }

        foreach (var gone in seen.Select(kv => kv.Key).Where(f => !now.ContainsKey(f)).Order(StringComparer.Ordinal).ToList())
        {
            seen.Remove(gone);
            _observations.Add(new DurableObservation(_now, h.Id, gone, null));
        }
    }

    private readonly List<ClientReply> _requests = [];
    private readonly List<ClientReply> _responses = [];
    private readonly Dictionary<Type, long> _inputs = [];
    private readonly Dictionary<Type, long> _effects = [];

    /// <summary>A client message: a request to submit, or a response that reached the client side (released by the barrier from a live node).</summary>
    public sealed record ClientReply(long Time, NodeId Node, long RequestId, ReadOnlyMemory<byte> Payload);

    public IReadOnlyList<ClientReply> Responses => _responses;

    /// <summary>
    /// How many of each Core input the simulator delivered to a node, and of each effect it acted on
    /// (P2-03): use, not existence. An effect built by a node and then discarded never counts.
    /// </summary>
    public IReadOnlyDictionary<string, long> Census =>
        _inputs.Select(kv => ("input:" + kv.Key.Name, kv.Value)).Concat(_effects.Select(kv => ("effect:" + kv.Key.Name, kv.Value)))
            .ToDictionary(x => x.Item1, x => x.Value, StringComparer.Ordinal);

    /// <summary>Schedules a client request to arrive at a node (P2-03's client path; P2-08 adds clients over the network).</summary>
    public void Submit(long at, NodeId node, long requestId, ReadOnlyMemory<byte> payload) => _requests.Add(new ClientReply(at, node, requestId, payload));

    /// <summary>Counted by type, not by a name built per step: ticks are nearly every step.</summary>
    private static void Count(Dictionary<Type, long> counts, Type type) => counts[type] = counts.GetValueOrDefault(type) + 1;

    /// <summary>Clients are network endpoints n101, n102, ...: every link fault can name their links.</summary>
    public const int ClientBase = 100;

    public static NodeId ClientNode(int client) => new(ClientBase + client);

    private readonly List<ClientOp> _clientLog = [];
    private readonly Dictionary<long, int> _clientOpOf = [];
    private readonly Dictionary<int, IRandomSource> _clientRandom = [];
    private readonly Dictionary<int, int> _clientSequence = [];
    private long _clientRequestSeq = 1L << 40;

    /// <summary>Set before <see cref="Run"/> when <see cref="SimulationConfig.Clients"/> is positive.</summary>
    public IClientWorkload? Workload { get; set; }

    /// <summary>Every client operation, in invocation order (P2-08's history, before interpretation).</summary>
    public IReadOnlyList<ClientOp> ClientLog => _clientLog;

    /// <summary>The client log as text, one line per operation: written beside the trace, byte-identical for a schedule.</summary>
    public string ClientLogText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var op in _clientLog)
        {
            sb.Append("op client=").Append(Inv(op.Client)).Append(" request=").Append(Inv(op.RequestId)).Append(" node=").Append(op.Node)
                .Append(" invoke=").Append(Inv(op.Invoke)).Append(" response=").Append(op.Response is { } r ? Inv(r) : "none")
                .Append(" req=").Append(Hex(op.Request.Span)).Append(" reply=").Append(Hex(op.Reply.Span)).Append('\n');
        }

        return sb.ToString();
    }

    private static string Inv(long v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        const string digits = "0123456789abcdef";
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[2 * i] = digits[bytes[i] >> 4];
            chars[(2 * i) + 1] = digits[bytes[i] & 15];
        }

        return new string(chars);
    }

    /// <summary>A client's next operation: sent over the network to its node, with a timeout.</summary>
    private void IssueNext(int client)
    {
        if (!_clientRandom.TryGetValue(client, out var random))
        {
            random = _clientRandom[client] = _streams.For("client:" + Inv(client));
        }

        var sequence = _clientSequence.GetValueOrDefault(client);
        _clientSequence[client] = sequence + 1;
        if (Workload?.NextCall(client, sequence, random) is not { } call)
        {
            return;
        }

        var requestId = ++_clientRequestSeq;
        var index = _clientLog.Count;
        _clientLog.Add(new ClientOp(client, requestId, call.Node, call.Request, _now, null, ReadOnlyMemory<byte>.Empty));
        _clientOpOf[requestId] = index;
        var from = ClientNode(client);
        var msg = ++_messageSeq;
        Trace.Add(_now, from.ToString(), "INVOKE", ("to", call.Node.ToString()), ("request", requestId), ("id", msg), ("len", call.Request.Length));
        var host = _hosts[call.Node.Value - 1];
        Carry(from, call.Node, msg, () => ArriveAtNode(host, from, msg, () =>
        {
            Trace.Add(_now, host.Id.ToString(), "REQUEST", ("request", requestId), ("len", call.Request.Length));
            Handle(host, new ClientRequest(requestId, call.Request));
        }));
        At(_now + _config.ClientTimeout, () =>
        {
            if (_clientLog[index].Response is null && !_timedOut.Contains(requestId))
            {
                _timedOut.Add(requestId);
                Trace.Add(_now, from.ToString(), "TIMEOUT", ("request", requestId));
                At(_now + 1, () => IssueNext(client));
            }
        });
    }

    private readonly HashSet<long> _timedOut = [];

    /// <summary>A released response travels back to its client over the network; late ones are ignored.</summary>
    private void ReplyToClient(NodeId node, ClientResponse r)
    {
        if (!_clientOpOf.TryGetValue(r.RequestId, out var index))
        {
            return; // a request injected with Submit, not by a client
        }

        var op = _clientLog[index];
        var to = ClientNode(op.Client);
        var msg = ++_messageSeq;
        var payload = r.Payload;
        Carry(node, to, msg, () =>
        {
            if (_clientLog[index].Response is null && !_timedOut.Contains(op.RequestId))
            {
                _clientLog[index] = _clientLog[index] with { Response = _now, Reply = payload };
                Trace.Add(_now, to.ToString(), "REPLY", ("request", op.RequestId), ("id", msg), ("len", payload.Length));
                // One unit later: real-time order is strict, so an operation invoked at the instant
                // the previous one responded would count as concurrent with it.
                At(_now + 1, () => IssueNext(op.Client));
            }
            else
            {
                Trace.Add(_now, to.ToString(), "REPLY-IGNORED", ("request", op.RequestId), ("id", msg));
            }
        });
    }

    public long Steps { get; private set; }

    public long Now => _now;

    public IReadOnlyList<SimDisk> Disks => _hosts.Select(h => h.Disk).ToList();

    /// <summary>A node's runtime: its instance (null while crashed), disk, and sends held by the barrier.</summary>
    private sealed class Host(NodeId id)
    {
        public NodeId Id { get; } = id;

        public INode? Node { get; set; }

        public int Incarnation { get; set; }

        public SimDisk Disk { get; } = new();

        /// <summary>Sends and client responses waiting for every earlier persist to be durable.</summary>
        public Queue<(Effect Effect, long Barrier)> Held { get; } = new();

        public long LastTick { get; set; }

        public long SlowLatency { get; set; }

        public long SlowUntil { get; set; }

        public long ViolateBarrierUntil { get; set; } = -1;

        public bool Paused { get; set; }

        /// <summary>Messages that arrived while paused, delivered in arrival order on resume.</summary>
        public Queue<Action> Backlog { get; } = new();

        public long SkewNumerator { get; set; } = 1;

        public long SkewDenominator { get; set; } = 1;

        /// <summary>Remainder carried between ticks so a skewed clock loses nothing to rounding.</summary>
        public long SkewCarry { get; set; }
    }

    private List<NodeId> _ids = [];

    public Trace Run()
    {
        var ids = Enumerable.Range(1, _config.Nodes).Select(i => new NodeId(i)).ToList();
        _ids = ids;
        foreach (var id in ids)
        {
            _hosts.Add(new Host(id));
        }

        foreach (var h in _hosts)
        {
            Start(h, ids);
            ScheduleTick(h, _config.TickInterval);
        }

        foreach (var fault in _schedule.Faults)
        {
            At(fault.At, () => Inject(fault));
        }

        for (var c = 1; c <= _config.Clients; c++)
        {
            var client = c;
            At(client, () => IssueNext(client));
        }

        foreach (var req in _requests)
        {
            var (id, payload) = (req.RequestId, req.Payload);
            At(req.Time, () =>
            {
                var h = _hosts[req.Node.Value - 1];
                if (h.Node is null || h.Paused)
                {
                    Trace.Add(_now, h.Id.ToString(), "REQUEST-LOST", ("request", id), ("reason", h.Node is null ? "down" : "paused"));
                    return;
                }

                Trace.Add(_now, h.Id.ToString(), "REQUEST", ("request", id), ("len", payload.Length));
                Handle(h, new ClientRequest(id, payload));
            });
        }

        while (_queue.TryDequeue(out var action, out var time) && time <= _config.Duration)
        {
            _now = time;
            action();
        }

        foreach (var c in _armedCrashes)
        {
            Trace.Add(_now, "sim", "UNFIRED", ("kind", nameof(CrashWhenInFlight)), ("node", c.Node.ToString()), ("at", c.At));
        }

        foreach (var c in _armedAfterWrite)
        {
            Trace.Add(_now, "sim", "UNFIRED", ("kind", nameof(CrashAfterWrite)), ("node", c.Node.ToString()), ("at", c.At));
        }

        foreach (var h in _hosts)
        {
            Trace.Add(_now, h.Id.ToString(), "FINAL", ("incarnation", h.Incarnation), ("durable", h.Disk.CompletedCount), ("disk", h.Disk.Digest()));
        }

        return Trace;
    }

    private void At(long time, Action action) => _queue.Enqueue(time, action);

    private void Inject(Fault fault)
    {
        Trace.Add(_now, "sim", "FAULT", ("kind", fault.GetType().Name), ("detail", Describe(fault)));
        switch (fault)
        {
            case Partition p:
                _blocked.Add((p.From, p.To));
                break;
            case Heal h:
                _blocked.Remove((h.From, h.To));
                break;
            case LinkFault lf:
                _armed.Add(lf);
                break;
            case Crash c:
                CrashNode(_hosts[c.Node.Value - 1], c.Loss);
                break;
            case Restart r:
                var host = _hosts[r.Node.Value - 1];
                if (host.Node is null)
                {
                    Start(host, _ids);
                }

                break;
            case SlowDisk sd:
                _hosts[sd.Node.Value - 1].SlowLatency = sd.Latency;
                _hosts[sd.Node.Value - 1].SlowUntil = sd.Until;
                break;
            case BarrierViolation bv:
                _hosts[bv.Node.Value - 1].ViolateBarrierUntil = bv.Until;
                break;
            case Pause pa:
                var ph = _hosts[pa.Node.Value - 1];
                if (ph.Node is not null)
                {
                    ph.Paused = true;
                }

                break;
            case Fifo:
                _fifo = true;
                break;
            case CrashWhenInFlight cw:
                _armedCrashes.Add(cw);
                break;
            case CrashAfterWrite caw:
                _armedAfterWrite.Add(caw);
                break;
            case Isolate iso:
                _isolated[iso.Node] = _isolated.GetValueOrDefault(iso.Node) + 1;
                At(iso.Until, () => _isolated[iso.Node]--);
                break;
            case CrashAll all:
                CrashAndRestart(_hosts, DiskLoss.Pending, all.Until);
                break;
            case CrashMajority maj:
                var draws = _streams.For("fault:majority:" + maj.At.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var order = _hosts.Select(h => (h, key: draws.NextUInt64())).OrderBy(x => x.key).Select(x => x.h).ToList();
                CrashAndRestart(order.Take((_hosts.Count / 2) + 1).ToList(), DiskLoss.Pending, maj.Until);
                break;
            case Unpause re:
                ResumeNode(_hosts[re.Node.Value - 1]);
                break;
            case Skew sk:
                var sh = _hosts[sk.Node.Value - 1];
                sh.SkewNumerator = sk.Numerator;
                sh.SkewDenominator = sk.Denominator;
                break;
            default:
                throw new System.ArgumentException("unknown fault " + fault.GetType().Name);
        }
    }

    private void CrashAndRestart(IReadOnlyList<Host> hosts, DiskLoss loss, long until)
    {
        foreach (var h in hosts)
        {
            CrashNode(h, loss);
        }

        At(until, () =>
        {
            foreach (var h in hosts.Where(h => h.Node is null))
            {
                Start(h, _ids);
            }
        });
    }

    /// <summary>Fires an armed state-placed crash once its node has enough writes in flight.</summary>
    private void CheckArmedCrashes(Host h)
    {
        var i = _armedCrashes.FindIndex(c => c.Node == h.Id && h.Disk.Pending.Count >= c.MinPending);
        if (i < 0)
        {
            return;
        }

        var c = _armedCrashes[i];
        _armedCrashes.RemoveAt(i);
        // After the current step: its writes are issued and still in flight (they complete later).
        At(_now, () =>
        {
            Trace.Add(_now, h.Id.ToString(), "FIRED", ("kind", nameof(CrashWhenInFlight)), ("pending", h.Disk.Pending.Count));
            CrashNode(h, c.Loss);
            At(_now + c.Down, () =>
            {
                if (h.Node is null)
                {
                    Start(h, _ids);
                }
            });
        });
    }

    private static string Describe(Fault f) => f switch
    {
        LinkFault l => l.From + "->" + l.To,
        Crash c => c.Node + ":" + c.Loss,
        NodeFault n => n.Node.ToString(),
        _ => "-",
    };

    private void ResumeNode(Host h)
    {
        if (!h.Paused)
        {
            return;
        }

        h.Paused = false;
        var elapsed = Perceived(h, _now - h.LastTick);
        h.LastTick = _now;
        Trace.Add(_now, h.Id.ToString(), "RESUME", ("tick", elapsed), ("backlog", h.Backlog.Count));
        Handle(h, new Tick(elapsed));
        while (h.Backlog.Count > 0 && h.Node is not null && !h.Paused)
        {
            h.Backlog.Dequeue()();
        }
    }

    /// <summary>Real elapsed units as this node's skewed clock perceives them, carrying the remainder.</summary>
    private static long Perceived(Host h, long real)
    {
        var scaled = (real * h.SkewNumerator) + h.SkewCarry;
        h.SkewCarry = scaled % h.SkewDenominator;
        return scaled / h.SkewDenominator;
    }

    private void CrashNode(Host h, DiskLoss loss)
    {
        if (h.Node is null)
        {
            return;
        }

        h.Node = null;
        h.Paused = false;
        h.Backlog.Clear();
        var dropped = h.Held.Count;
        h.Held.Clear();
        var draws = _streams.For(Purpose("crash", h.Id, null, h.Incarnation));
        var disk = h.Disk.Crash(loss, draws.NextUInt64);
        Trace.Add(_now, h.Id.ToString(), "CRASH", [("loss", (object)loss.ToString()), .. disk, ("unsent", dropped)]);
        if (Observe)
        {
            _observations.Add(new CrashObservation(_now, h.Id));
            ObserveDisk(h);
        }
    }

    /// <summary>The earliest armed one-shot fault on a link, consumed by the first message it meets.</summary>
    private LinkFault? TakeArmed(NodeId from, NodeId to)
    {
        var i = _armed.FindIndex(f => f.From == from && f.To == to);
        if (i < 0)
        {
            return null;
        }

        var f = _armed[i];
        _armed.RemoveAt(i);
        return f;
    }

    private void Start(Host h, IReadOnlyList<NodeId> ids)
    {
        h.Incarnation++;
        var peers = ids.Where(p => p != h.Id).ToList();
        var random = _streams.For(Purpose("node", h.Id, "incarnation", h.Incarnation));
        h.Node = _factory(new NodeContext(h.Id, peers, random, h.Disk.Snapshot()));
        h.LastTick = _now;
        Trace.Add(_now, h.Id.ToString(), "START", ("incarnation", h.Incarnation));
        if (Observe)
        {
            _observations.Add(new StartObservation(_now, h.Id, h.Incarnation));
        }
    }

    private void ScheduleTick(Host h, long after) => At(_now + after, () =>
    {
        if (h.Node is not null && !h.Paused)
        {
            var elapsed = Perceived(h, _now - h.LastTick);
            h.LastTick = _now;
            Handle(h, new Tick(elapsed));
        }

        ScheduleTick(h, _config.TickInterval);
    });

    private void Handle(Host h, Input input)
    {
        Steps++;
        Count(_inputs, input.GetType());
        var effects = h.Node!.Handle(input);
        foreach (var e in effects)
        {
            switch (e)
            {
                case Persist p:
                    var latency = Between("disk:" + h.Id, (ulong)h.Disk.IssuedCount, _config.MinDiskLatency, _config.MaxDiskLatency);
                    if (_now < h.SlowUntil)
                    {
                        latency = System.Math.Max(latency, h.SlowLatency);
                    }

                    var w = h.Disk.Issue(p, _now + latency);
                    Trace.Add(_now, h.Id.ToString(), "PERSIST", ("seq", w.Seq), ("op", p.GetType().Name), ("file", p.File));
                    Count(_effects, p.GetType());
                    At(w.CompleteAt, () => CompleteWrite(h, w));
                    CheckArmedCrashes(h);
                    break;
                case Send or ClientResponse:
                    h.Held.Enqueue((e, h.Disk.IssuedCount));
                    break;
                case Emit ev:
                    Count(_effects, typeof(Emit));
                    Trace.Add(_now, h.Id.ToString(), "EVENT", [("name", (object)ev.Name), .. ev.Fields.Select(f => (f.Key, (object)f.Value))]);
                    break;
                default:
                    throw new InvalidOperationException("unknown effect " + e.GetType().Name);
            }
        }

        Release(h);
    }

    private void CompleteWrite(Host h, SimDisk.PendingWrite w)
    {
        if (h.Disk.Pending.Count == 0 || h.Disk.Pending[0].Seq != w.Seq)
        {
            return; // lost in a crash (writes complete in issue order, so nothing else can be first)
        }

        h.Disk.CompleteNext();
        Trace.Add(_now, h.Id.ToString(), "DURABLE", ("seq", w.Seq));
        if (Observe)
        {
            ObserveDisk(h);
        }

        Release(h);
        FireAfterWrite(h, w);
    }

    /// <summary>Fires an armed <see cref="CrashAfterWrite"/>: after this write is durable and what it held back has been released.</summary>
    private void FireAfterWrite(Host h, SimDisk.PendingWrite w)
    {
        var i = _armedAfterWrite.FindIndex(c => c.Node == h.Id);
        if (i < 0)
        {
            return;
        }

        var c = _armedAfterWrite[i];
        _armedAfterWrite.RemoveAt(i);
        At(_now, () =>
        {
            Trace.Add(_now, h.Id.ToString(), "FIRED", ("kind", nameof(CrashAfterWrite)), ("after", w.Seq));
            CrashNode(h, c.Loss);
            At(_now + c.Down, () =>
            {
                if (h.Node is null)
                {
                    Start(h, _ids);
                }
            });
        });
    }

    /// <summary>The persist barrier: release held effects, in order, whose barrier write is durable.</summary>
    private void Release(Host h)
    {
        var violate = _now < h.ViolateBarrierUntil;
        while (h.Held.Count > 0 && (violate || h.Held.Peek().Barrier <= h.Disk.CompletedCount))
        {
            var (effect, _) = h.Held.Dequeue();
            if (effect is Send s)
            {
                Count(_effects, typeof(Send));
                Transmit(h.Id, s);
            }
            else if (effect is ClientResponse r)
            {
                // Until P2-03 this branch did not exist: every client response was dropped here.
                Count(_effects, typeof(ClientResponse));
                Trace.Add(_now, h.Id.ToString(), "RESPONSE", ("request", r.RequestId), ("len", r.Payload.Length), ("h", SimDisk.HashBytes(r.Payload.Span)));
                _responses.Add(new ClientReply(_now, h.Id, r.RequestId, r.Payload));
                ReplyToClient(h.Id, r);
            }
        }
    }

    private void Transmit(NodeId from, Send s)
    {
        var id = ++_messageSeq;
        Trace.Add(_now, from.ToString(), "SEND", ("to", s.To.ToString()), ("id", id), ("len", s.Payload.Length), ("h", SimDisk.HashBytes(s.Payload.Span)));
        if (Observe)
        {
            _observations.Add(new SentObservation(_now, from, s.To, id, s.Payload));
        }
        var to = _hosts[s.To.Value - 1];
        var payload = s.Payload;
        Carry(from, s.To, id, () => ArriveAtNode(to, from, id, () => Handle(to, new Receive(from, payload))));
    }

    /// <summary>A message arriving at a node: lost if the node is down, held in its backlog if paused.</summary>
    private void ArriveAtNode(Host to, NodeId from, long id, Action handle)
    {
        if (to.Node is null)
        {
            Trace.Add(_now, to.Id.ToString(), "LOST", ("id", id), ("reason", "down"));
            return;
        }

        if (to.Paused)
        {
            Trace.Add(_now, to.Id.ToString(), "BACKLOG", ("from", from.ToString()), ("id", id));
            to.Backlog.Enqueue(() =>
            {
                Trace.Add(_now, to.Id.ToString(), "DELIVER", ("from", from.ToString()), ("id", id));
                ObserveDelivery(to.Id, from, id);
                handle();
            });
            return;
        }

        Trace.Add(_now, to.Id.ToString(), "DELIVER", ("from", from.ToString()), ("id", id));
        ObserveDelivery(to.Id, from, id);
        handle();
    }

    private void ObserveDelivery(NodeId to, NodeId from, long id)
    {
        if (Observe)
        {
            _observations.Add(new DeliveredObservation(_now, to, from, id));
        }
    }

    /// <summary>
    /// The network, for any message on the link From→To (between nodes, or between a client and a
    /// node): partitions and isolation block it; an armed link fault drops, duplicates, delays or
    /// holds it; FIFO links keep send order; otherwise it arrives after a jittered delay.
    /// </summary>
    private void Carry(NodeId from, NodeId to, long id, Action deliver)
    {
        var delay = Between("delay:" + from + "->" + to, (ulong)id, _config.MinNetworkDelay, _config.MaxNetworkDelay);
        var link = (from, to);
        if (_blocked.Contains(link) || _isolated.GetValueOrDefault(from) > 0 || _isolated.GetValueOrDefault(to) > 0)
        {
            Trace.Add(_now, from.ToString(), "BLOCKED", ("to", to.ToString()), ("id", id));
            return;
        }

        // A message held by an earlier Reorder on this link is released just after this one arrives.
        var releaseHeld = _held.Remove(link, out var held) ? held : null;
        var deliverAt = _now + delay;
        if (_fifo)
        {
            // In send order: never before the previous message on this link.
            deliverAt = System.Math.Max(deliverAt, _lastDelivery.GetValueOrDefault(link) + 1);
            _lastDelivery[link] = deliverAt;
        }

        switch (TakeArmed(from, to))
        {
            case Drop:
                Trace.Add(_now, from.ToString(), "DROP", ("to", to.ToString()), ("id", id));
                break;
            case Duplicate:
                Trace.Add(_now, from.ToString(), "DUP", ("to", to.ToString()), ("id", id));
                At(deliverAt, deliver);
                At(deliverAt + 1, deliver);
                break;
            case Delay d:
                Trace.Add(_now, from.ToString(), "DELAYED", ("to", to.ToString()), ("id", id), ("extra", d.Extra));
                At(deliverAt + d.Extra, deliver);
                break;
            case Reorder:
                Trace.Add(_now, from.ToString(), "HELD", ("to", to.ToString()), ("id", id));
                _held[link] = deliver;
                break;
            default:
                At(deliverAt, deliver);
                break;
        }

        if (releaseHeld is not null)
        {
            At(deliverAt + 1, releaseHeld);
        }
    }

    /// <summary>
    /// A stream purpose, formatted invariantly: string interpolation would format numbers with the
    /// current culture, making every random draw depend on the machine's locale.
    /// </summary>
    private static string Purpose(string kind, NodeId node, string? label, int incarnation) =>
        kind + ":" + node + ":" + (label is null ? "" : label + ":") + incarnation.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private long Between(string purpose, ulong index, long min, long max) =>
        min + (long)(_streams.At(purpose, index) % (ulong)(max - min + 1));
}
