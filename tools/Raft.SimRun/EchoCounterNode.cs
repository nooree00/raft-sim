using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Raft.Core;

namespace Raft.SimRun;

/// <summary>
/// The trivial protocol of phase 1 (spec §11): not Raft, but it exercises everything a fault can act
/// on. Every <see cref="Period"/> ticks the node increments a counter, persists it as a checksummed
/// record, then announces it to every peer ("P|value"); a peer echoes each announcement ("E|value").
/// Because the announcement follows the persist, the persist barrier guarantees the property checked
/// by <see cref="EchoCounterChecks"/>: a value any peer has received is durable at its sender.
/// </summary>
public sealed class EchoCounterNode : INode
{
    public const string File = "counter.log";
    public const long Period = 100;

    private readonly NodeContext _ctx;
    private long _counter;
    private long _sinceLast;

    public EchoCounterNode(NodeContext ctx)
    {
        _ctx = ctx;
        _counter = Recover(ctx.Files);
        // Stagger nodes so their periods do not align: an injected-randomness draw.
        _sinceLast = ctx.Random.NextLong(Period);
    }

    /// <summary>The recovered counter, emitted on the first input so the trace records it.</summary>
    public long Recovered { get; private set; } = -1;

    public IReadOnlyList<Effect> Handle(Input input)
    {
        var effects = new List<Effect>();
        if (Recovered < 0)
        {
            Recovered = _counter;
            effects.Add(new Emit("recovered", [new Field("value", Str(_counter))]));
        }

        switch (input)
        {
            case Tick t:
                _sinceLast += t.Elapsed;
                while (_sinceLast >= Period)
                {
                    _sinceLast -= Period;
                    _counter++;
                    effects.Add(new PersistAppend(File, Record(_counter)));
                    foreach (var p in _ctx.Peers)
                    {
                        effects.Add(new Send(p, Encoding.ASCII.GetBytes("P|" + Str(_counter))));
                    }
                }

                break;
            case Receive r:
                var text = Encoding.ASCII.GetString(r.Payload.Span);
                if (text.StartsWith("P|", StringComparison.Ordinal))
                {
                    effects.Add(new Emit("got", [new Field("from", r.From.ToString()), new Field("value", text[2..])]));
                    effects.Add(new Send(r.From, Encoding.ASCII.GetBytes("E|" + text[2..])));
                }
                else
                {
                    effects.Add(new Emit("echo", [new Field("from", r.From.ToString()), new Field("value", text[2..])]));
                }

                break;
        }

        return effects;
    }

    /// <summary>[length=8][counter, 8 bytes][checksum of the counter bytes, 4 bytes].</summary>
    public static byte[] Record(long value)
    {
        var buf = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(buf, 8);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(4), value);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), Checksum(buf.AsSpan(4, 8)));
        return buf;
    }

    /// <summary>The last valid record's value; a torn or corrupt tail is ignored.</summary>
    public static long Recover(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files)
    {
        if (!files.TryGetValue(File, out var mem))
        {
            return 0;
        }

        var span = mem.Span;
        long value = 0;
        for (var at = 0; at + 16 <= span.Length; at += 16)
        {
            var rec = span.Slice(at, 16);
            if (BinaryPrimitives.ReadInt32LittleEndian(rec) != 8 ||
                BinaryPrimitives.ReadUInt32LittleEndian(rec[12..]) != Checksum(rec.Slice(4, 8)))
            {
                break;
            }

            value = BinaryPrimitives.ReadInt64LittleEndian(rec[4..]);
        }

        return value;
    }

    private static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        var h = 2166136261u;
        foreach (var b in bytes)
        {
            h = (h ^ b) * 16777619u;
        }

        return h;
    }

    private static string Str(long v) => v.ToString(CultureInfo.InvariantCulture);
}
