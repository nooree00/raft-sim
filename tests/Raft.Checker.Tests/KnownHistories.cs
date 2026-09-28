using System.Collections.Generic;
using Raft.Checker;

namespace Raft.Checker.Tests;

/// <summary>
/// A history with a verdict and where the verdict comes from. A rejected history names its
/// <see cref="Category"/> in <see cref="Anomalies"/>; an accepted history may be the near-miss
/// <see cref="TwinOf"/> a rejected one: the same history made linearizable by changing one operation.
/// </summary>
public sealed record KnownHistory(string Name, bool Linearizable, string Source, IReadOnlyList<Operation> Ops, string? Category = null, string? TwinOf = null);

/// <summary>
/// The anomaly taxonomy for single-key KV histories (P2-04; definitions in
/// docs/design/anomaly-taxonomy.md). Every category needs at least one rejected history.
/// </summary>
public static class Anomalies
{
    public static readonly string[] All =
    [
        "stale-read", "lost-write", "committed-then-vanished", "duplicate-append", "cas-twice-from-one-value",
        "real-time-order-only", "value-never-written", "indeterminate-observed-then-unobserved",
        "readers-disagree-on-order", "delete-resurrected",
    ];
}

/// <summary>
/// Hand-written histories (spec §6). Their verdicts come from me, so they share my model of
/// linearizability; that residual risk is what the Herlihy &amp; Wing published examples address
/// (pending: the paper is not reachable from this environment — see the phase report).
/// Times are logical; an operation with Response null is indeterminate.
/// </summary>
public static class KnownHistories
{
    private const string Hand = "hand-written";

    private static Operation Put(int c, string k, string v, long i, long? r) => new(c, OpKind.Put, k, i, r, Value: v);

    private static Operation Append(int c, string k, string v, long i, long? r) => new(c, OpKind.Append, k, i, r, Value: v);

    private static Operation Get(int c, string k, string? output, long i, long r) => new(c, OpKind.Get, k, i, r, Output: output);

    private static Operation Delete(int c, string k, long i, long r) => new(c, OpKind.Delete, k, i, r);

    private static Operation Cas(int c, string k, string? expected, string v, bool ok, long i, long r) =>
        new(c, OpKind.CompareAndSwap, k, i, r, Value: v, Expected: expected, Output: ok ? "true" : "false");

    public static readonly IReadOnlyList<KnownHistory> All =
    [
        // --- must be rejected ---
        new("stale-read", false, Hand,
        [
            Put(1, "x", "1", 0, 10),
            Put(2, "x", "2", 20, 30),
            Get(3, "x", "1", 40, 50),
        ], Category: "stale-read"),
        new("lost-write", false, Hand,
        [
            Append(1, "x", "a", 0, 10),
            Append(2, "x", "b", 5, 15),
            Get(3, "x", "b", 20, 30),
        ], Category: "lost-write"),
        new("acknowledged-put-invisible", false, Hand,
        [
            Put(1, "x", "1", 0, 10),
            Get(2, "x", null, 20, 30),
        ], Category: "lost-write"),
        new("committed-then-vanished", false, Hand,
        [
            Put(1, "x", "1", 0, 10),
            Get(2, "x", "1", 20, 30),
            Get(3, "x", null, 40, 50),
        ], Category: "committed-then-vanished"),
        new("both-cas-succeed", false, Hand,
        [
            Put(1, "x", "0", 0, 5),
            Cas(2, "x", "0", "1", true, 10, 20),
            Cas(3, "x", "0", "2", true, 12, 22),
        ], Category: "cas-twice-from-one-value"),
        new("read-of-a-value-never-written", false, Hand,
        [
            Get(1, "x", "7", 0, 10),
        ], Category: "value-never-written"),
        new("indeterminate-write-observed-then-vanished", false, Hand,
        [
            Put(1, "x", "1", 0, null),
            Get(2, "x", "1", 10, 20),
            Get(3, "x", null, 30, 40),
        ], Category: "indeterminate-observed-then-unobserved"),
        new("reads-disagree-on-order", false, Hand,
        [
            Put(1, "x", "1", 0, 100),
            Put(2, "x", "2", 0, 100),
            Get(3, "x", "1", 10, 20),
            Get(3, "x", "2", 30, 40),
            Get(4, "x", "2", 10, 20),
            Get(4, "x", "1", 30, 40),
        ], Category: "readers-disagree-on-order"),

        // --- added in P2-04: the categories the audit found empty ---
        new("duplicate-append-applied-twice", false, Hand,
        [
            Append(1, "x", "a", 0, 10),
            Get(2, "x", "aa", 20, 30),
        ], Category: "duplicate-append"),
        new("appends-reordered-across-real-time", false, Hand,
        [
            Append(1, "x", "a", 0, 10),
            Append(2, "x", "b", 20, 30),
            Get(3, "x", "ba", 40, 50),
        ], Category: "real-time-order-only"),
        new("delete-resurrected", false, Hand,
        [
            Put(1, "x", "1", 0, 10),
            Delete(2, "x", 20, 30),
            Get(3, "x", "1", 40, 50),
        ], Category: "delete-resurrected"),

        // --- must be accepted ---
        new("read-inside-write-interval", true, Hand,
        [
            Put(1, "x", "1", 0, 10),
            Get(2, "x", "1", 5, 6),
        ]),
        new("read-concurrent-with-overwrite", true, Hand,
        [
            Put(1, "x", "1", 0, 10),
            Put(2, "x", "2", 20, 30),
            Get(3, "x", "1", 25, 35),
        ], TwinOf: "stale-read"),
        new("indeterminate-write-observed", true, Hand,
        [
            Put(1, "x", "1", 0, null),
            Get(2, "x", "1", 20, 30),
        ]),
        new("indeterminate-write-not-observed", true, Hand,
        [
            Put(1, "x", "1", 0, null),
            Get(2, "x", null, 20, 30),
        ]),
        new("one-cas-wins", true, Hand,
        [
            Put(1, "x", "0", 0, 5),
            Cas(2, "x", "0", "1", true, 10, 20),
            Cas(3, "x", "0", "2", false, 12, 22),
            Get(1, "x", "1", 30, 40),
        ]),
        new("appends-in-either-order", true, Hand,
        [
            Append(1, "x", "a", 0, 10),
            Append(2, "x", "b", 5, 15),
            Get(3, "x", "ba", 20, 30),
        ], TwinOf: "lost-write"),
        new("keys-are-independent", true, Hand,
        [
            Put(1, "x", "1", 0, 10),
            Put(2, "y", "2", 0, 10),
            Get(3, "y", "2", 20, 30),
            Get(3, "x", "1", 40, 50),
        ]),

        // --- near-miss twins (P2-04): each a rejected history above with one operation changed ---
        new("acknowledged-put-read-concurrently", true, Hand,
        [
            Put(1, "x", "1", 0, 10),
            Get(2, "x", null, 5, 30),
        ], TwinOf: "acknowledged-put-invisible"),
        new("committed-and-still-there", true, Hand,
        [
            Put(1, "x", "1", 0, 10),
            Get(2, "x", "1", 20, 30),
            Get(3, "x", "1", 40, 50),
        ], TwinOf: "committed-then-vanished"),
        new("second-cas-fails", true, Hand,
        [
            Put(1, "x", "0", 0, 5),
            Cas(2, "x", "0", "1", true, 10, 20),
            Cas(3, "x", "0", "2", false, 12, 22),
        ], TwinOf: "both-cas-succeed"),
        new("read-of-an-absent-key", true, Hand,
        [
            Get(1, "x", null, 0, 10),
        ], TwinOf: "read-of-a-value-never-written"),
        new("indeterminate-write-observed-and-kept", true, Hand,
        [
            Put(1, "x", "1", 0, null),
            Get(2, "x", "1", 10, 20),
            Get(3, "x", "1", 30, 40),
        ], TwinOf: "indeterminate-write-observed-then-vanished"),
        new("reads-agree-on-order", true, Hand,
        [
            Put(1, "x", "1", 0, 100),
            Put(2, "x", "2", 0, 100),
            Get(3, "x", "1", 10, 20),
            Get(3, "x", "2", 30, 40),
            Get(4, "x", "2", 10, 20),
            Get(4, "x", "2", 30, 40),
        ], TwinOf: "reads-disagree-on-order"),
        new("single-append-read-once", true, Hand,
        [
            Append(1, "x", "a", 0, 10),
            Get(2, "x", "a", 20, 30),
        ], TwinOf: "duplicate-append-applied-twice"),
        new("appends-overlapping-in-real-time", true, Hand,
        [
            Append(1, "x", "a", 0, 10),
            Append(2, "x", "b", 5, 30),
            Get(3, "x", "ba", 40, 50),
        ], TwinOf: "appends-reordered-across-real-time"),
        new("read-concurrent-with-delete", true, Hand,
        [
            Put(1, "x", "1", 0, 10),
            Delete(2, "x", 20, 30),
            Get(3, "x", "1", 25, 50),
        ], TwinOf: "delete-resurrected"),
    ];
}
