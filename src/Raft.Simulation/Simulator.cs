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

        while (_queue.TryDequeue(out var action, out var time) && time <= _config.Duration)
        {
            _now = time;
            action();
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
                    At(w.CompleteAt, () => CompleteWrite(h, w));
                    break;
                case Send or ClientResponse:
                    h.Held.Enqueue((e, h.Disk.IssuedCount));
                    break;
                case Emit ev:
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
        Release(h);
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
                Transmit(h.Id, s);
            }
        }
    }

    private void Transmit(NodeId from, Send s)
    {
        var id = ++_messageSeq;
        var delay = Between("delay:" + from + "->" + s.To, (ulong)id, _config.MinNetworkDelay, _config.MaxNetworkDelay);
        var hash = SimDisk.HashBytes(s.Payload.Span);
        var link = (from, s.To);
        Trace.Add(_now, from.ToString(), "SEND", ("to", s.To.ToString()), ("id", id), ("len", s.Payload.Length), ("h", hash));
        if (_blocked.Contains(link))
        {
            Trace.Add(_now, from.ToString(), "BLOCKED", ("to", s.To.ToString()), ("id", id));
            return;
        }

        var to = _hosts[s.To.Value - 1];
        var payload = s.Payload;
        void Deliver()
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
                    Handle(to, new Receive(from, payload));
                });
                return;
            }

            Trace.Add(_now, to.Id.ToString(), "DELIVER", ("from", from.ToString()), ("id", id));
            Handle(to, new Receive(from, payload));
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

        switch (TakeArmed(from, s.To))
        {
            case Drop:
                Trace.Add(_now, from.ToString(), "DROP", ("to", s.To.ToString()), ("id", id));
                break;
            case Duplicate:
                Trace.Add(_now, from.ToString(), "DUP", ("to", s.To.ToString()), ("id", id));
                At(deliverAt, Deliver);
                At(deliverAt + 1, Deliver);
                break;
            case Delay d:
                Trace.Add(_now, from.ToString(), "DELAYED", ("to", s.To.ToString()), ("id", id), ("extra", d.Extra));
                At(deliverAt + d.Extra, Deliver);
                break;
            case Reorder:
                Trace.Add(_now, from.ToString(), "HELD", ("to", s.To.ToString()), ("id", id));
                _held[link] = Deliver;
                break;
            default:
                At(deliverAt, Deliver);
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
