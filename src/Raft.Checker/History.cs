using System;
using System.Collections.Generic;

namespace Raft.Checker;

/// <summary>
/// The key-value operations of spec §6 (every operation touches exactly one key), and the FIFO
/// queue operations of Herlihy &amp; Wing's Figure 1, used for their published examples. A queue
/// operation's <see cref="Operation.Key"/> names the queue object.
/// </summary>
public enum OpKind
{
    Get,
    Put,
    Append,
    CompareAndSwap,
    Delete,
    Enqueue,
    Dequeue,
}

/// <summary>
/// One client operation in a history. <see cref="Response"/> is null for an indeterminate operation
/// (timeout, crash): it may or may not have taken effect, at any point after <see cref="Invoke"/>,
/// and its output is unknown. Outputs: Get → the value, or null if absent; CompareAndSwap →
/// "true"/"false"; Dequeue → the item; Put, Append, Delete, Enqueue → null (no information).
/// Real-time order: a precedes b iff a responded strictly before b was invoked.
/// </summary>
public sealed record Operation(
    int Client,
    OpKind Kind,
    string Key,
    long Invoke,
    long? Response,
    string? Value = null,
    string? Expected = null,
    string? Output = null)
{
    public bool IsIndeterminate => Response is null;

    public bool Precedes(Operation other) => Response is { } r && r < other.Invoke;

    public override string ToString() =>
        $"c{Client} {Kind}({Key}{(Expected is null ? "" : $", {Expected}")}{(Value is null ? "" : $", {Value}")}) " +
        $"[{Invoke}, {(Response is null ? "∞" : Response)}] → {(IsIndeterminate ? "?" : Output ?? "·")}";
}

public static class History
{
    /// <summary>Structural problems that make a history meaningless to check.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<Operation> ops)
    {
        var problems = new List<string>();
        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (op.Response is { } r && r < op.Invoke)
            {
                problems.Add($"#{i}: responds before it is invoked");
            }

            if (op.Kind is OpKind.Put or OpKind.Append or OpKind.CompareAndSwap or OpKind.Enqueue && op.Value is null)
            {
                problems.Add($"#{i}: {op.Kind} needs a value");
            }

            if (!op.IsIndeterminate && op.Kind == OpKind.CompareAndSwap && op.Output is not ("true" or "false"))
            {
                problems.Add($"#{i}: a completed CompareAndSwap outputs true or false");
            }

            if (!op.IsIndeterminate && op.Kind is OpKind.Put or OpKind.Append or OpKind.Delete or OpKind.Enqueue && op.Output is not null)
            {
                problems.Add($"#{i}: {op.Kind} has no output");
            }

            if (!op.IsIndeterminate && op.Kind == OpKind.Dequeue && op.Output is null)
            {
                problems.Add($"#{i}: a completed Dequeue outputs the item");
            }
        }

        // A client is sequential: its operations must not overlap in time.
        for (var i = 0; i < ops.Count; i++)
        {
            for (var j = i + 1; j < ops.Count; j++)
            {
                if (ops[i].Client == ops[j].Client && !ops[i].Precedes(ops[j]) && !ops[j].Precedes(ops[i]))
                {
                    problems.Add($"#{i} and #{j}: client {ops[i].Client} has overlapping operations");
                }
            }
        }

        return problems;
    }
}
