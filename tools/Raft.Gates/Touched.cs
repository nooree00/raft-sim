using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Raft.Gates;

/// <summary>An entry's footprint for the touched-file selection: its patch, its control if it has one, and its test target if it has one.</summary>
internal sealed record TouchedEntry(string Id, string Patch, string? Control, string? Target = null);

/// <summary>
/// P9-00, phase 9 decision 1: the harness entries a push makes worth running before it. An entry is
/// selected when its patch or its control names a file any commit of the push changed; the
/// selection runs at the head. Per commit is not enough: S-lin-4 broke at P8-02, which never touched
/// the one file its patch changes. The plan is printed before anything runs, with the selected
/// fraction of the manifest (a selection that routinely takes most of it is not selecting), and a
/// selection estimated past the threshold runs only when accepted (the reviewer, at approval).
/// </summary>
internal static class Touched
{
    public const string ConfigFile = "ci/sabotage-touched.txt";

    /// <summary>
    /// What a run of the selection is expected to cost, whether it goes ahead, and the ceiling it runs
    /// under: the shards' 15 minutes, or 1.5 times an accepted estimate past them (P9-00: the first
    /// accepted selection, 80 entries in 18.5 minutes, failed on the shards' ceiling it had inherited).
    /// </summary>
    internal sealed record Plan(int Selected, int Manifest, TimeSpan Estimate, bool Run, string Line)
    {
        public TimeSpan Ceiling => TimeSpan.FromMinutes(Math.Max(15, Estimate.TotalMinutes * 1.5));
    }

    /// <summary>The files a unified diff names, old and new sides, without the a/ and b/ prefixes.</summary>
    public static IReadOnlySet<string> PathsIn(string diff)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in diff.Split('\n'))
        {
            if ((line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal)) && line.Length > 4)
            {
                var path = line[4..].Split('\t')[0].TrimEnd('\r');
                if (path == "/dev/null")
                {
                    continue;
                }

                paths.Add(path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path);
            }
        }

        return paths;
    }

    /// <summary>
    /// P12-02, phase 11's finding: a change to the node moves facts measured from the soak (a seed
    /// named as the hardest, a control's pinned seeds, the soak sample's distribution) in entries
    /// whose patch and target never name the node. S-soak-6 survived on GitHub for that reason. So a
    /// push changing a file under this prefix also selects every entry whose target is in a
    /// soak-derived class.
    /// </summary>
    public const string NodePrefix = "src/Raft.Core/";

    /// <summary>The classes whose tests run the soak's executions or name seeds measured from them.</summary>
    public static readonly string[] SoakDerived =
    [
        "Raft.Scale.Tests.SoakTests.",
        "Raft.Scale.Tests.LinearizabilityTests.",
        "Raft.Scale.Tests.KnownLimitTests.",
        "Raft.Scale.Tests.PositiveControlTests.",
        "Raft.Budget.Tests.BudgetTests.",
    ];

    /// <summary>The ids of the entries whose patch or control names one of <paramref name="changed"/>, and, when the node changed, the soak-derived entries (<see cref="NodeChanged"/>).</summary>
    public static IReadOnlyList<string> Select(IEnumerable<TouchedEntry> entries, IReadOnlySet<string> changed)
    {
        var node = NodeChanged(changed);
        return entries.Where(e => PathsIn(e.Patch).Concat(e.Control is null ? [] : PathsIn(e.Control)).Any(changed.Contains) || (node && SoakDerivedTarget(e.Target))).Select(e => e.Id).ToList();
    }

    /// <summary>Whether the push changed the node.</summary>
    public static bool NodeChanged(IReadOnlySet<string> changed) => changed.Any(p => p.StartsWith(NodePrefix, StringComparison.Ordinal));

    /// <summary>Whether an entry's target is in a soak-derived class.</summary>
    public static bool SoakDerivedTarget(string? target) => target is not null && SoakDerived.Any(c => target.StartsWith(c, StringComparison.Ordinal));

    /// <summary>
    /// The estimate's inputs (P13-01): the factor that turns a cost-file line, timed on GitHub, into
    /// this machine's seconds; how far a run may be from its estimate before the file is stale; and
    /// the threshold past which a selection needs --accept-estimate.
    /// </summary>
    internal sealed record Config(double EntryScale, double StaleFactor, double StaleFloor, double ConfirmAboveMinutes);

    /// <summary>"entry-scale F", "stale-factor F", "stale-floor S" and "confirm-above-minutes N", from <see cref="ConfigFile"/>; '#' comments.</summary>
    public static Config ParseConfig(string text)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        string[] keys = ["entry-scale", "stale-factor", "stale-floor", "confirm-above-minutes"];
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (!keys.Contains(parts[0]))
            {
                throw new FormatException($"{ConfigFile}: unknown key '{parts[0]}'");
            }

            values[parts[0]] = double.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        double Get(string k) => values.TryGetValue(k, out var v) ? v : throw new FormatException($"{ConfigFile}: no {k}");
        return new Config(Get("entry-scale"), Get("stale-factor"), Get("stale-floor"), Get("confirm-above-minutes"));
    }

    /// <summary>
    /// The selection's estimate in this machine's seconds: the shard plan's model of the entries on
    /// the stage's workers (P10-00: each worker's baseline build and units, then the longest entry first
    /// to the worker that would finish soonest), each entry's line scaled to this machine. Phase 8's flat
    /// 13.7 s an entry missed every single entry by eleven times (S-kill-1 alone takes 117 s) and the
    /// 67-entry run by 2.4.
    /// </summary>
    public static double Estimate(IReadOnlyList<PlanEntry> entries, ShardCosts costs, int workers, double entryScale) =>
        ShardPlan.ModelledTime(entries, costs, workers, entryScale);

    /// <summary>The plan for <paramref name="selected"/> of <paramref name="manifest"/> entries, estimated at <paramref name="estimateSeconds"/>.</summary>
    public static Plan PlanFor(int selected, int manifest, double estimateSeconds, Config config, bool accepted)
    {
        var estimate = TimeSpan.FromSeconds(selected == 0 ? 0 : estimateSeconds);
        var fraction = manifest == 0 ? 0 : 100.0 * selected / manifest;
        var over = estimate.TotalMinutes > config.ConfirmAboveMinutes;
        var run = selected > 0 && (!over || accepted);
        var verdict = selected == 0 ? "nothing to run"
            : !over ? "running"
            : accepted ? $"over the {N(config.ConfirmAboveMinutes)}-minute threshold, accepted: running"
            : $"over the {N(config.ConfirmAboveMinutes)}-minute threshold: not run; pass --accept-estimate to run it, and record the decision";
        var line = $"touched: {selected} of {manifest} entries ({fraction.ToString("F1", CultureInfo.InvariantCulture)}% of the manifest), estimated {estimate.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)} min (the shard plan's model, lines times {N(config.EntryScale)}); {verdict}";
        return new Plan(selected, manifest, estimate, run, line);
    }

    /// <summary>
    /// The run against its estimate (the observer <see cref="Currency.TouchedRun"/>): beyond the factor
    /// either way, the file's scale, or the cost file's build and unit lines, are stale. The refresh
    /// names the scale at which this run's estimate equals its time.
    /// </summary>
    public static void CheckRun(Config config, double estimateSeconds, double actualSeconds, Func<double, double> estimateAt, Findings f) =>
        Currency.Factor(Currency.TouchedRun, [("this run's time", estimateSeconds, actualSeconds)], config.StaleFactor, config.StaleFloor, f, (_, actual) =>
        {
            if (estimateAt(0.05) > actual)
            {
                return $"The build and unit lines in {ShardPlan.CostFile} alone exceed this run: refresh them from its fixed-cost split";
            }

            if (estimateAt(20) < actual)
            {
                return $"No entry-scale up to 20 reaches this run: refresh {ShardPlan.CostFile}'s lines for the selected entries";
            }

            var (lo, hi) = (0.05, 20.0);
            for (var i = 0; i < 60; i++)
            {
                var mid = (lo + hi) / 2;
                (lo, hi) = estimateAt(mid) < actual ? (mid, hi) : (lo, mid);
            }

            return $"At entry-scale {N(lo)} this run's estimate would equal its time; refit the scale over the recent touched runs";
        });

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
