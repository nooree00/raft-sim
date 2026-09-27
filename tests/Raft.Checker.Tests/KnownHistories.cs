using System.Collections.Generic;
using Raft.Checker;

namespace Raft.Checker.Tests;

/// <summary>A history with a verdict and where the verdict comes from.</summary>
public sealed record KnownHistory(string Name, bool Linearizable, string Source, IReadOnlyList<Operation> Ops);

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
        ]),
        new("lost-write", false, Hand,
        [
            Append(1, "x", "a", 0, 10),
            Append(2, "x", "b", 5, 15),
            Get(3, "x", "b", 20, 30),
        ]),
        new("acknowledged-put-invisible", false, Hand,
        [
            Put(1, "x", "1", 0, 10),
            Get(2, "x", null, 20, 30),
        ]),
        new("committed-then-vanished", false, Hand,
        [
            Put(1, "x", "1", 0, 10),
            Get(2, "x", "1", 20, 30),
            Get(3, "x", null, 40, 50),
        ]),
        new("both-cas-succeed", false, Hand,
        [
            Put(1, "x", "0", 0, 5),
            Cas(2, "x", "0", "1", true, 10, 20),
            Cas(3, "x", "0", "2", true, 12, 22),
        ]),
        new("read-of-a-value-never-written", false, Hand,
        [
            Get(1, "x", "7", 0, 10),
        ]),
        new("indeterminate-write-observed-then-vanished", false, Hand,
        [
            Put(1, "x", "1", 0, null),
            Get(2, "x", "1", 10, 20),
            Get(3, "x", null, 30, 40),
        ]),
        new("reads-disagree-on-order", false, Hand,
        [
            Put(1, "x", "1", 0, 100),
            Put(2, "x", "2", 0, 100),
            Get(3, "x", "1", 10, 20),
            Get(3, "x", "2", 30, 40),
            Get(4, "x", "2", 10, 20),
            Get(4, "x", "1", 30, 40),
        ]),

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
        ]),
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
        ]),
        new("keys-are-independent", true, Hand,
        [
            Put(1, "x", "1", 0, 10),
            Put(2, "y", "2", 0, 10),
            Get(3, "y", "2", 20, 30),
            Get(3, "x", "1", 40, 50),
        ]),
    ];
}
