using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Raft.Gates;

/// <summary>
/// The test-count floor (spec §4, §12). `dotnet test` reports success for a project whose tests
/// were not discovered, so every test project on disk must have produced a TRX in which it
/// executed exactly as many tests as ci/test-baseline.txt records. Exact, not "at least": with a
/// floor, adding ten tests and deleting five passes; with equality every change to the count is a
/// visible edit to the baseline in the same commit.
/// Vacuity risk: no TRX files at all, and a loop over nothing — guarded by requiring one result
/// set per test project on disk.
/// </summary>
internal static class TestCount
{
    private static readonly string[] SummaryHeader = ["| Test project | Executed | Skipped | Duration |", "|---|---:|---:|---:|"];

    internal sealed record ProjectResult(string Project, int Executed, int Skipped, TimeSpan Duration);

    public static Findings Run(Repo repo, string[] args)
    {
        var rest = args.ToList();
        var results = Options.Take(rest, "--results") ?? "TestResults";
        var summary = Options.Take(rest, "--summary") ?? Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        var f = new Findings();

        var expected = repo.ProjectFiles()
            .Where(p => p.StartsWith("tests/", StringComparison.Ordinal))
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Order(StringComparer.Ordinal)
            .ToList();
        f.Require(expected.Count > 0, "no test projects under tests/");

        var dir = repo.PathOf(results);
        var trx = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.trx", SearchOption.AllDirectories) : [];
        f.Require(trx.Length > 0, $"no TRX files under {results} — did the test run write any?");

        var actual = trx.SelectMany(Parse).GroupBy(r => r.Project)
            .ToDictionary(g => g.Key, g => new ProjectResult(g.Key, g.Sum(r => r.Executed), g.Sum(r => r.Skipped),
                TimeSpan.FromTicks(g.Sum(r => r.Duration.Ticks))), StringComparer.Ordinal);
        var baseline = ReadBaseline(repo, f);

        foreach (var project in expected)
        {
            if (!actual.TryGetValue(project, out var r))
            {
                f.Fail($"{project}: no TRX results");
                continue;
            }

            f.Require(r.Executed > 0, $"{project}: 0 executed ({r.Skipped} skipped)");
            if (!baseline.TryGetValue(project, out var floor))
            {
                f.Fail($"{project}: not in ci/test-baseline.txt (executed {r.Executed})");
            }
            else
            {
                f.Require(r.Executed == floor, $"{project}: {r.Executed} executed, baseline {floor} — change ci/test-baseline.txt in the same commit if intended");
            }

            f.Note($"{project}: {r.Executed} executed, {r.Skipped} skipped, {r.Duration.TotalSeconds:F1}s");
        }

        foreach (var extra in actual.Keys.Except(expected, StringComparer.Ordinal))
        {
            f.Fail($"{extra}: results from a test assembly that is not a project under tests/");
        }

        foreach (var stale in baseline.Keys.Except(expected, StringComparer.Ordinal))
        {
            f.Fail($"{stale}: in ci/test-baseline.txt but no such test project");
        }

        if (summary is not null)
        {
            File.AppendAllLines(summary, SummaryHeader
                .Concat(actual.Values.OrderBy(v => v.Project, StringComparer.Ordinal)
                    .Select(v => $"| {v.Project} | {v.Executed} | {v.Skipped} | {v.Duration.TotalSeconds:F1}s |")));
        }

        return f;
    }

    /// <summary>One result per test assembly in the file, named after the assembly's file name.</summary>
    internal static IEnumerable<ProjectResult> Parse(string trxPath)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var doc = XDocument.Load(trxPath);

        var storageById = doc.Descendants(ns + "UnitTest").ToDictionary(
            e => (string)e.Attribute("id")!,
            e => Path.GetFileNameWithoutExtension((string?)e.Attribute("storage") ?? "?"),
            StringComparer.Ordinal);

        return doc.Descendants(ns + "UnitTestResult")
            .GroupBy(r => storageById.GetValueOrDefault((string?)r.Attribute("testId") ?? "", "?"))
            .Select(g => new ProjectResult(
                g.Key,
                Executed: g.Count(r => (string?)r.Attribute("outcome") is "Passed" or "Failed"),
                Skipped: g.Count(r => (string?)r.Attribute("outcome") is not ("Passed" or "Failed")),
                Duration: TimeSpan.FromTicks(g.Sum(r => TimeSpan.Parse((string?)r.Attribute("duration") ?? "0", CultureInfo.InvariantCulture).Ticks))))
            .ToList();
    }

    private static Dictionary<string, int> ReadBaseline(Repo repo, Findings f)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = repo.PathOf("ci/test-baseline.txt");
        if (!File.Exists(path))
        {
            f.Fail("ci/test-baseline.txt missing");
            return map;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                f.Fail($"ci/test-baseline.txt: malformed line '{raw}'");
                continue;
            }

            map[parts[0]] = n;
        }

        return map;
    }
}
