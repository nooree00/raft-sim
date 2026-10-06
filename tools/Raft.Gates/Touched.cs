using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Raft.Gates;

/// <summary>An entry's footprint for the touched-file selection: its patch and, if it has one, its control.</summary>
internal sealed record TouchedEntry(string Id, string Patch, string? Control);

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

    /// <summary>What a run of the selection is expected to cost, and whether it goes ahead.</summary>
    internal sealed record Plan(int Selected, int Manifest, TimeSpan Estimate, bool Run, string Line);

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

    /// <summary>The ids of the entries whose patch or control names one of <paramref name="changed"/>.</summary>
    public static IReadOnlyList<string> Select(IEnumerable<TouchedEntry> entries, IReadOnlySet<string> changed) =>
        entries.Where(e => PathsIn(e.Patch).Concat(e.Control is null ? [] : PathsIn(e.Control)).Any(changed.Contains)).Select(e => e.Id).ToList();

    /// <summary>The per-entry rate and the threshold, from <see cref="ConfigFile"/>: "seconds-per-entry N" and "confirm-above-minutes N".</summary>
    public static (double SecondsPerEntry, double ConfirmAboveMinutes) ParseConfig(string text)
    {
        double? rate = null, threshold = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            var value = double.Parse(parts[1], CultureInfo.InvariantCulture);
            switch (parts[0])
            {
                case "seconds-per-entry":
                    rate = value;
                    break;
                case "confirm-above-minutes":
                    threshold = value;
                    break;
                default:
                    throw new FormatException($"{ConfigFile}: unknown key '{parts[0]}'");
            }
        }

        return (rate ?? throw new FormatException($"{ConfigFile}: no seconds-per-entry"), threshold ?? throw new FormatException($"{ConfigFile}: no confirm-above-minutes"));
    }

    /// <summary>The plan for <paramref name="selected"/> of <paramref name="manifest"/> entries.</summary>
    public static Plan PlanFor(int selected, int manifest, double secondsPerEntry, double confirmAboveMinutes, bool accepted)
    {
        var estimate = TimeSpan.FromSeconds(selected * secondsPerEntry);
        var fraction = manifest == 0 ? 0 : 100.0 * selected / manifest;
        var over = estimate.TotalMinutes > confirmAboveMinutes;
        var run = selected > 0 && (!over || accepted);
        var verdict = selected == 0 ? "nothing to run"
            : !over ? "running"
            : accepted ? $"over the {N(confirmAboveMinutes)}-minute threshold, accepted: running"
            : $"over the {N(confirmAboveMinutes)}-minute threshold: not run; pass --accept-estimate to run it, and record the decision";
        var line = $"touched: {selected} of {manifest} entries ({fraction.ToString("F1", CultureInfo.InvariantCulture)}% of the manifest), estimated {estimate.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)} min at {N(secondsPerEntry)} s per entry; {verdict}";
        return new Plan(selected, manifest, estimate, run, line);
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
