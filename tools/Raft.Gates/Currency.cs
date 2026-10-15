using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Raft.Gates;

/// <summary>
/// P13-01, phase 13 decision 5: one comparison for every hand-kept input that claims something
/// observable. Phase 12's class finding: a gate reading a file kept by hand reads it as true, so it
/// cannot be the check that the file is current. Four inputs were checked by nothing (a record's
/// commit, the touched estimate, the cost file beyond a factor of 5, the Budget seeds); each now has
/// an observer where its truth lives, and every observer reports through the two policies here with
/// one failure text: the input, the key, the claim, the observation and where it came from, and
/// what the file should hold, so a refresh is copying, not remembering. The registry
/// (<see cref="Inputs"/>) names an observer for each such input and fails an observer it does not
/// name. Vacuity risk: a comparison that never reports stale silences all four at once (S-input-2);
/// an observer that reads the claim back compares the file with itself, so each has its own
/// sabotage (S-meas-3, S-touch-3, S-shard-12, S-soak-12).
/// </summary>
internal static partial class Currency
{
    /// <summary>An observer: the input it checks (a registry path), where its observation comes from, and the gate it runs in.</summary>
    internal sealed record Observer(string Id, string Input, string Source, string RunsIn);

    public static readonly Observer RecordCommit = new("record-commit", Measurements.Directory, "git, the head's ancestors", "gates measurements");

    public static readonly Observer TouchedRun = new("touched-run", Touched.ConfigFile, "the touched stage's own run", "gates sabotage --touched");

    public static readonly Observer GitHubRun = new("github-run", ShardPlan.CostFile, "GitHub's head shards, their entry-time notices", "gates build-collect");

    public static readonly Observer SoakRanking = new("soak-ranking", SeedsFile, "the baseline soak's ten hardest decided histories", "gates currency soak-ranking (the soak job)");

    public static readonly Observer ClassReach = new("class-reach", SabotageSpec.ClassReachFile, "GitHub's head shards, the tests red beside each entry's target in its class", "gates build-collect");

    public static readonly Observer MarginsCommit = new("margins-commit", Margins.BenchTables, "git, the last change to each row's control", "gates margins --currency (fails a report at the head)");

    /// <summary>Every observer; the registry must name each exactly once, at its input.</summary>
    public static readonly IReadOnlyList<Observer> Observers = [RecordCommit, TouchedRun, GitHubRun, SoakRanking, ClassReach, MarginsCommit];

    /// <summary>The Budget tests' seeds (P13-01: moved out of the test's source so the soak's ranking can be compared with them).</summary>
    public const string SeedsFile = "ci/budget-seeds.txt";

    /// <summary>
    /// Within a factor: a claim is stale when its observation is off by more than
    /// <paramref name="floor"/> and beyond <paramref name="factor"/> either way. Returns how many were stale.
    /// </summary>
    public static int Factor(Observer o, IEnumerable<(string Key, double Claimed, double Observed)> pairs, double factor, double floor, Findings f, Func<string, double, string> refresh)
    {
        var policy = $"within a factor of {N(factor)} (and {N(floor)} s)";
        var (n, stale, worstKey, worst, worstDistance) = (0, 0, "none", 1.0, 1.0);
        foreach (var (key, claimed, observed) in pairs)
        {
            n++;
            var ratio = claimed > 0 && observed > 0 ? observed / claimed : double.PositiveInfinity;
            var distance = Math.Max(ratio, 1 / ratio);
            if (distance > worstDistance)
            {
                (worstKey, worst, worstDistance) = (key, ratio, distance);
            }

            if (Math.Abs(observed - claimed) > floor && (observed > claimed * factor || observed < claimed / factor || claimed <= 0))
            {
                stale++;
                f.Fail(Text(o, key, N(claimed), $"{N(observed)} ({ratio.ToString("F2", CultureInfo.InvariantCulture)} times)", policy, refresh(key, observed)));
            }
        }

        f.Note($"currency {o.Id}: {n} claim(s) in {o.Input} against {o.Source}, {policy}; {stale} stale; the furthest {worstKey} at {worst.ToString("F2", CultureInfo.InvariantCulture)} times");
        return stale;
    }

    /// <summary>A member of an observed set: a claim is stale when the observation does not contain it. Returns how many were stale.</summary>
    public static int Member(Observer o, IEnumerable<(string Key, string Claimed)> claims, Func<string, bool> observed, string observedAs, Findings f, Func<string, string> refresh)
    {
        var policy = $"a member of {observedAs}";
        var (n, stale) = (0, 0);
        foreach (var (key, claimed) in claims)
        {
            n++;
            if (!observed(claimed))
            {
                stale++;
                f.Fail(Text(o, key, claimed, "it is not one", policy, refresh(key)));
            }
        }

        f.Note($"currency {o.Id}: {n} claim(s) in {o.Input} against {o.Source}, {policy}; {stale} stale");
        return stale;
    }

    /// <summary>
    /// Listed (P14-05): every item the observation finds must be in the input; an item it finds that the
    /// input does not list is stale. Returns how many were stale.
    /// </summary>
    public static int Listed(Observer o, IEnumerable<(string Key, string Observed)> found, Func<string, bool> listed, string rule, Findings f, Func<string, string> refresh)
    {
        var policy = $"listed: {rule}";
        var (n, stale) = (0, 0);
        foreach (var (key, observed) in found)
        {
            n++;
            if (!listed(key))
            {
                stale++;
                f.Fail(Text(o, key, "not listed", observed, policy, refresh(key)));
            }
        }

        f.Note($"currency {o.Id}: {n} item(s) found by {o.Source}, each to be in {o.Input} ({policy}); {stale} stale");
        return stale;
    }

    /// <summary>The one failure text every observer uses.</summary>
    internal static string Text(Observer o, string key, string claimed, string observed, string policy, string refresh) =>
        $"{o.Input}: {key} claims {claimed}; {o.Source}: {observed} ({policy}): stale. {refresh}";

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// `gates currency soak-ranking --report FILE`, run by the soak job after the soak: each Budget
    /// seed among the soak's ten hardest decided histories by checker states. Only a report of the
    /// full soak counts: the suite's 300-execution sample ranks other histories.
    /// </summary>
    public static Findings Run(Repo repo, string[] args)
    {
        var rest = args.ToList();
        var f = new Findings();
        var which = rest.Count > 0 && !rest[0].StartsWith("--", StringComparison.Ordinal) ? rest[0] : null;
        if (which != SoakRanking.Id)
        {
            f.Fail($"usage: gates currency {SoakRanking.Id} --report FILE (the only observer run on its own; the others run inside their gates)");
            return f;
        }

        rest.RemoveAt(0);
        var report = Options.Take(rest, "--report");
        if (report is null || !File.Exists(report))
        {
            f.Fail($"--report: no soak report at '{report}'");
            return f;
        }

        CheckSeeds(ParseSeeds(repo.ReadText(SeedsFile)), File.ReadAllText(report), f);
        return f;
    }

    /// <summary>The executions a full soak runs; a report covering fewer is not the soak's ranking.</summary>
    public const int SoakExecutions = 10_000;

    /// <summary>The seeds, one per line; '#' comments.</summary>
    public static IReadOnlyList<int> ParseSeeds(string text) =>
        [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => int.Parse(l, CultureInfo.InvariantCulture))];

    [GeneratedRegex(@"^ten hardest decided histories \(states explored, seed\): (?<list>.*)$", RegexOptions.Multiline)]
    private static partial Regex RankingLine();

    [GeneratedRegex(@"(?<states>\d+) seed (?<seed>\d+)")]
    private static partial Regex RankingItem();

    [GeneratedRegex(@"most states explored (?<max>\d+)")]
    private static partial Regex MostStates();

    /// <summary>The decision, apart from the file system so it can be tested on a written report.</summary>
    internal static void CheckSeeds(IReadOnlyList<int> seeds, string report, Findings f)
    {
        f.Require(seeds.Count > 0, $"{SeedsFile} names no seed: the Budget tests would run none");
        var first = report.Split('\n')[0];
        if (!first.StartsWith($"{SoakExecutions} executions ", StringComparison.Ordinal))
        {
            f.Fail($"the report is not the full soak's ('{first}'): the ranking of a sample names other histories");
            return;
        }

        var line = RankingLine().Match(report);
        if (!line.Success)
        {
            f.Fail("the soak report has no 'ten hardest decided histories' line");
            return;
        }

        var ranking = RankingItem().Matches(line.Groups["list"].Value).Select(m => (States: long.Parse(m.Groups["states"].Value, CultureInfo.InvariantCulture), Seed: int.Parse(m.Groups["seed"].Value, CultureInfo.InvariantCulture))).ToList();
        // The guard: the ranking is by the checker's states, the count the report gives as its most.
        var max = MostStates().Match(report);
        f.Require(ranking.Count == 10, $"the soak ranked {ranking.Count} decided histories, not ten");
        f.Require(ranking.Zip(ranking.Skip(1)).All(p => p.First.States >= p.Second.States), "the soak's ranking is not in descending order of states explored");
        f.Require(max.Success && ranking.All(r => r.States <= long.Parse(max.Groups["max"].Value, CultureInfo.InvariantCulture)), "a ranked history explored more states than the report's most: the ranking counts something else");
        var top = ranking.Select(r => r.Seed).ToHashSet();
        Member(SoakRanking, seeds.Select((s, i) => ($"seed {i + 1}", s.ToString(CultureInfo.InvariantCulture))), s => top.Contains(int.Parse(s, CultureInfo.InvariantCulture)), "the soak's ten hardest decided histories", f,
            _ => $"The file should hold the soak's hardest: {string.Join(", ", ranking.Take(Math.Max(3, seeds.Count)).Select(r => $"{r.Seed} ({r.States} states)"))}");
    }
}
