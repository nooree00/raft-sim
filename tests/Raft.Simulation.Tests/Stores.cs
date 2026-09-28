using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Raft.Checker;
using Raft.Core;
using Raft.Simulation;

namespace Raft.Simulation.Tests;

/// <summary>
/// The KV request and reply bytes of P2-09's stores: "Put|k|v", "Append|k|v", "Get|k",
/// "Cas|k|expected|v" (expected "-" for absent), "Delete|k"; replies "ok" or "ok|output",
/// with "-" for an absent value.
/// </summary>
internal static class KvCodec
{
    public static byte[] Encode(OpKind kind, string key, string? value = null, string? expected = null) => Encoding.ASCII.GetBytes(kind switch
    {
        OpKind.Put => $"Put|{key}|{value}",
        OpKind.Append => $"Append|{key}|{value}",
        OpKind.Get => $"Get|{key}",
        OpKind.CompareAndSwap => $"Cas|{key}|{expected ?? "-"}|{value}",
        OpKind.Delete => $"Delete|{key}",
        _ => throw new ArgumentException(kind.ToString()),
    });

    public static Operation Decode(int client, long invoke, long? response, ReadOnlySpan<byte> request, ReadOnlySpan<byte> reply)
    {
        var p = Encoding.ASCII.GetString(request).Split('|');
        var r = Encoding.ASCII.GetString(reply).Split('|');
        string? output = r.Length > 1 ? (r[1] == "-" ? null : r[1]) : null;
        var op = p[0] switch
        {
            "Put" => new Operation(client, OpKind.Put, p[1], invoke, response, Value: p[2]),
            "Append" => new Operation(client, OpKind.Append, p[1], invoke, response, Value: p[2]),
            "Get" => new Operation(client, OpKind.Get, p[1], invoke, response),
            "Cas" => new Operation(client, OpKind.CompareAndSwap, p[1], invoke, response, Value: p[3], Expected: p[2] == "-" ? null : p[2]),
            "Delete" => new Operation(client, OpKind.Delete, p[1], invoke, response),
            _ => throw new FormatException(p[0]),
        };
        return response is null || op.Kind is OpKind.Put or OpKind.Append or OpKind.Delete ? op : op with { Output = output };
    }

    /// <summary>
    /// The client log as a checker history. A simulated client that times out moves on while its
    /// operation may still take effect at any later time, so its next operation overlaps it: a
    /// sequential client cannot express that. As in Knossos and Jepsen, a client that times out is
    /// treated as crashed, and its later operations belong to a fresh logical client.
    /// </summary>
    public static List<Operation> History(Simulator sim)
    {
        var generation = new Dictionary<int, int>();
        var history = new List<Operation>();
        foreach (var o in sim.ClientLog)
        {
            var g = generation.GetValueOrDefault(o.Client);
            history.Add(Decode((g * 1_000) + o.Client, o.Invoke, o.Response, o.Request.Span, o.Reply.Span));
            if (o.Response is null)
            {
                generation[o.Client] = g + 1;
            }
        }

        return history;
    }

    /// <summary>Applies a request to a map, returning the reply (the sequential KV semantics of spec §6).</summary>
    public static byte[] Apply(Dictionary<string, string> state, ReadOnlySpan<byte> request)
    {
        var p = Encoding.ASCII.GetString(request).Split('|');
        var present = state.TryGetValue(p[1], out var current);
        string reply;
        switch (p[0])
        {
            case "Put": state[p[1]] = p[2]; reply = "ok"; break;
            case "Append": state[p[1]] = (present ? current : "") + p[2]; reply = "ok"; break;
            case "Get": reply = "ok|" + (present ? current : "-"); break;
            case "Delete": state.Remove(p[1]); reply = "ok"; break;
            case "Cas":
                var ok = p[2] == "-" ? !present : present && current == p[2];
                if (ok)
                {
                    state[p[1]] = p[3];
                }

                reply = "ok|" + (ok ? "true" : "false");
                break;
            default: throw new FormatException(p[0]);
        }

        return Encoding.ASCII.GetBytes(reply);
    }

    public static bool IsWrite(ReadOnlySpan<byte> request) => !Encoding.ASCII.GetString(request).StartsWith("Get|", StringComparison.Ordinal);
}

/// <summary>
/// Random single-key operations over a few keys, each value unique so that a stale or lost value
/// is visible. Writes go to <see cref="WriteNode"/>, reads to <see cref="ReadNode"/>.
/// </summary>
internal sealed class KvWorkload(NodeId writeNode, NodeId readNode, int perClient, int keys = 2) : IClientWorkload
{
    public NodeId WriteNode => writeNode;

    public NodeId ReadNode => readNode;

    public ClientCall? NextCall(int client, int sequence, IRandomSource random)
    {
        if (sequence >= perClient)
        {
            return null;
        }

        var key = "k" + random.NextLong(keys).ToString(CultureInfo.InvariantCulture);
        var value = client.ToString(CultureInfo.InvariantCulture) + "." + sequence.ToString(CultureInfo.InvariantCulture);
        var (kind, node) = random.NextLong(10) switch
        {
            < 4 => (OpKind.Get, readNode),
            < 7 => (OpKind.Put, writeNode),
            < 8 => (OpKind.CompareAndSwap, writeNode),
            _ => (OpKind.Delete, writeNode),
        };
        var expected = kind == OpKind.CompareAndSwap ? (random.NextLong(3) == 0 ? null : client.ToString(CultureInfo.InvariantCulture) + "." + (sequence - 1).ToString(CultureInfo.InvariantCulture)) : null;
        return new ClientCall(node, KvCodec.Encode(kind, key, value, expected));
    }
}

/// <summary>
/// Store (a), spec §6's example: asynchronous primary-backup. The primary (n1) applies a write,
/// persists it, responds, and only then sends it to the backups; clients read from a backup (n2),
/// which answers from whatever it has received so far — a stale read whenever replication lags.
/// </summary>
internal sealed class AsyncPrimaryBackupNode(NodeContext ctx) : INode
{
    private readonly Dictionary<string, string> _state = Replay(ctx.Files);

    public IReadOnlyList<Effect> Handle(Input input)
    {
        switch (input)
        {
            case ClientRequest r:
                var reply = KvCodec.Apply(_state, r.Payload.Span);
                if (!KvCodec.IsWrite(r.Payload.Span))
                {
                    return [new ClientResponse(r.RequestId, reply)];
                }

                var effects = new List<Effect> { new PersistAppend("kv.log", Record(r.Payload.Span)), new ClientResponse(r.RequestId, reply) };
                effects.AddRange(ctx.Peers.Select(p => (Effect)new Send(p, r.Payload)));
                return effects;
            case Receive m:
                KvCodec.Apply(_state, m.Payload.Span);
                return [new PersistAppend("kv.log", Record(m.Payload.Span))];
            default:
                return [];
        }
    }

    internal static byte[] Record(ReadOnlySpan<byte> command) => [.. Encoding.ASCII.GetBytes(command.Length.ToString("D4", CultureInfo.InvariantCulture)), .. command];

    /// <summary>The state rebuilt from the log's complete records; a torn tail is ignored.</summary>
    internal static Dictionary<string, string> Replay(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files)
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!files.TryGetValue("kv.log", out var log))
        {
            return state;
        }

        var span = log.Span;
        for (var at = 0; at + 4 <= span.Length;)
        {
            if (!int.TryParse(Encoding.ASCII.GetString(span.Slice(at, 4)), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || at + 4 + n > span.Length)
            {
                break;
            }

            KvCodec.Apply(state, span.Slice(at + 4, n));
            at += 4 + n;
        }

        return state;
    }
}

/// <summary>
/// Store (b): one node that acknowledges a write before its write is durable (the response is
/// emitted ahead of the persist, so the barrier does not hold it), and recovers from its disk after
/// a crash. A crash that loses the unsynced write loses an acknowledged value.
/// </summary>
internal sealed class AckBeforeDurableNode(NodeContext ctx) : INode
{
    private readonly Dictionary<string, string> _state = AsyncPrimaryBackupNode.Replay(ctx.Files);

    public IReadOnlyList<Effect> Handle(Input input) => input is ClientRequest r
        ? KvCodec.IsWrite(r.Payload.Span)
            ? [new ClientResponse(r.RequestId, KvCodec.Apply(_state, r.Payload.Span)), new PersistAppend("kv.log", AsyncPrimaryBackupNode.Record(r.Payload.Span))]
            : [new ClientResponse(r.RequestId, KvCodec.Apply(_state, r.Payload.Span))]
        : [];
}

/// <summary>Store (c), the control: one node that persists every write before it responds, and recovers from its disk.</summary>
internal sealed class DurableSingleNode(NodeContext ctx) : INode
{
    private readonly Dictionary<string, string> _state = AsyncPrimaryBackupNode.Replay(ctx.Files);

    public IReadOnlyList<Effect> Handle(Input input) => input is ClientRequest r
        ? KvCodec.IsWrite(r.Payload.Span)
            ? [new PersistAppend("kv.log", AsyncPrimaryBackupNode.Record(r.Payload.Span)), new ClientResponse(r.RequestId, KvCodec.Apply(_state, r.Payload.Span))]
            : [new ClientResponse(r.RequestId, KvCodec.Apply(_state, r.Payload.Span))]
        : [];
}
