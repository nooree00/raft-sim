using System.Collections.Generic;
using Raft.Checker;
using Xunit;

namespace Raft.Checker.Tests;

/// <summary>
/// P8-04: before ReadIndex exists, the histories it could produce if it were wrong, each rejected by
/// the WGL checker and by the brute-force oracle, and the correct counterpart of each accepted by
/// both (the P6-04 pattern: show the checker catches it, then build the fix with something to verify
/// it against). The oracle shares nothing with the checker but the sequential model, so the verdicts
/// do not rest on WGL's search alone. Sabotage S-wgl-13.
/// </summary>
public sealed class ReadIndexHistoryTests
{
    private static Operation Put(int c, string v, long i, long r) => new(c, OpKind.Put, "x", i, r, Value: v);

    private static Operation Append(int c, string v, long i, long r) => new(c, OpKind.Append, "x", i, r, Value: v);

    private static Operation Delete(int c, long i, long r) => new(c, OpKind.Delete, "x", i, r);

    private static Operation Cas(int c, string? expected, string v, long i, long r) => new(c, OpKind.CompareAndSwap, "x", i, r, Value: v, Expected: expected, Output: "true");

    private static Operation Get(int c, string? output, long i, long r) => new(c, OpKind.Get, "x", i, r, Output: output);

    public static TheoryData<string> Cases() => new("deposed-leader", "before-the-no-op", "old-quorum-while-joint");

    /// <summary>
    /// The three histories by name, each with the read a correct node gives in its place:
    /// a deposed leader answers with the value it had after a new leader committed a write;
    /// a new leader answers before its term's entry commits, missing its predecessor's acknowledged append;
    /// a leader counting a quorum of the old configuration alone answers after the new one deleted the key.
    /// </summary>
    private static (List<Operation> Stale, List<Operation> Correct) Case(string name) => name switch
    {
        "deposed-leader" => ([Put(1, "1", 0, 10), Put(2, "2", 20, 30), Get(3, "1", 40, 50)], [Put(1, "1", 0, 10), Put(2, "2", 20, 30), Get(3, "2", 40, 50)]),
        "before-the-no-op" => ([Append(1, "a", 0, 10), Append(2, "b", 20, 30), Get(3, "a", 40, 50)], [Append(1, "a", 0, 10), Append(2, "b", 20, 30), Get(3, "ab", 40, 50)]),
        _ => ([Cas(1, null, "v", 0, 10), Delete(2, 20, 30), Get(3, "v", 40, 50)], [Cas(1, null, "v", 0, 10), Delete(2, 20, 30), Get(3, null, 40, 50)]),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void AStaleReadIsRejectedAndTheCorrectReadAccepted(string name)
    {
        var (stale, correct) = Case(name);
        Assert.False(WglChecker.Check(stale).IsLinearizable, $"{name}: the checker accepted the stale read");
        Assert.False(BruteForceOracle.IsLinearizable(stale), $"{name}: the oracle accepted the stale read");
        Assert.True(WglChecker.Check(correct).IsLinearizable, $"{name}: the checker rejected the correct read");
        Assert.True(BruteForceOracle.IsLinearizable(correct), $"{name}: the oracle rejected the correct read");
    }
}
