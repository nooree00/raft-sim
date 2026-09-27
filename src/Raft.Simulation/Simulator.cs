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
    private readonly PriorityQueue<Action, (long Time, long Seq)> _queue = new();
    private readonly List<Host> _hosts = [];
    private long _seq;
    private long _now;
    private long _messageSeq;

    public Simulator(SimulationConfig config, NodeFactory factory, ulong seed)
    {
        _config = config;
        _factory = factory;
        _streams = new Streams(seed);
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
    }

    public Trace Run()
    {
        var ids = Enumerable.Range(1, _config.Nodes).Select(i => new NodeId(i)).ToList();
        foreach (var id in ids)
        {
            _hosts.Add(new Host(id));
        }

        foreach (var h in _hosts)
        {
            Start(h, ids);
            ScheduleTick(h, _config.TickInterval);
        }

        while (_queue.TryDequeue(out var action, out var key) && key.Time <= _config.Duration)
        {
            _now = key.Time;
            action();
        }

        foreach (var h in _hosts)
        {
            Trace.Add(_now, h.Id.ToString(), "FINAL", ("incarnation", h.Incarnation), ("durable", h.Disk.CompletedCount), ("disk", h.Disk.Digest()));
        }

        return Trace;
    }

    private void At(long time, Action action) => _queue.Enqueue(action, (time, ++_seq));

    private void Start(Host h, IReadOnlyList<NodeId> ids)
    {
        h.Incarnation++;
        var peers = ids.Where(p => p != h.Id).ToList();
        var random = _streams.For($"node:{h.Id}:incarnation:{h.Incarnation}");
        h.Node = _factory(new NodeContext(h.Id, peers, random, h.Disk.Snapshot()));
        h.LastTick = _now;
        Trace.Add(_now, h.Id.ToString(), "START", ("incarnation", h.Incarnation));
    }

    private void ScheduleTick(Host h, long after) => At(_now + after, () =>
    {
        if (h.Node is not null)
        {
            var elapsed = _now - h.LastTick;
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
                    var latency = Between($"disk:{h.Id}", (ulong)h.Disk.IssuedCount, _config.MinDiskLatency, _config.MaxDiskLatency);
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
                    throw new InvalidOperationException($"unknown effect {e.GetType().Name}");
            }
        }

        Release(h);
    }

    private void CompleteWrite(Host h, SimDisk.PendingWrite w)
    {
        if (h.Disk.Pending.Count == 0 || h.Disk.Pending[0].Seq != w.Seq)
        {
            return; // lost in a crash
        }

        h.Disk.CompleteNext();
        Trace.Add(_now, h.Id.ToString(), "DURABLE", ("seq", w.Seq));
        Release(h);
    }

    /// <summary>The persist barrier: release held effects, in order, whose barrier write is durable.</summary>
    private void Release(Host h)
    {
        while (h.Held.Count > 0 && h.Held.Peek().Barrier <= h.Disk.CompletedCount)
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
        var delay = Between($"delay:{from}->{s.To}", (ulong)id, _config.MinNetworkDelay, _config.MaxNetworkDelay);
        var hash = SimDisk.HashBytes(s.Payload.Span);
        Trace.Add(_now, from.ToString(), "SEND", ("to", s.To.ToString()), ("id", id), ("len", s.Payload.Length), ("h", hash));
        var to = _hosts[s.To.Value - 1];
        var payload = s.Payload;
        At(_now + delay, () =>
        {
            if (to.Node is null)
            {
                Trace.Add(_now, to.Id.ToString(), "LOST", ("id", id), ("reason", "down"));
                return;
            }

            Trace.Add(_now, to.Id.ToString(), "DELIVER", ("from", from.ToString()), ("id", id));
            Handle(to, new Receive(from, payload));
        });
    }

    private long Between(string purpose, ulong index, long min, long max) =>
        min + (long)(_streams.At(purpose, index) % (ulong)(max - min + 1));
}
