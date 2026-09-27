using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Raft.Architecture.Tests;

/// <summary>
/// P1-01: every item of docs/design/figure2-checklist.md (the paper's words) has exactly one row in
/// the traceability table of docs/design/node-interface.md, and every Raft.Core member a row names
/// exists in the built assembly — so the table cannot trace a rule to an interface that does not
/// exist. Vacuity risks: zero items parsed (guarded: the count must match the checklist's 52), and a
/// member that exists but cannot carry its rule (not detectable here; found when phase 3 uses it).
/// Sabotages: S-trace-1, S-trace-2.
/// </summary>
public sealed partial class TraceabilityTests
{
    private const int ChecklistItems = 52;

    private static string Doc(string name) => File.ReadAllText(Path.Combine(RepoRoot.Path, "docs", "design", name));

    private static List<string> ChecklistIds() =>
        ChecklistItem().Matches(Doc("figure2-checklist.md")).Select(m => m.Groups["id"].Value).ToList();

    private static List<(string Id, string[] Cells)> TableRows() =>
        Doc("node-interface.md").Split('\n')
            .Select(l => TableRow().Match(l))
            .Where(m => m.Success)
            .Select(m => (m.Groups["id"].Value, m.Groups["rest"].Value.Split('|').Select(c => c.Trim()).ToArray()))
            .ToList();

    [Fact]
    public void TheChecklistHasEveryTranscribedItem() =>
        Assert.Equal(ChecklistItems, ChecklistIds().Distinct().Count());

    [Fact]
    public void EveryChecklistItemHasExactlyOneRowAndNoRowIsInvented()
    {
        var ids = ChecklistIds();
        var rows = TableRows().Select(r => r.Id).ToList();

        var missing = ids.Except(rows).ToList();
        var invented = rows.Except(ids).ToList();
        var duplicated = rows.GroupBy(r => r).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(missing.Count == 0, "checklist items with no row: " + string.Join(", ", missing));
        Assert.True(invented.Count == 0, "rows for no checklist item: " + string.Join(", ", invented));
        Assert.True(duplicated.Count == 0, "items with more than one row: " + string.Join(", ", duplicated));
    }

    [Fact]
    public void EveryRowNamesASourceAndTheFaultsThatStressIt()
    {
        foreach (var (id, cells) in TableRows())
        {
            Assert.True(cells.Length >= 3, $"{id}: needs Carried by | Source | Stressed by");
            Assert.True(cells[1].Length > 0 && cells[2].Length > 0, $"{id}: empty Source or Stressed by");
        }
    }

    [Fact]
    public void EveryMemberARowNamesExistsInRaftCore()
    {
        var core = typeof(Raft.Core.INode).Assembly;
        var unresolved = new List<string>();
        var named = 0;
        foreach (var (id, cells) in TableRows())
        {
            var members = Member().Matches(cells[0]).Select(m => m.Groups["m"].Value).ToList();
            Assert.True(members.Count > 0, $"{id}: Carried by names no Raft.Core member");
            foreach (var m in members)
            {
                named++;
                if (!Resolves(core, m))
                {
                    unresolved.Add($"{id}: {m}");
                }
            }
        }

        Assert.True(named >= ChecklistItems, $"only {named} members named");
        Assert.True(unresolved.Count == 0, "members that do not exist in Raft.Core:\n  " + string.Join("\n  ", unresolved));
    }

    /// <summary>"Raft.Core.Type" or "Raft.Core.Type.Member".</summary>
    private static bool Resolves(Assembly core, string name)
    {
        if (core.GetType(name) is not null)
        {
            return true;
        }

        var dot = name.LastIndexOf('.');
        var type = core.GetType(name[..dot]);
        return type is not null && type.GetMember(name[(dot + 1)..], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Length > 0;
    }

    [GeneratedRegex(@"^- \*\*(?<id>(F2|F13|S6|S8)-\d{2})\*\*", RegexOptions.Multiline)]
    private static partial Regex ChecklistItem();

    [GeneratedRegex(@"^\| (?<id>(F2|F13|S6|S8)-\d{2}) \|(?<rest>.*)\|\s*$")]
    private static partial Regex TableRow();

    [GeneratedRegex(@"`(?<m>Raft\.Core\.[A-Za-z0-9_.]+)`")]
    private static partial Regex Member();
}
