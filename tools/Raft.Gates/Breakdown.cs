using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Raft.Gates;

/// <summary>A task parsed from docs/phases/P*/breakdown.md.</summary>
internal sealed record BreakdownTask(string Id, string File, IReadOnlyDictionary<string, string> Fields)
{
    public string Phase => Id[..Id.IndexOf('-', StringComparison.Ordinal)];
}

/// <summary>
/// Spec §12: a breakdown missing a vacuity risk, a sabotage, or a verifiability answer is
/// malformed. This checks presence, shape, and that sabotage ids resolve to real entries — not
/// whether the prose is good, which stays the reviewer's.
/// Vacuity risk: the heading format drifts and zero tasks parse; guarded by rejecting any "### P"
/// heading that does not parse and requiring at least one task per file.
/// </summary>
internal static partial class Breakdown
{
    public static readonly string[] Fields = ["Task", "Vacuity", "Sabotage", "Verifiable here", "Prediction", "Outcome"];

    private static readonly string[] Placeholders = ["tbd", "todo", "n/a", "na", "-", "?", "...", "…", "none", "x"];

    public static Findings Run(Repo repo, string[] args)
    {
        var rest = args.ToList();
        var final = Options.Take(rest, "--final");
        var f = new Findings();

        var tasks = LoadAll(repo, f);
        var started = Trailers.Commits(repo).SelectMany(c => c.Tasks).ToHashSet(StringComparer.Ordinal);
        var sabotages = SabotageSpec.LoadAll(repo, f).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var t in tasks)
        {
            // A task's sabotage entries must exist once its implementation has begun (its first
            // Task: trailer), and for every task at the end of the phase.
            var mustResolve = started.Contains(t.Id) || final is not null;
            CheckTask(t, sabotages, referenced, f, mustResolve);
            if (final is not null && t.Phase == final)
            {
                f.Require(t.Fields.GetValueOrDefault("Outcome") != "pending", $"{t.Id}: outcome still pending at the end of {final}");
            }
        }

        foreach (var orphan in sabotages.Except(referenced).Order(StringComparer.Ordinal))
        {
            f.Fail($"{orphan}: sabotage not referenced by any task");
        }

        f.Note($"{tasks.Count} tasks, {sabotages.Count} sabotages");
        return f;
    }

    public static IReadOnlyList<BreakdownTask> LoadAll(Repo repo, Findings f)
    {
        var dir = repo.PathOf("docs/phases");
        var files = Directory.Exists(dir)
            ? Directory.GetDirectories(dir, "P*").Select(d => Path.Combine(d, "breakdown.md")).Where(File.Exists).Order(StringComparer.Ordinal).ToList()
            : [];
        f.Require(files.Count > 0, "no docs/phases/P*/breakdown.md");

        var tasks = new List<BreakdownTask>();
        foreach (var file in files)
        {
            var phase = Path.GetFileName(Path.GetDirectoryName(file)!);
            var parsed = Parse(Path.GetRelativePath(repo.Root, file), File.ReadAllLines(file), f);
            f.Require(parsed.Count > 0, $"{phase}: no tasks parsed");
            foreach (var t in parsed)
            {
                f.Require(t.Phase == phase, $"{t.Id}: task in {phase}'s breakdown");
            }

            tasks.AddRange(parsed);
        }

        foreach (var dup in tasks.GroupBy(t => t.Id).Where(g => g.Count() > 1))
        {
            f.Fail($"{dup.Key}: defined {dup.Count()} times");
        }

        return tasks;
    }

    internal static List<BreakdownTask> Parse(string file, IReadOnlyList<string> lines, Findings f)
    {
        var tasks = new List<BreakdownTask>();
        string? id = null;
        Dictionary<string, string>? fields = null;

        void Flush()
        {
            if (id is not null)
            {
                tasks.Add(new BreakdownTask(id, file, fields!));
            }

            id = null;
            fields = null;
        }

        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
            {
                Flush();
                if (line.StartsWith("### P", StringComparison.Ordinal))
                {
                    var m = TaskHeading().Match(line);
                    if (!m.Success)
                    {
                        f.Fail($"{file}: unparseable task heading '{line}'");
                        continue;
                    }

                    id = m.Groups["id"].Value;
                    fields = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                continue;
            }

            if (fields is null)
            {
                continue;
            }

            var fm = FieldLine().Match(line);
            if (fm.Success)
            {
                var name = fm.Groups["name"].Value;
                if (!Fields.Contains(name))
                {
                    f.Fail($"{id}: unknown field '{name}'");
                }
                else if (!fields.TryAdd(name, fm.Groups["value"].Value.Trim()))
                {
                    f.Fail($"{id}: field '{name}' given twice");
                }
            }
            else if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                f.Fail($"{id}: bullet that is not a '- **Field:** value' line: '{line.Trim()}'");
            }
        }

        Flush();
        return tasks;
    }

    internal static void CheckTask(BreakdownTask t, IReadOnlySet<string> sabotages, ISet<string> referenced, Findings f, bool mustResolve)
    {
        foreach (var name in Fields)
        {
            if (!t.Fields.TryGetValue(name, out var value))
            {
                f.Fail($"{t.Id}: missing field {name}");
            }
            else if (value.Length < 3 || Placeholders.Contains(value.ToLowerInvariant().Trim('.', ' ')))
            {
                f.Fail($"{t.Id}: field {name} is a placeholder ('{value}')");
            }
        }

        if (t.Fields.TryGetValue("Sabotage", out var sab))
        {
            var parts = sab.Split("; manual:", 2, StringSplitOptions.TrimEntries);
            var ids = parts[0].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var manual = parts.Length > 1 && parts[1].Length > 0;
            foreach (var sid in ids)
            {
                if (!SabotageId().IsMatch(sid))
                {
                    f.Fail($"{t.Id}: '{sid}' is not a sabotage id (S-name-n)");
                }
                else if (!sabotages.Contains(sid))
                {
                    if (mustResolve)
                    {
                        f.Fail($"{t.Id}: sabotage {sid} has no sabotage/{sid}/ entry");
                    }
                }
                else
                {
                    referenced.Add(sid);
                }
            }

            f.Require(ids.Length > 0 || manual, $"{t.Id}: no sabotage ids and no '; manual:' description");
            if (ids.Length == 0)
            {
                f.Require(!t.Fields.GetValueOrDefault("Verifiable here", "").StartsWith("yes", StringComparison.Ordinal),
                    $"{t.Id}: verifiable here 'yes' but the sabotage is manual only");
            }
        }

        if (t.Fields.TryGetValue("Verifiable here", out var ver))
        {
            f.Require(Verifiable().IsMatch(ver), $"{t.Id}: Verifiable here must be 'yes|partial|no — reason'");
        }

        if (t.Fields.TryGetValue("Prediction", out var pred))
        {
            f.Require(pred.Contains("**Observable:**", StringComparison.Ordinal), $"{t.Id}: prediction has no **Observable:**");
        }

        if (t.Fields.TryGetValue("Outcome", out var outcome))
        {
            f.Require(outcome == "pending" || OutcomeLine().IsMatch(outcome),
                $"{t.Id}: Outcome must be 'pending' or 'right|wrong|partly (evidence|forcing) — what happened'");
        }
    }

    [GeneratedRegex(@"^### (?<id>P\d+-\d{2}) — \S.*$")]
    private static partial Regex TaskHeading();

    [GeneratedRegex(@"^- \*\*(?<name>[^*]+):\*\* ?(?<value>.*)$")]
    private static partial Regex FieldLine();

    [GeneratedRegex(@"^S-[a-z]+-\d+$")]
    private static partial Regex SabotageId();

    [GeneratedRegex(@"^(yes|partial|no) — \S")]
    private static partial Regex Verifiable();

    /// <summary>
    /// The class says what the outcome is worth. "forcing": writing the prediction changed the work,
    /// so it could not come true and is a checklist item. "evidence": the work was not altered by
    /// it, so it could have been wrong. Only evidence counts as a test of the prediction.
    /// </summary>
    [GeneratedRegex(@"^(right|wrong|partly) \((evidence|forcing)\) — \S")]
    private static partial Regex OutcomeLine();
}
