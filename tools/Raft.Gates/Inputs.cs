using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Raft.Gates;

/// <summary>
/// P13-01, phase 13 decision 5: every input a gate reads, registered with what verifies it, so a
/// gate reading a new file fails until someone says. The registry (<see cref="RegistryFile"/>) has
/// one line per input: <c>PATH KIND DETAIL</c>, the kind one of
///   - <c>currency OBSERVER</c>: a claim about something observable, compared with it on every run
///     of the observer (<see cref="Currency"/>);
///   - <c>construction GATE</c>: compared with what it describes, both ways, by that gate on every run;
///   - <c>subject REASON</c>: what a gate checks, not a premise;
///   - <c>decision REASON</c>: a chosen value with nothing to observe;
///   - <c>row "TEXT" ...</c>: a premise nothing checks yet, TEXT found in exactly one open register row.
/// The inputs are found, not listed: every tracked file under <c>ci/</c>, every <c>ci/</c> path a
/// tracked script, test, project file or workflow names, and every repository path a string literal
/// in the gates' source begins with (a bare top-level directory name is not counted: "ci" and
/// "compose" are also a workflow's and a job's names). What it cannot see: a value kept by hand
/// inside code, as the Budget seeds were until P13-01 moved them to a file, and a path a gate builds
/// from pieces. Vacuity risk: a registry checked against itself passes whatever the gates read;
/// guarded by finding the inputs from the source, sabotage S-input-1.
/// </summary>
internal static partial class Inputs
{
    public const string RegistryFile = "ci/inputs.txt";

    internal static readonly string[] Kinds = ["currency", "construction", "subject", "decision", "row"];

    internal sealed record Line(int Number, string Path, string Kind, string Detail);

    public static Findings Run(Repo repo, string[] args)
    {
        var f = new Findings();
        var tracked = repo.Git("ls-files").StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
        f.Require(tracked.Count > 0, "git ls-files listed nothing: no inputs could be found");
        var lines = Parse(File.Exists(repo.PathOf(RegistryFile)) ? repo.ReadText(RegistryFile) : "", f);
        var found = Discover(tracked, p => File.ReadAllText(repo.PathOf(p)));
        var openRows = Register.Rows(repo, f).Where(r => r.Status == "open").Select(r => r.Item).ToList();
        Evaluate(lines, found, tracked, Program.CommandNames, openRows, f);
        return f;
    }

    /// <summary>The registry's lines; a line that does not parse fails.</summary>
    internal static IReadOnlyList<Line> Parse(string text, Findings f)
    {
        var lines = new List<Line>();
        var n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !Kinds.Contains(parts[1]))
            {
                f.Fail($"{RegistryFile}:{n}: '{line}' is not PATH KIND DETAIL, KIND one of {string.Join(", ", Kinds)}");
                continue;
            }

            lines.Add(new Line(n, parts[0].TrimEnd('/'), parts[1], parts[2].Trim()));
        }

        f.Require(lines.Count > 0, $"{RegistryFile} registers no input");
        return lines;
    }

    /// <summary>Every input found, each with the files that name it.</summary>
    internal static IReadOnlyDictionary<string, SortedSet<string>> Discover(IReadOnlyList<string> tracked, Func<string, string> read)
    {
        var found = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(string path, string by)
        {
            if (!found.TryGetValue(path, out var set))
            {
                found[path] = set = new SortedSet<string>(StringComparer.Ordinal);
            }

            set.Add(by);
        }

        foreach (var p in tracked.Where(p => p.StartsWith("ci/", StringComparison.Ordinal)))
        {
            Add(p, "ci/");
        }

        foreach (var p in tracked.Where(Reader))
        {
            foreach (Match m in CiPath().Matches(read(p)))
            {
                Add(m.Value.TrimEnd('.'), p);
            }
        }

        var roots = tracked.GroupBy(p => p.Split('/')[0], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Any(p => p == g.Key), StringComparer.Ordinal);
        var token = new Regex(@"^(?:" + string.Join("|", roots.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")(?=$|/)(?:/[\w.{}*\-]+)*/?");
        foreach (var p in tracked.Where(p => p.StartsWith("tools/Raft.Gates/", StringComparison.Ordinal) && p.EndsWith(".cs", StringComparison.Ordinal)))
        {
            foreach (Match lit in StringLiteral().Matches(read(p)))
            {
                var t = token.Match(lit.Groups[1].Value);
                if (!t.Success)
                {
                    continue;
                }

                var root = t.Value.Split('/')[0];
                if (t.Value.Contains('/', StringComparison.Ordinal) || roots[root])
                {
                    Add(Placeholder().Replace(t.Value, "*").TrimEnd('/'), p);
                }
            }
        }

        return found;
    }

    /// <summary>A tracked file that may read a ci/ input: a script, a test or project, a workflow; not the inputs themselves, the documents, the sabotage patches, or the gates' own tests (their fixtures).</summary>
    private static bool Reader(string p) =>
        !p.StartsWith("ci/", StringComparison.Ordinal) && !p.StartsWith("docs/", StringComparison.Ordinal) && !p.StartsWith("sabotage/", StringComparison.Ordinal)
        && !p.StartsWith("tests/Raft.Gates.Tests/", StringComparison.Ordinal)
        && (p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".sh", StringComparison.Ordinal) || p.EndsWith(".csproj", StringComparison.Ordinal)
            || p.EndsWith(".props", StringComparison.Ordinal) || p.EndsWith(".yml", StringComparison.Ordinal) || p.EndsWith(".yaml", StringComparison.Ordinal));

    /// <summary>The decision, apart from git and the file system so it can be tested on written inputs.</summary>
    internal static void Evaluate(IReadOnlyList<Line> lines, IReadOnlyDictionary<string, SortedSet<string>> found, IReadOnlyList<string> tracked, IReadOnlyCollection<string> gates, IReadOnlyList<string> openRows, Findings f)
    {
        var byPath = lines.GroupBy(l => l.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var (path, dup) in byPath.Where(kv => kv.Value.Count > 1))
        {
            f.Fail($"{RegistryFile}: {path} is registered {dup.Count} times (lines {string.Join(", ", dup.Select(d => d.Number))})");
        }

        foreach (var (path, by) in found)
        {
            f.Require(byPath.ContainsKey(path), $"{path}: read by {string.Join(", ", by)} and not in {RegistryFile}: register it with what verifies it (if nothing does yet, a row)");
        }

        foreach (var l in lines)
        {
            f.Require(Exists(l.Path, tracked), $"{RegistryFile}:{l.Number}: {l.Path} names no tracked file");
            var word = l.Detail.Split(' ', 2)[0].TrimEnd(':');
            switch (l.Kind)
            {
                case "currency":
                    var o = Currency.Observers.FirstOrDefault(x => x.Id == word);
                    if (o is null)
                    {
                        f.Fail($"{RegistryFile}:{l.Number}: no observer '{word}' (observers: {string.Join(", ", Currency.Observers.Select(x => x.Id))})");
                    }
                    else
                    {
                        f.Require(o.Input == l.Path, $"{RegistryFile}:{l.Number}: observer {o.Id} checks {o.Input}, not {l.Path}");
                    }

                    break;
                case "construction":
                    f.Require(gates.Contains(word), $"{RegistryFile}:{l.Number}: no gate '{word}' to compare {l.Path} with what it describes");
                    break;
                case "row":
                    var quoted = RowText().Match(l.Detail);
                    var hits = quoted.Success ? openRows.Count(r => r.Contains(quoted.Groups[1].Value, StringComparison.Ordinal)) : 0;
                    f.Require(hits == 1, $"{RegistryFile}:{l.Number}: {l.Path}'s row \"{(quoted.Success ? quoted.Groups[1].Value : l.Detail)}\" matches {hits} open register rows, not one");
                    break;
            }
        }

        foreach (var o in Currency.Observers)
        {
            var named = lines.Count(l => l.Kind == "currency" && l.Detail.Split(' ', 2)[0].TrimEnd(':') == o.Id);
            f.Require(named == 1, $"observer {o.Id} ({o.RunsIn}) is named by {named} registry lines, not one");
        }

        f.Note($"{found.Count} inputs found, {lines.Count} registered: " + string.Join(", ", Kinds.Select(k => $"{lines.Count(l => l.Kind == k)} {k}")));
    }

    private static bool Exists(string path, IReadOnlyList<string> tracked)
    {
        if (path.Contains('*', StringComparison.Ordinal))
        {
            var glob = new Regex("^" + Regex.Escape(path).Replace(@"\*", "[^/]+", StringComparison.Ordinal) + "(?:/|$)");
            return tracked.Any(t => glob.IsMatch(t));
        }

        return tracked.Any(t => t == path || t.StartsWith(path + "/", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"(?<![\w.\-])ci/[\w.\-]+")]
    private static partial Regex CiPath();

    [GeneratedRegex(@"(?<!\\)""((?:[^""\\\n]|\\.)*)""")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^""([^""]+)""")]
    private static partial Regex RowText();
}
