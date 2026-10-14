using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Raft.Gates;

/// <summary>
/// P12-08, phase 12 decision 7: every check whose verdict compares a quantity that varies from run
/// to run with a threshold, and how far each side of it sits from that threshold. A row is one
/// bound of one check: the side that must pass (the unpatched code) and, when a sabotage is meant to
/// cross this bound, the side that must fail (the patched code), each over several runs. A timing
/// is taken at GitHub's ratio to this machine, 1.1 to 1.5 (phase 7), at whichever end is worse for
/// the side. A row is flagged when either side comes within a factor of 1.5 of the bound in any
/// run. Vacuity risk: reading only the unpatched side, as phase 11's stall control was calibrated
/// (S-bench-1 then survived 2 of 13 runs on GitHub, its patched side near the bound); guarded by
/// computing both sides on every row, sabotage S-margin-1.
/// </summary>
internal static partial class Margins
{
    /// <summary>The bench controls' margin tables, one a phase (P14-06): `scripts/margins.sh` writes them.</summary>
    public const string BenchTables = "docs/phases/*/margins-bench.tsv";

    /// <summary>
    /// What each family of rows describes (P14-06): a row is stale once one of these changed after the
    /// commit it was measured at. The replication cost check's rows are the Scale suite's; every other row
    /// is a host bench control's.
    /// </summary>
    internal static IReadOnlyList<string> SourcesOf(string check) => check.StartsWith("cost-", StringComparison.Ordinal)
        ? ["src/Raft.Core", "tests/Raft.Scale.Tests", "scripts/margins.sh"]
        : ["src/Raft.Host", "tests/Raft.Host.Slow.Tests", "scripts/margins.sh"];

    /// <summary>The factor a side must keep from its bound.</summary>
    public const double Required = 1.5;

    /// <summary>GitHub's runs take 1.1 to 1.5 times as long as this machine's (phase 7).</summary>
    public const double GitHubLow = 1.1;

    public const double GitHubHigh = 1.5;

    /// <summary>One bound of one check. <paramref name="PassAbove"/>: the unpatched side passes when its value is at or above the bound (else at or below). <paramref name="Patched"/> is empty when no sabotage is meant to cross this bound.</summary>
    internal sealed record Row(string Check, double Bound, bool PassAbove, bool Timing, IReadOnlyList<double> Unpatched, IReadOnlyList<double> Patched, string Note = "");

    /// <summary>How many times the bound each side keeps (infinity when it cannot reach it), and whether the row is flagged.</summary>
    internal sealed record Verdict(Row Row, double UnpatchedMargin, double PatchedMargin)
    {
        public bool Flagged => UnpatchedMargin < Required || PatchedMargin < Required;
    }

    public static Verdict Judge(Row row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var unpatched = row.Unpatched.Count == 0 ? 0 : Margin(row, row.Unpatched, mustExceed: row.PassAbove);
        var patched = row.Patched.Count == 0 ? double.PositiveInfinity : Margin(row, row.Patched, mustExceed: !row.PassAbove);
        return new Verdict(row, unpatched, patched);
    }

    /// <summary>
    /// The factor by which the worst value on this side clears the bound in the direction it must:
    /// for a side that must stay above, its smallest value (a timing at GitHub's low ratio) over the
    /// bound; for a side that must stay below, the bound over its largest (a timing at GitHub's high
    /// ratio). A bound of zero is a count that must be zero, measured against one.
    /// </summary>
    private static double Margin(Row row, IReadOnlyList<double> values, bool mustExceed)
    {
        var bound = row.Bound == 0 ? 1 : row.Bound;
        if (mustExceed)
        {
            var worst = values.Min() * (row.Timing ? GitHubLow : 1);
            return worst <= 0 ? 0 : worst / bound;
        }
        else
        {
            var worst = values.Max() * (row.Timing ? GitHubHigh : 1);
            return row.Bound == 0 ? (worst <= 0 ? double.PositiveInfinity : 0) : worst <= 0 ? double.PositiveInfinity : bound / worst;
        }
    }

    /// <summary>
    /// The rows of a margins file: tab-separated, `check bound above|below timing|ratio unpatched
    /// patched note`, each side a comma-separated list of values (`-` for a side with none).
    /// </summary>
    public static List<Row> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var rows = new List<Row>();
        foreach (var line in lines.Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var cols = line.Split('\t');
            if (cols.Length < 6)
            {
                throw new FormatException($"a margins row needs at least six tab-separated columns: {line}");
            }

            rows.Add(new Row(cols[0], Number(cols[1]), cols[2] == "above", cols[3] == "timing", Values(cols[4]), Values(cols[5]), cols.Length > 6 ? cols[6] : ""));
        }

        return rows;
    }

    private static double Number(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static List<double> Values(string s) => s == "-" ? [] : [.. s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Number)];

    /// <summary>`gates margins --data FILE`: the table, and a failure for each flagged row.</summary>
    public static Findings Run(Repo repo, string[] args)
    {
        var rest = args.ToList();
        var currency = rest.Remove("--currency");
        var data = Options.Take(rest, "--data") ?? NewestBenchTable(repo);
        var f = new Findings();
        if (currency)
        {
            if (data is null)
            {
                f.Fail($"no bench margins table ({BenchTables})");
                return f;
            }

            var lines = File.ReadAllLines(repo.PathOf(data));
            var head = repo.Git("rev-parse", "HEAD").StdOut.Trim();
            var atReport = repo.Git("show", "--name-only", "--format=", "HEAD").StdOut.Split('\n').Any(l => ReportPath().IsMatch(l.Trim()));
            CheckCurrency(data, RowCommits(lines, Parse(lines).Select(r => r.Check)),
                check => repo.Git(["log", "-1", "--format=%H", "--", .. SourcesOf(check)]).StdOut.Trim(),
                (ancestor, commit) => repo.Git("merge-base", "--is-ancestor", ancestor, commit).ExitCode == 0,
                atReport, head, f);
            return f;
        }

        if (data is null)
        {
            f.Fail($"no bench margins table ({BenchTables})");
            return f;
        }

        var path = repo.PathOf(data);
        if (!File.Exists(path))
        {
            f.Fail($"no margins file at {data}");
            return f;
        }

        var verdicts = Parse(File.ReadAllLines(path)).Select(Judge).ToList();
        f.Require(verdicts.Count > 0, $"{data} has no rows");
        foreach (var v in verdicts)
        {
            var line = string.Create(CultureInfo.InvariantCulture, $"{v.Row.Check}: bound {v.Row.Bound} ({(v.Row.PassAbove ? "pass above" : "pass below")}{(v.Row.Timing ? ", a timing" : "")}); unpatched {Show(v.UnpatchedMargin)} over {v.Row.Unpatched.Count} runs, patched {(v.Row.Patched.Count == 0 ? "not crossing this bound" : Show(v.PatchedMargin) + $" over {v.Row.Patched.Count} runs")}");
            if (v.Flagged)
            {
                f.Fail(line + $": within a factor of {Required} of its bound");
            }
            else
            {
                f.Note(line);
            }
        }

        return f;
    }

    /// <summary>The newest phase's bench margins table, or null when no phase has one.</summary>
    internal static string? NewestBenchTable(Repo repo) =>
        System.IO.Directory.GetDirectories(repo.PathOf("docs/phases"))
            .Select(d => (Dir: d, Phase: PhaseNumber().Match(Path.GetFileName(d))))
            .Where(x => x.Phase.Success && File.Exists(Path.Combine(x.Dir, "margins-bench.tsv")))
            .OrderByDescending(x => int.Parse(x.Phase.Groups[1].Value, CultureInfo.InvariantCulture))
            .Select(x => Path.GetRelativePath(repo.Root, Path.Combine(x.Dir, "margins-bench.tsv")).Replace('\\', '/'))
            .FirstOrDefault();

    /// <summary>
    /// Each row's measurement commit (P14-06): the table's header names one for every row ("... at
    /// &lt;commit&gt;."), and a line `# at &lt;commit&gt;: &lt;checks&gt;` overrides it for the checks it names
    /// (a control measured again alone). A row with neither has no commit.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> RowCommits(IReadOnlyList<string> lines, IEnumerable<string> checks)
    {
        var header = lines.Where(l => l.StartsWith('#')).Select(l => HeaderCommit().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        var commits = checks.ToDictionary(c => c, _ => header, StringComparer.Ordinal);
        foreach (var m in lines.Select(l => OverrideCommit().Match(l)).Where(m => m.Success))
        {
            foreach (var check in m.Groups[2].Value.Split('(')[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (commits.ContainsKey(check))
                {
                    commits[check] = m.Groups[1].Value;
                }
            }
        }

        return commits;
    }

    /// <summary>
    /// The observer <see cref="Currency.MarginsCommit"/> (P14-06): each row measured at or after the last
    /// change to what it describes. Stale rows fail only when the head commit carries a phase report: an
    /// audit takes over an hour and belongs once a phase, before its report, so on any other push they are
    /// noted. Vacuity risk: an observer reading a row's commit as current passes every table (S-margin-2).
    /// </summary>
    internal static void CheckCurrency(string table, IReadOnlyDictionary<string, string?> rows, Func<string, string> lastChange, Func<string, string, bool> isAncestor, bool atReport, string head, Findings f)
    {
        // A row is current when its commit contains the last change to its control's sources.
        var last = rows.Keys.ToDictionary(c => c, lastChange, StringComparer.Ordinal);
        var judged = new Findings();
        var stale = 0;
        foreach (var (check, commit) in rows)
        {
            var current = commit is not null && last[check].Length > 0 && isAncestor(last[check], commit);
            if (!current)
            {
                stale++;
                judged.Fail(Currency.Text(Currency.MarginsCommit, check, commit ?? "no commit", $"its control's sources last changed at {Short(last[check])}, after it", "measured at or after the last change to its control's sources", "Run scripts/margins.sh at the head and write its table for this phase"));
            }
        }

        foreach (var failure in judged.Failures)
        {
            if (atReport)
            {
                f.Fail(failure);
            }
            else
            {
                f.Note("would fail a report: " + failure);
            }
        }

        f.Note($"currency {Currency.MarginsCommit.Id}: {rows.Count} row(s) in {table} against {Currency.MarginsCommit.Source}; {stale} stale{(atReport ? ", the head carries a phase report" : $", not judged: the head ({Short(head)}) carries no phase report")}");
    }

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;

    [GeneratedRegex(@"\bat ([0-9a-f]{7,40})\.")]
    private static partial Regex HeaderCommit();

    [GeneratedRegex(@"^# at ([0-9a-f]{7,40}): (.+)$")]
    private static partial Regex OverrideCommit();

    [GeneratedRegex(@"^docs/phases/P\d+/report\.md$")]
    private static partial Regex ReportPath();

    [GeneratedRegex(@"^P(\d+)$")]
    private static partial Regex PhaseNumber();

    private static string Show(double margin) => double.IsPositiveInfinity(margin) ? "never reaches it" : string.Create(CultureInfo.InvariantCulture, $"{margin:F2} times");
}
