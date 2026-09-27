using System;
using System.Collections.Generic;

namespace Raft.Checker;

/// <summary>The sequential specification of the key-value store (spec §6).</summary>
public static class KvModel
{
    /// <summary>Applies <paramref name="op"/> to <paramref name="state"/> in place and returns its output.</summary>
    public static string? Apply(Dictionary<string, string> state, Operation op)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(op);
        var present = state.TryGetValue(op.Key, out var current);
        switch (op.Kind)
        {
            case OpKind.Get:
                return present ? current : null;
            case OpKind.Put:
                state[op.Key] = op.Value!;
                return null;
            case OpKind.Append:
                state[op.Key] = (present ? current : "") + op.Value;
                return null;
            case OpKind.CompareAndSwap:
                // Expected null means "the key is absent".
                if (op.Expected is null ? !present : present && current == op.Expected)
                {
                    state[op.Key] = op.Value!;
                    return "true";
                }

                return "false";
            case OpKind.Delete:
                state.Remove(op.Key);
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(op), op.Kind, "unknown operation");
        }
    }
}
