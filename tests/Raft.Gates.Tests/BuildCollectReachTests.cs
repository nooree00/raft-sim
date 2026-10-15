using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P15-06 (P14-05's broad-run, renamed for what it can see): build-collect reads every head shard's
/// notice of the tests red beside each entry's target and requires a test red beside at least half of
/// its class's other entries, and at least four, to be in the class-reach list (the observer
/// class-reach). The entries that ran a test are the manifest's entries targeting another method of its
/// class. Sabotage S-reach-1 (the observation taken from the list).
/// </summary>
public sealed class BuildCollectReachTests
{
    private static readonly string[] TwoShards = ["1/2", "2/2"];

    private const string Jobs = """{"jobs":[{"id":11,"name":"sabotage 1/2"},{"id":22,"name":"sabotage 2/2"}]}""";

    private const string Listed = "Raft.X.Tests.C.Listed";

    /// <summary>Six entries target methods of class C (one of them a theory case), so a test of C ran beside six; two target class D.</summary>
    private static readonly string[] Targets = ["Raft.X.Tests.C.A", "Raft.X.Tests.C.B", "Raft.X.Tests.C.E", "Raft.X.Tests.C.F", "Raft.X.Tests.C.G", "Raft.X.Tests.C.H(n: 1)", "Raft.X.Tests.D.A", "Raft.X.Tests.D.B"];

    private static string Annotations(params string[] messages) =>
        "[" + string.Join(",", messages.Select(m => $$"""{"annotation_level":"notice","title":"","message":"{{m.Replace("\n", "\\n", StringComparison.Ordinal)}}"}""")) + "]";

    private static string Part(int k, int n, string lines) => $"sabotage red neighbours {k}/{n}\n{lines}";

    private static Findings Check(Func<long, string> annotations)
    {
        var f = new Findings();
        BuildCollect.CheckReach(TwoShards, Jobs, annotations, [Listed], Targets, f);
        return f;
    }

    /// <summary>Sabotage S-reach-1: summed over the shards, red beside 5 of its class's 6 other entries, and not listed.</summary>
    [Fact]
    public void ATestRedBesideHalfOfItsClasssOtherEntriesMustBeListed()
    {
        var f = Check(id => id == 11 ? Annotations(Part(1, 1, "@Raft.X.Tests.C\nUnlisted 3")) : Annotations(Part(1, 1, "@Raft.X.Tests.C\nUnlisted 2")));
        var failure = Assert.Single(f.Failures);
        Assert.StartsWith("ci/class-reach.txt: Raft.X.Tests.C.Unlisted claims not listed; GitHub's head shards, the tests red beside each entry's target in its class: red beside 5 of its class's 6 other entries", failure, StringComparison.Ordinal);
    }

    /// <summary>A listed test passes however often it is red, and an unlisted one red beside 2 of 6 does not reach its class.</summary>
    [Fact]
    public void AListedTestPassesAndSoDoesOneRarelyRed() =>
        Assert.Empty(Check(id => id == 11 ? Annotations(Part(1, 1, "@Raft.X.Tests.C\nListed 6\nRare 1")) : Annotations(Part(1, 1, "@Raft.X.Tests.C\nRare 1"))).Failures);

    /// <summary>Class D's tests ran beside two entries, fewer than four: red beside both, not judged.</summary>
    [Fact]
    public void ATestRanBesideTooFewEntriesIsNotJudged() =>
        Assert.Empty(Check(id => id == 11 ? Annotations(Part(1, 1, "@Raft.X.Tests.D\nC 2")) : Annotations(Part(1, 1, ""))).Failures);

    /// <summary>The vacuity guard: counts over the shards that reported cover less than the run did.</summary>
    [Fact]
    public void AShardWithNoNoticeFails() =>
        Assert.Contains(Check(id => id == 11 ? Annotations(Part(1, 1, "@Raft.X.Tests.C\nUnlisted 3")) : "[]").Failures, x => x.Contains("sabotage 2/2: no 'sabotage red neighbours' notice", StringComparison.Ordinal));

    /// <summary>A step keeps ten notices and drops the rest silently, so a shard's notice missing a part counts less than the shard did.</summary>
    [Fact]
    public void AShardWhoseNoticeMissesAPartFails() =>
        Assert.Contains(Check(id => id == 11 ? Annotations(Part(1, 3, "@Raft.X.Tests.C\nA 1"), Part(3, 3, "@Raft.X.Tests.C\nB 1")) : Annotations(Part(1, 1, ""))).Failures,
            x => x.Contains("sabotage 1/2: red-neighbours parts 1, 3 of 3: a part is missing", StringComparison.Ordinal));

    /// <summary>A part at GitHub's cap was cut (run 38517402423's whole-run notice is exactly 4,096 characters), so its counts are not read.</summary>
    [Fact]
    public void APartAtGitHubsCapWasCutAndFails() =>
        Assert.Contains(Check(id => id == 11 ? Annotations(Part(1, 1, "@Raft.X.Tests.C\n" + new string('x', 4096))) : Annotations(Part(1, 1, ""))).Failures,
            x => x.Contains("GitHub's cap, so it was cut", StringComparison.Ordinal));

    /// <summary>The harness's notices, read back: every count, each part under the cap, a class split across parts restated.</summary>
    [Fact]
    public void TheNoticesAreReadAsTheHarnessWritesThem()
    {
        var red = Enumerable.Range(0, 400).ToDictionary(i => $"Raft.X.Tests.Class{i / 50}.AMethodWithTheLongNameTheseTestsHave{i}", i => 1 + (i % 7), StringComparer.Ordinal);

        var notices = Sabotage.NeighbourNotices(red)!;
        var (read, problem) = BuildCollect.NeighbourNotice(Annotations([.. notices]));

        Assert.Null(problem);
        Assert.True(notices.Count > 1, $"{notices.Count} part(s)");
        Assert.All(notices, n => Assert.True(n.Length < Sabotage.AnnotationCap, $"{n.Length} characters"));
        Assert.Equal(red.OrderBy(r => r.Key, StringComparer.Ordinal), read!.OrderBy(r => r.Key, StringComparer.Ordinal));
        Assert.Equal(["sabotage red neighbours 1/1\n"], Sabotage.NeighbourNotices(new Dictionary<string, int>()));
    }

    /// <summary>An entry's red neighbours: a theory's cases once, as their method, red when any case failed; never the target's own method.</summary>
    [Fact]
    public void AnEntrysRedNeighboursCountATheoryOnceAndNeverItsOwnMethod()
    {
        (string, string)[] results = [("C.T(n: 1)", "Passed"), ("C.T(n: 2)", "Failed"), ("C.T(n: 3)", "Failed"), ("C.Mine(n: 1)", "Failed"), ("C.Mine(n: 2)", "Failed"), ("C.Green", "Passed"), ("C.Red", "Failed")];

        Assert.Equal(["C.Red", "C.T"], Sabotage.RedNeighbours(results, "C.Mine(n: 1)").Order(StringComparer.Ordinal));
    }
}
