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
/// P4-11: counts can be edited to agree with a test that stopped running (the CRDT project's
/// npm-test finding: every test green, the only oracle in an excluded file). So every test written
/// in a test project's sources must appear executed in that project's results, no test may be
/// written under tests/ outside a project, and every harness target must run in the project its
/// entry names.
/// P5-07: `--projects A,B` holds all of this to the named projects only, for a harness entry whose
/// mechanism needs only them (`scripts/ci-test.sh A B`); CI names none. A name that is not a test
/// project fails, or a typo would narrow the run to nothing (sabotage S-count-4).
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
        var only = Options.Take(rest, "--projects");
        var f = new Findings();

        var all = repo.ProjectFiles()
            .Where(p => p.StartsWith("tests/", StringComparison.Ordinal))
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Order(StringComparer.Ordinal)
            .ToList();
        f.Require(all.Count > 0, "no test projects under tests/");
        var (expected, scopeProblems) = Scope(all, only);
        foreach (var problem in scopeProblems)
        {
            f.Fail(problem);
        }

        if (only is not null)
        {
            f.Note($"projects named: {string.Join(", ", expected)} ({expected.Count} of {all.Count}); the others are not checked by this run");
        }

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

        foreach (var extra in actual.Keys.Except(all, StringComparer.Ordinal))
        {
            f.Fail($"{extra}: results from a test assembly that is not a project under tests/");
        }

        foreach (var stale in baseline.Keys.Except(all, StringComparer.Ordinal))
        {
            f.Fail($"{stale}: in ci/test-baseline.txt but no such test project");
        }

        // Written versus ran (P4-11). A count can be edited to agree with a test that no longer runs;
        // a test written in a project's sources and absent from its results cannot.
        var executed = trx.SelectMany(ParseExecuted).GroupBy(t => t.Project)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Test).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var sources = Directory.EnumerateFiles(repo.PathOf("tests"), "*.cs", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(repo.Root, p).Replace('\\', '/'))
            .Where(p => !p.Split('/').Any(s => s is "bin" or "obj"))
            .Where(p => p.Split('/') is var parts && (parts.Length <= 2 || !all.Contains(parts[1], StringComparer.Ordinal) || expected.Contains(parts[1], StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal)
            .Select(p => (Path: p, Text: File.ReadAllText(repo.PathOf(p))));
        foreach (var problem in Unexecuted(sources, expected, executed))
        {
            f.Fail(problem);
        }

        foreach (var problem in TargetsNotExecuted(SabotageSpec.LoadAll(repo, f).Where(s => s.Kind == "test")
            .Where(s => expected.Contains(Path.GetFileName((s.Get("project") ?? "").TrimEnd('/')), StringComparer.Ordinal))
            .Select(s => (s.Id, Project: s.Get("project") ?? "", Target: s.Get("target") ?? "")), executed))
        {
            f.Fail(problem);
        }

        if (summary is not null)
        {
            File.AppendAllLines(summary, SummaryHeader
                .Concat(actual.Values.OrderBy(v => v.Project, StringComparer.Ordinal)
                    .Select(v => $"| {v.Project} | {v.Executed} | {v.Skipped} | {v.Duration.TotalSeconds:F1}s |")));
        }

        return f;
    }

    /// <summary>
    /// The projects a run covers: every test project when none are named, else the named ones, each of
    /// which must be a test project (an unknown name would narrow the run to nothing).
    /// </summary>
    internal static (IReadOnlyList<string> Expected, IReadOnlyList<string> Problems) Scope(IReadOnlyList<string> all, string? only)
    {
        if (only is null)
        {
            return (all, []);
        }

        var named = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        var problems = named.Except(all, StringComparer.Ordinal).Select(n => $"--projects: {n} is not a test project under tests/").ToList();
        if (named.Count == 0)
        {
            problems.Add("--projects names no project");
        }

        return (all.Intersect(named, StringComparer.Ordinal).ToList(), problems);
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

    /// <summary>Each executed (passed or failed) test in the file: its assembly and its class-qualified method name.</summary>
    internal static IEnumerable<(string Project, string Test)> ParseExecuted(string trxPath)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var doc = XDocument.Load(trxPath);
        var byId = doc.Descendants(ns + "UnitTest").ToDictionary(
            e => (string)e.Attribute("id")!,
            e => (Project: Path.GetFileNameWithoutExtension((string?)e.Attribute("storage") ?? "?"),
                  Test: e.Element(ns + "TestMethod") is { } m ? (string?)m.Attribute("className") + "." + (string?)m.Attribute("name") : "?"),
            StringComparer.Ordinal);
        return doc.Descendants(ns + "UnitTestResult")
            .Where(r => (string?)r.Attribute("outcome") is "Passed" or "Failed")
            .Select(r => byId.TryGetValue((string?)r.Attribute("testId") ?? "", out var t) ? t : ("?", "?"))
            .ToList();
    }

    /// <summary>
    /// The tests written in one source file: every method marked [Fact] or [Theory], qualified by the
    /// file's namespace and the top-level public class it sits in (nested helper classes are private).
    /// </summary>
    internal static IReadOnlyList<string> Written(string source)
    {
        var tests = new List<string>();
        string ns = "", cls = "";
        var pending = false;
        var inRawString = false;
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();

            // A raw string literal (a fixture's source, as in TestCountTests) is text, not tests.
            if (CountOf(line, "\"\"\"") % 2 == 1)
            {
                inRawString = !inRawString;
                continue;
            }

            if (inRawString)
            {
                continue;
            }

            if (line.StartsWith("namespace ", StringComparison.Ordinal))
            {
                ns = line["namespace ".Length..].TrimEnd(';', ' ');
            }
            else if (line.StartsWith("public ", StringComparison.Ordinal) && line.Contains(" class ", StringComparison.Ordinal))
            {
                var rest = line[(line.IndexOf(" class ", StringComparison.Ordinal) + " class ".Length)..];
                cls = new string(rest.TakeWhile(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            }
            else if (trimmed.StartsWith("[Fact", StringComparison.Ordinal) || trimmed.StartsWith("[Theory", StringComparison.Ordinal))
            {
                pending = true;
            }
            else if (pending && trimmed.StartsWith("public ", StringComparison.Ordinal) && trimmed.Contains('(', StringComparison.Ordinal))
            {
                var head = trimmed[..trimmed.IndexOf('(', StringComparison.Ordinal)];
                tests.Add($"{ns}.{cls}.{head[(head.LastIndexOf(' ') + 1)..]}");
                pending = false;
            }
        }

        return tests;
    }

    private static int CountOf(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    /// <summary>
    /// Tests written but not executed in their own project's results, and tests written under tests/
    /// outside any test project (they belong to no run).
    /// </summary>
    internal static IEnumerable<string> Unexecuted(IEnumerable<(string Path, string Text)> sources, IReadOnlyCollection<string> projects, IReadOnlyDictionary<string, HashSet<string>> executed)
    {
        foreach (var (path, text) in sources)
        {
            var parts = path.Split('/');
            var project = parts.Length > 2 && projects.Contains(parts[1], StringComparer.Ordinal) ? parts[1] : null;
            foreach (var test in Written(text))
            {
                if (project is null)
                {
                    yield return $"{test} is written in {path}, outside any test project: no run executes it";
                }
                else if (!executed.TryGetValue(project, out var ran) || !ran.Contains(test))
                {
                    yield return $"{project}: {test} is written in {path} but was not executed";
                }
            }
        }
    }

    /// <summary>Harness entries whose target was not executed in the project the entry names (a theory's target names one case; its method is what ran).</summary>
    internal static IEnumerable<string> TargetsNotExecuted(IEnumerable<(string Id, string Project, string Target)> entries, IReadOnlyDictionary<string, HashSet<string>> executed)
    {
        foreach (var (id, projectPath, target) in entries)
        {
            var project = Path.GetFileName(projectPath.TrimEnd('/'));
            var method = target.Contains('(', StringComparison.Ordinal) ? target[..target.IndexOf('(', StringComparison.Ordinal)] : target;
            if (!executed.TryGetValue(project, out var ran) || !ran.Contains(method))
            {
                yield return $"{id}: target {method} was not executed in {project}, the project the entry names";
            }
        }
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
