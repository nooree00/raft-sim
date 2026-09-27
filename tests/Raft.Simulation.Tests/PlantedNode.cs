using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.SimRun;

namespace Raft.Simulation.Tests;

/// <summary>Bugs planted in the echo-counter protocol, for proving the shrinker (P1-11).</summary>
[Flags]
internal enum Bugs
{
    None = 0,

    /// <summary>
    /// After a node receives the same message twice (an announcement or an echo), its next announcement is sent before its
    /// persist is issued. Breaks durability only if a crash then loses that unsynced write: a
    /// duplicate delivery and a crash with unsynced writes must coincide.
    /// </summary>
    DuplicateThenCrash = 1,

    /// <summary>An announcement lower than the last one from the same peer is echoed with a wrong value (breaks echo validity at that peer).</summary>
    RegressionEcho = 2,

    /// <summary>
    /// A repeat from a peer, after a gap from that peer, makes the node echo a value the peer never
    /// announced. With FIFO links a gap comes from a drop, a repeat from a duplicate: it fails only
    /// when the drop precedes the duplicate.
    /// </summary>
    GapThenRepeat = 4,
}

/// <summary>The echo-counter node with <see cref="Bugs"/> planted by rewriting its effects.</summary>
internal sealed class PlantedNode(NodeContext ctx, Bugs bugs) : INode
{
    private const long Bogus = 1_000_000;
    private readonly EchoCounterNode _inner = new(ctx);
    private readonly Dictionary<(NodeId, char), long> _last = [];
    private readonly HashSet<NodeId> _gapFrom = [];
    private bool _sawRepeat;

    public IReadOnlyList<Effect> Handle(Input input)
    {
        var effects = _inner.Handle(input).ToList();
        switch (input)
        {
            case Tick when _sawRepeat && bugs.HasFlag(Bugs.DuplicateThenCrash):
                var append = effects.FindIndex(e => e is PersistAppend);
                if (append >= 0)
                {
                    // The announcement's sends move ahead of the persist that should precede them.
                    var sends = effects.Skip(append + 1).TakeWhile(e => e is Send).ToList();
                    effects.RemoveRange(append + 1, sends.Count);
                    effects.InsertRange(append, sends);
                    _sawRepeat = false;
                }

                break;
            case Receive r:
                var text = Encoding.ASCII.GetString(r.Payload.Span);
                var key = (r.From, text[0]);
                var value = long.Parse(text[2..], CultureInfo.InvariantCulture);
                var last = _last.GetValueOrDefault(key);
                _last[key] = value;
                if (value == last)
                {
                    _sawRepeat = true;
                }

                if (value < last && text[0] == 'P' && bugs.HasFlag(Bugs.RegressionEcho))
                {
                    var i = effects.FindIndex(e => e is Send);
                    effects[i] = new Send(r.From, Encoding.ASCII.GetBytes("E|" + (value + Bogus).ToString(CultureInfo.InvariantCulture)));
                }

                if (value > last + 1)
                {
                    _gapFrom.Add(r.From);
                }

                if (value == last && _gapFrom.Contains(r.From) && bugs.HasFlag(Bugs.GapThenRepeat))
                {
                    effects.Add(new Send(r.From, Encoding.ASCII.GetBytes("E|" + Bogus.ToString(CultureInfo.InvariantCulture))));
                }

                break;
        }

        return effects;
    }
}
