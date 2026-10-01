using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Kv;
using Raft.Simulation;

namespace Raft.Core.Tests;

/// <summary>
/// Three real <see cref="RaftNode"/>s whose every message the test delivers or drops, with disks that
/// complete each write at once, recorded as the simulator's observations so the same checkers read
/// it (P4-04). For executions whose shape must be exact, like Figure 8, which a schedule of timed
/// faults cannot force. Each node's election timeout is fixed (minimum plus its offset), and a node
/// is ticked only when the test says so.
/// </summary>
internal sealed class ManualCluster
{
    public static readonly NodeId N1 = new(1), N2 = new(2), N3 = new(3);
    public static readonly NodeId[] All = [N1, N2, N3];

    private readonly RaftOptions _options;
    private readonly Dictionary<NodeId, ulong> _timeoutOffset;
    private readonly Dictionary<NodeId, RaftNode?> _nodes = [];
    private readonly Dictionary<NodeId, Dictionary<string, byte[]>> _files = [];
    private readonly Dictionary<NodeId, int> _incarnation = [];
    private readonly List<(long Id, NodeId From, NodeId To, byte[] Payload)> _inFlight = [];
    private readonly List<(long Id, int Client, NodeId Node, string Command, long Invoke)> _requests = [];
    private readonly Dictionary<long, (long Time, byte[] Reply)> _replies = [];
    private long _time, _step, _id, _request;

    public ManualCluster(RaftOptions options, ulong n1Offset = 0, ulong n2Offset = 149, ulong n3Offset = 0)
    {
        _options = options;
        _timeoutOffset = new() { [N1] = n1Offset, [N2] = n2Offset, [N3] = n3Offset };
        foreach (var n in All)
        {
            _files[n] = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            Start(n);
        }
    }

    public List<Observation> Observations { get; } = [];

    public Role RoleOf(NodeId n) => _nodes[n]?.Role ?? Role.Follower;

    /// <summary>The cluster's clock: one unit per input handled.</summary>
    public long Now => _time;

    private sealed class ConstantRandom(ulong value) : IRandomSource
    {
        public ulong NextUInt64() => value;
    }

    private void Start(NodeId n)
    {
        _incarnation[n] = _incarnation.GetValueOrDefault(n) + 1;
        var files = _files[n].ToDictionary(kv => kv.Key, kv => (ReadOnlyMemory<byte>)kv.Value, StringComparer.Ordinal);
        _nodes[n] = new RaftNode(new NodeContext(n, All.Where(p => p != n).ToList(), new ConstantRandom(_timeoutOffset[n]), files), _options, new KvStateMachine());
        Observations.Add(new StartObservation(_time, n, _incarnation[n]));
    }

    private void Handle(NodeId n, Input input)
    {
        _time++;
        _step++;
        foreach (var e in _nodes[n]!.Handle(input))
        {
            switch (e)
            {
                case Persist p:
                    Observations.Add(new IssuedObservation(_time, n, p, _step));
                    Apply(n, p);
                    Observations.Add(new DurableObservation(_time, n, p.File, null, p));
                    break;
                case Send s:
                    var id = ++_id;
                    _inFlight.Add((id, n, s.To, s.Payload.ToArray()));
                    Observations.Add(new SentObservation(_time, n, s.To, id, s.Payload));
                    break;
                case Emit ev:
                    Observations.Add(new EmittedObservation(_time, n, ev, _step));
                    break;
                case ClientResponse r:
                    _replies.TryAdd(r.RequestId, (_time, r.Payload.ToArray()));
                    break;
            }
        }
    }

    private void Apply(NodeId n, Persist p)
    {
        var files = _files[n];
        switch (p)
        {
            case PersistAppend a:
                files[a.File] = [.. files.GetValueOrDefault(a.File, []), .. a.Data.Span];
                break;
            case PersistTruncate t when files.TryGetValue(t.File, out var cur) && cur.Length > t.Length:
                files[t.File] = cur[..(int)t.Length];
                break;
        }
    }

    public void Tick(NodeId n, long elapsed) => Handle(n, new Tick(elapsed));

    /// <summary>Ticks a node one unit at a time until it asks for votes in a term above <paramref name="above"/>; returns that term.</summary>
    public long Stand(NodeId n, long above = 0)
    {
        for (var i = 0; i < 1_000; i++)
        {
            Tick(n, 1);
            var rv = _inFlight.Where(m => m.From == n).Select(m => MessageCodec.Decode(m.Payload)).OfType<RequestVote>().Where(r => r.Term.Value > above).LastOrDefault();
            if (rv is not null)
            {
                return rv.Term.Value;
            }
        }

        throw new InvalidOperationException(n + " did not stand within 1000 ticks");
    }

    /// <summary>Sends a client request to <paramref name="n"/> on behalf of <paramref name="client"/>; the request id.</summary>
    public long Client(NodeId n, string command, int client = 0)
    {
        _requests.Add((++_request, client, n, command, _time + 1));
        Handle(n, new ClientRequest(_request, Encoding.ASCII.GetBytes(command)));
        return _request;
    }

    /// <summary>
    /// Every client request as the simulator's client log records it (P5-02): a request still
    /// unanswered is indeterminate. A crashed or deposed leader never answers, so its pending
    /// requests stay unanswered.
    /// </summary>
    public IReadOnlyList<ClientOp> ClientLog() =>
        _requests.Select(r => _replies.TryGetValue(r.Id, out var a)
            ? new ClientOp(r.Client, r.Id, r.Node, Encoding.ASCII.GetBytes(r.Command), r.Invoke, a.Time, a.Reply)
            : new ClientOp(r.Client, r.Id, r.Node, Encoding.ASCII.GetBytes(r.Command), r.Invoke, null, ReadOnlyMemory<byte>.Empty)).ToList();

    /// <summary>Delivers, in order, every message now in flight from <paramref name="from"/> to <paramref name="to"/>; messages to a node that is down are lost.</summary>
    public int Deliver(NodeId from, NodeId to)
    {
        var batch = _inFlight.Where(m => m.From == from && m.To == to).ToList();
        foreach (var m in batch)
        {
            _inFlight.Remove(m);
            if (_nodes[to] is null)
            {
                continue;
            }

            Observations.Add(new DeliveredObservation(_time, to, from, m.Id, _step + 1));
            Handle(to, new Receive(from, m.Payload));
        }

        return batch.Count;
    }

    /// <summary>Delivers between the given nodes until nothing more is in flight among them.</summary>
    public void Settle(params NodeId[] among)
    {
        for (var round = 0; round < 10_000; round++)
        {
            var moved = 0;
            foreach (var from in among)
            {
                foreach (var to in among.Where(t => t != from))
                {
                    moved += Deliver(from, to);
                }
            }

            if (moved == 0)
            {
                return;
            }
        }

        throw new InvalidOperationException("messages did not settle");
    }

    /// <summary>Loses every message in flight from <paramref name="from"/> (to <paramref name="to"/> only, when given).</summary>
    public void Drop(NodeId from, NodeId? to = null) => _inFlight.RemoveAll(m => m.From == from && (to is null || m.To == to));

    /// <summary>The node's log as its disk holds it.</summary>
    public IReadOnlyList<StoredEntry> EntriesOf(NodeId n) => EntryLog.Recover(_files[n].GetValueOrDefault(EntryLog.FileName)).Entries;

    public void Crash(NodeId n)
    {
        _nodes[n] = null;
        _inFlight.RemoveAll(m => m.From == n || m.To == n);
        Observations.Add(new CrashObservation(_time, n));
        foreach (var (file, content) in _files[n])
        {
            Observations.Add(new DurableObservation(_time, n, file, content));
        }
    }

    public void Restart(NodeId n) => Start(n);

    public (ElectionHistory Elections, LogHistory Log) Histories()
    {
        var eh = new ElectionHistory(Observations, All.Length);
        return (eh, LogHistory.FromObservations(Observations, eh, All.Length));
    }
}
