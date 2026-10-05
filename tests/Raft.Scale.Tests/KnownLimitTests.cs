using System.Collections.Generic;
using System.Linq;
using Raft.Checker;
using Xunit;

namespace Raft.Scale.Tests;

/// <summary>
/// P5-05: the recorded known limits, named entry by entry, and each rule of the judgement on a
/// hand-built history: a limit matches only its exact history, only while still undecided.
/// </summary>
public sealed class KnownLimitTests
{
    /// <summary>
    /// The reviewed set, by identity, not by count: replacing an entry fails as surely as adding one
    /// (S-kl-2). Empty since P7-11: with compaction on, the baseline soak has no undecided search.
    /// </summary>
    [Fact]
    public void TheRecordedKnownLimitsAreExactlyTheReviewedOnes() =>
        Assert.Empty(KnownLimits.Recorded.Select(e => $"{e.Id} {e.Seed} {e.Key} {e.Digest}"));

    private static readonly List<Operation> History =
    [
        new(1, OpKind.Append, "x", 0, null, Value: "a"),
        new(2, OpKind.Get, "x", 10, 20, Output: "a"),
        new(3, OpKind.Put, "y", 0, 10, Value: "1"),
    ];

    private static readonly KnownLimit Entry = new("KL-9", 5, "x", KnownLimits.Digest(History.Where(o => o.Key == "x")), "a row", "a report");

    private static CheckResult Result(Verdict verdict, string? key) => new(verdict, key, [], [], 1);

    [Fact]
    public void AnUndecidedSearchIsARecordedLimitOnlyForItsSeedKeyAndExactHistory()
    {
        Assert.Equal((true, (string?)null), KnownLimits.Judge([Entry], 5, History, Result(Verdict.Undecided, "x")));
        Assert.Equal((false, (string?)null), KnownLimits.Judge([Entry], 6, History, Result(Verdict.Undecided, "x")));

        // The same seed and key with one operation more: another history, so the entry no longer matches (S-kl-1).
        var changed = History.Append(new Operation(4, OpKind.Get, "x", 30, 40, Output: "a")).ToList();
        var (recorded, failure) = KnownLimits.Judge([Entry], 5, changed, Result(Verdict.Undecided, "x"));
        Assert.False(recorded);
        Assert.Contains("no longer has the recorded history", failure, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordedLimitThatBecomesDecidableFailsAsStale()
    {
        var (recorded, failure) = KnownLimits.Judge([Entry], 5, History, Result(Verdict.Linearizable, null));

        Assert.False(recorded);
        Assert.Contains("the entry is stale", failure, System.StringComparison.Ordinal);
    }
}
