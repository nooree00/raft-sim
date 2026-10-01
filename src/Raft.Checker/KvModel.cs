using System;
using System.Collections.Generic;
using System.Linq;

namespace Raft.Checker;

/// <summary>The sequential specification of the key-value store (spec §6).</summary>
public sealed class KvModel : ISequentialModel<Dictionary<string, string>>
{
    public static readonly KvModel Instance = new();

    public Dictionary<string, string> NewState() => new(StringComparer.Ordinal);

    public Dictionary<string, string> Copy(Dictionary<string, string> state) => new(state, StringComparer.Ordinal);

    /// <summary>Keys in ordinal order, each length-prefixed so no value can imitate a separator.</summary>
    public string Fingerprint(Dictionary<string, string> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var sb = new System.Text.StringBuilder();
        foreach (var key in state.Keys.Order(StringComparer.Ordinal))
        {
            Field(sb, key);
            Field(sb, state[key]);
        }

        return sb.ToString();
    }

    internal static void Field(System.Text.StringBuilder sb, string s) =>
        sb.Append(s.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(s);

    public bool IsReadOnly(Operation op) => op?.Kind == OpKind.Get;

    /// <summary>Applies <paramref name="op"/> to <paramref name="state"/> in place and returns its output.</summary>
    public string? Apply(Dictionary<string, string> state, Operation op)
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
