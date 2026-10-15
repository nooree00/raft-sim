using System;
using System.Collections.Generic;
using System.Threading;
using Raft.Core;

namespace Raft.Host;

/// <summary>
/// P15-03: what each output of a batch waits for. Node-interface §4 lets a send or a response leave once
/// every persist the node emitted before it is durable. In a batch an output is behind a persist of its
/// own list (<see cref="Hold.Own"/>, A), behind only an earlier list's persist (<see cref="Hold.Earlier"/>,
/// B), or behind none (<see cref="Hold.None"/>, C: §4 lets it leave at the batch's start, and the group
/// commit holds it until the batch's sync all the same). The observable these classes split, the leader's
/// commit-respond and leader-to-send each a sync longer under load, has two candidate causes: this hold,
/// and the replication pipeline's wait for the previous append's answer (one append in flight per
/// follower, P11-02). The emission stamps separate them: the hold runs from emission, the pipeline's wait
/// before it.
/// </summary>
public static class OutputClasses
{
    public enum Hold
    {
        Own = 0,
        Earlier = 1,
        None = 2,
    }

    /// <summary>Each output (a send or a response) of a batch's lists, in order, with its list and its class. Sabotage S-held-2.</summary>
    public static IEnumerable<(int List, Effect Output, Hold Hold)> Of(IReadOnlyList<IReadOnlyList<Effect>> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        var earlier = false;
        for (var k = 0; k < lists.Count; k++)
        {
            var own = false;
            foreach (var e in lists[k])
            {
                switch (e)
                {
                    case PersistAppend or PersistWriteAt or PersistTruncate or PersistRename or PersistDelete:
                        own = true;
                        break;
                    case Send or ClientResponse:
                        yield return (k, e, own ? Hold.Own : earlier ? Hold.Earlier : Hold.None);
                        break;
                }
            }

            earlier |= own;
        }
    }

    /// <summary>The trace's emission stamp of each output: at the time its list was handled, with its class. Sabotage S-held-1.</summary>
    public static void Stamp(HandOffTrace trace, IReadOnlyList<IReadOnlyList<Effect>> lists, IReadOnlyList<long> handled)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(handled);
        foreach (var (k, output, c) in Of(lists))
        {
            var kind = c switch
            {
                Hold.Own => HandOffTrace.Kind.EmittedOwn,
                Hold.Earlier => HandOffTrace.Kind.EmittedEarlier,
                _ => HandOffTrace.Kind.EmittedNone,
            };
            if (output is Send s)
            {
                trace.AddAt(handled[k], kind, s.To.Value, s.Payload);
            }
            else if (output is ClientResponse r)
            {
                trace.AddAt(handled[k], kind, r.RequestId, null);
            }
        }
    }

    /// <summary>
    /// Counts each class's outputs and their holds, by kind (sends first, then responses, A, B, C each):
    /// an output's hold is from its list's handling to the batch's release, except an output behind no
    /// persist, which the executor releases as it reaches it, before any write (P15-05; in a batch of one
    /// list, <see cref="DiskExecutor.Execute"/> always did): its hold ends when the batch starts executing.
    /// </summary>
    public static void Count(IReadOnlyList<IReadOnlyList<Effect>> lists, IReadOnlyList<long> handled, long executing, long released, long[] counts, long[] ticks)
    {
        ArgumentNullException.ThrowIfNull(handled);
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(ticks);
        foreach (var (k, output, c) in Of(lists))
        {
            var i = (output is Send ? 0 : 3) + (int)c;
            Interlocked.Increment(ref counts[i]);
            Interlocked.Add(ref ticks[i], (c == Hold.None ? executing : released) - handled[k]);
        }
    }
}
