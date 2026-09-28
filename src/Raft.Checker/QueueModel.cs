using System;
using System.Collections.Generic;
using System.Linq;

namespace Raft.Checker;

/// <summary>
/// FIFO queues, one per key, as axiomatised in Herlihy &amp; Wing, Fig. 3: Enq(e)/Ok() inserts e;
/// Deq()/Ok(e) requires a non-empty queue and returns and removes its first item. Deq on an empty
/// queue is undefined (a partial operation).
/// </summary>
public sealed class QueueModel : ISequentialModel<Dictionary<string, Queue<string>>>
{
    public static readonly QueueModel Instance = new();

    public Dictionary<string, Queue<string>> NewState() => new(StringComparer.Ordinal);

    public Dictionary<string, Queue<string>> Copy(Dictionary<string, Queue<string>> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.ToDictionary(kv => kv.Key, kv => new Queue<string>(kv.Value), StringComparer.Ordinal);
    }

    public string Fingerprint(Dictionary<string, Queue<string>> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var sb = new System.Text.StringBuilder();
        foreach (var key in state.Keys.Order(StringComparer.Ordinal))
        {
            KvModel.Field(sb, key);
            sb.Append('[');
            foreach (var item in state[key])
            {
                KvModel.Field(sb, item);
            }

            sb.Append(']');
        }

        return sb.ToString();
    }

    public string? Apply(Dictionary<string, Queue<string>> state, Operation op)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(op);
        if (!state.TryGetValue(op.Key, out var q))
        {
            q = new Queue<string>();
            state[op.Key] = q;
        }

        switch (op.Kind)
        {
            case OpKind.Enqueue:
                q.Enqueue(op.Value!);
                return null;
            case OpKind.Dequeue:
                return q.Count == 0 ? SequentialModel.Undefined : q.Dequeue();
            default:
                throw new ArgumentOutOfRangeException(nameof(op), op.Kind, "not a queue operation");
        }
    }
}
