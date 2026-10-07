using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Raft.Gates;

/// <summary>
/// P10-01, phase 10 decision 5: the configuration beside every number, made mechanical. A
/// measurement is a record under <see cref="Directory"/>, one JSON file written by the measuring
/// tool, whose <c>config</c> carries every field in <see cref="ConfigFields"/>, each non-empty. In a
/// report of phase 10 or later, a table row holding a number must cite where it came from: a record
/// (<c>m:ID</c>, which must exist) or a CI run (<c>run:ID</c>). Vacuity risk: a gate that reads no
/// records passes an empty directory; guarded by requiring a record once any phase-10 report exists,
/// and by S-meas-1 and S-meas-2.
/// </summary>
internal static partial class Measurements
{
    public const string Directory = "measurements";

    /// <summary>What a number is meaningless without (decision 5).</summary>
    public static readonly string[] ConfigFields =
    [
        "commit", "configuration", "sdk", "runtime", "image", "os", "kernel", "cpuModel", "cpuCount", "cpuLimit",
        "gc", "tieredCompilation", "tieredPgo", "dataFileSystem", "warmup", "repetition", "load",
    ];

    public static Findings Run(Repo repo, string[] args)
    {
        var f = new Findings();
        var records = Load(repo, f);
        var reports = Reports(repo);
        f.Require(reports.Count == 0 || records.Count > 0, $"a phase-10 report exists but {Directory}/ holds no record");
        foreach (var (path, text) in reports)
        {
            foreach (var problem in ReportProblems(text, records.Keys.ToHashSet(StringComparer.Ordinal)))
            {
                f.Fail($"{path}: {problem}");
            }
        }

        f.Note($"{records.Count} records, {reports.Count} reports checked");
        return f;
    }

    /// <summary>Every record, by id; a record whose id is not its file name, or whose configuration is incomplete, fails.</summary>
    internal static Dictionary<string, JsonElement> Load(Repo repo, Findings f)
    {
        var records = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var dir = repo.PathOf(Directory);
        if (!System.IO.Directory.Exists(dir))
        {
            return records;
        }

        foreach (var file in System.IO.Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement.Clone();
            foreach (var problem in RecordProblems(name, root))
            {
                f.Fail($"{Directory}/{name}.json: {problem}");
            }

            records[name] = root;
        }

        return records;
    }

    /// <summary>A record's own problems: its id, and each configuration field missing or empty.</summary>
    internal static IReadOnlyList<string> RecordProblems(string name, JsonElement record)
    {
        var problems = new List<string>();
        if (!record.TryGetProperty("id", out var id) || id.GetString() != name)
        {
            problems.Add($"its id is not its file name ({name})");
        }

        if (!record.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
        {
            problems.Add("no config");
            return problems;
        }

        foreach (var field in ConfigFields)
        {
            if (!config.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                problems.Add($"config.{field} is missing or empty: a number without it is not a measurement");
            }
        }

        if (!record.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object || !results.EnumerateObject().Any())
        {
            problems.Add("no results");
        }

        return problems;
    }

    /// <summary>
    /// Each table row (not a header or separator) holding a digit that cites neither an existing
    /// record (<c>m:ID</c>) nor a CI run (<c>run:ID</c>).
    /// </summary>
    internal static IReadOnlyList<string> ReportProblems(string report, IReadOnlySet<string> records)
    {
        var problems = new List<string>();
        var inTable = false;
        foreach (var (line, number) in report.Split('\n').Select((l, i) => (l.TrimEnd('\r'), i + 1)))
        {
            if (!line.TrimStart().StartsWith('|'))
            {
                inTable = false;
                continue;
            }

            if (!inTable)
            {
                inTable = true; // the header row
                continue;
            }

            if (Separator().IsMatch(line) || !line.Any(char.IsAsciiDigit))
            {
                continue;
            }

            var cited = Citation().Matches(line).Select(m => (m.Groups["kind"].Value, m.Groups["id"].Value)).ToList();
            if (cited.Count == 0)
            {
                problems.Add($"line {number}: a number with no record or run cited (m:ID or run:ID): {line.Trim()}");
                continue;
            }

            foreach (var (kind, id) in cited.Where(c => c.Item1 == "m" && !records.Contains(c.Item2)))
            {
                problems.Add($"line {number}: cites m:{id}, which is not in {Directory}/");
            }
        }

        return problems;
    }

    /// <summary>docs/phases/P{n}/report.md for n of 10 and over: the reports this gate holds to records.</summary>
    private static List<(string Path, string Text)> Reports(Repo repo) =>
        System.IO.Directory.GetDirectories(repo.PathOf("docs/phases"))
            .Select(d => (Dir: d, Phase: PhaseNumber().Match(Path.GetFileName(d))))
            .Where(x => x.Phase.Success && int.Parse(x.Phase.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 10 && File.Exists(Path.Combine(x.Dir, "report.md")))
            .Select(x => (Path.GetRelativePath(repo.Root, Path.Combine(x.Dir, "report.md")), File.ReadAllText(Path.Combine(x.Dir, "report.md"))))
            .ToList();

    [GeneratedRegex(@"^\s*\|[\s:\-|]+\|\s*$")]
    private static partial Regex Separator();

    [GeneratedRegex(@"\b(?<kind>m|run):(?<id>[A-Za-z0-9][A-Za-z0-9._-]*)")]
    private static partial Regex Citation();

    [GeneratedRegex(@"^P(\d+)$")]
    private static partial Regex PhaseNumber();
}
