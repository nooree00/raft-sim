using System;
using System.Collections.Generic;

namespace Raft.Simulation;

/// <summary>
/// Events ordered by (time, sequence number). The sequence number is not decoration: .NET's
/// PriorityQueue is not stable, so with time alone two events at the same time come out in an order
/// that depends on everything else in the heap — and removing an unrelated fault would reorder them,
/// breaking the shrinker's premise. With the sequence, same-time events keep their insertion order.
/// </summary>
public sealed class Agenda
{
    private readonly PriorityQueue<Action, (long Time, long Seq)> _heap = new();
    private long _seq;

    public int Count => _heap.Count;

    public void Enqueue(long time, Action action) => _heap.Enqueue(action, (time, ++_seq));

    public bool TryDequeue(out Action action, out long time)
    {
        if (_heap.TryDequeue(out var a, out var key))
        {
            action = a;
            time = key.Time;
            return true;
        }

        action = () => { };
        time = 0;
        return false;
    }
}
