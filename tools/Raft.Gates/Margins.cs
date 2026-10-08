using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

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
internal static class Margins
{
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
        var data = Options.Take(rest, "--data") ?? "docs/phases/P12/margins.tsv";
        var f = new Findings();
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

    private static string Show(double margin) => double.IsPositiveInfinity(margin) ? "never reaches it" : string.Create(CultureInfo.InvariantCulture, $"{margin:F2} times");
}
