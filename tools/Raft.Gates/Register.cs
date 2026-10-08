using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Raft.Gates;

/// <summary>
/// Spec §12: the register of deferred items, and "unimplemented throws and is listed".
///   - every row's promised phase exists in the spec's phase table;
///   - an open row promised to a completed phase fails;
///   - a done row names a test method that exists in the built test assemblies;
///   - a dropped row cites a commit that changed RAFT_PROJECT_SPEC.md;
///   - every method in the built src/ assemblies that constructs a NotImplementedException —
///     including lambdas and local functions, which compile into nested generated types — is
///     named in an open row.
///   - P12-01: docs/phases/status.md is checked against the reports both ways (see
///     <see cref="CompletionProblems"/>): the file is kept by hand, and the gate that reads it cannot
///     be the check that it is current.
/// Vacuity risks: an empty register (guarded: at least one row), a status file that never marks a
/// phase complete (guarded: an accepted report's phase must be marked complete, P12-01), a malformed
/// row silently skipped (guarded: every table row must parse).
/// </summary>
internal static partial class Register
{
    internal sealed record Row(string Item, string Phase, string Status, string Evidence);

    public static Findings Run(Repo repo, string[] args)
    {
        var f = new Findings();
        var phases = SpecPhases(repo);
        f.Require(phases.Count > 0, "no phases parsed from RAFT_PROJECT_SPEC.md §11");
        var completed = CompletedPhases(repo, f);
        foreach (var problem in CompletionProblems(ReportStatuses(repo), completed))
        {
            f.Fail(problem);
        }

        var rows = Rows(repo, f);
        f.Require(rows.Count > 0, "docs/register.md has no rows");

        var testMethods = new Lazy<HashSet<string>>(() => BuiltMethods(repo, "tests/", testsOnly: true));
        foreach (var r in rows)
        {
            f.Require(phases.Contains(r.Phase), $"'{r.Item}': promised to unknown phase '{r.Phase}'");
            switch (r.Status)
            {
                case "open":
                    f.Require(!completed.Contains(r.Phase), $"'{r.Item}': open, but promised to {r.Phase}, which is complete");
                    break;
                case "done":
                    f.Require(testMethods.Value.Contains(r.Evidence), $"'{r.Item}': done, but no test named '{r.Evidence}' in the built test assemblies (a method marked [Fact] or [Theory]; any other method is not a test)");
                    break;
                case "dropped":
                    var touched = repo.Git("show", "--name-only", "--format=", r.Evidence);
                    f.Require(touched.Ok && touched.StdOut.Split('\n').Contains("RAFT_PROJECT_SPEC.md"),
                        $"'{r.Item}': dropped, but '{r.Evidence}' is not a commit that changed RAFT_PROJECT_SPEC.md");
                    break;
                default:
                    f.Fail($"'{r.Item}': status '{r.Status}' is not open, done or dropped");
                    break;
            }
        }

        KnownLimits(repo, rows, f);

        var openText = string.Join("\n", rows.Where(r => r.Status == "open").Select(r => r.Item));
        var throwing = NotImplementedSites(repo, f);
        foreach (var site in throwing)
        {
            f.Require(openText.Contains(site, StringComparison.Ordinal), $"{site} throws NotImplementedException but no open register row names it");
        }

        f.Note($"{rows.Count} rows, completed phases [{string.Join(", ", completed.Order(StringComparer.Ordinal))}], {throwing.Count} NotImplementedException sites");
        return f;
    }

    /// <summary>
    /// P5-05: every recorded known limit (ci/known-limits.txt, spec §6) names one open register row it
    /// is promised to, and the phase report that approved it, which must exist and name the entry's
    /// id. An entry added outside a phase review has no report to point at. Sabotage S-reg-6. P6-12:
    /// the membership soak keeps its own entries (ci/known-limits-membership.txt), held to the same,
    /// with ids unique across both files. Sabotage S-reg-7.
    /// </summary>
    internal static void KnownLimits(Repo repo, IReadOnlyList<Row> rows, Findings f)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in KnownLimitFiles)
        {
            var path = repo.PathOf(file);
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var line in File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
            {
                var c = line.Split('|', StringSplitOptions.TrimEntries);
                if (c.Length != 6 || !KnownLimitId().IsMatch(c[0]) || !c[4].StartsWith("register: ", StringComparison.Ordinal) || !c[5].StartsWith("report: ", StringComparison.Ordinal))
                {
                    f.Fail($"{file}: malformed entry '{line}'");
                    continue;
                }

                var id = c[0];
                f.Require(ids.Add(id), $"{file}: {id} recorded twice");
                var row = c[4]["register: ".Length..];
                var open = rows.Count(r => r.Status == "open" && r.Item.StartsWith(row, StringComparison.Ordinal));
                f.Require(open == 1, $"{id}: cites the register row '{row}', which matches {open} open rows (exactly one required)");
                var report = c[5]["report: ".Length..];
                var reportPath = repo.PathOf(report);
                f.Require(report.StartsWith("docs/phases/", StringComparison.Ordinal) && report.EndsWith("/report.md", StringComparison.Ordinal) && File.Exists(reportPath)
                    && Regex.IsMatch(File.ReadAllText(reportPath), $@"\b{Regex.Escape(id)}\b"),
                    $"{id}: cites '{report}', which is not a phase report that names {id}");
            }
        }
    }

    /// <summary>The baseline soak's known limits, then the membership soak's (P6-12).</summary>
    private static readonly string[] KnownLimitFiles = ["ci/known-limits.txt", "ci/known-limits-membership.txt"];

    [GeneratedRegex(@"^KL-\d+$")]
    private static partial Regex KnownLimitId();

    internal static HashSet<string> SpecPhases(Repo repo) =>
        repo.ReadText("RAFT_PROJECT_SPEC.md").Split('\n')
            .Select(l => SpecPhaseRow().Match(l)).Where(m => m.Success)
            .Select(m => "P" + m.Groups["n"].Value).ToHashSet(StringComparer.Ordinal);

    internal static HashSet<string> CompletedPhases(Repo repo, Findings f)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var path = repo.PathOf("docs/phases/status.md");
        if (!File.Exists(path))
        {
            f.Fail("docs/phases/status.md missing");
            return set;
        }

        foreach (var line in File.ReadAllLines(path).Where(l => l.StartsWith("- P", StringComparison.Ordinal)))
        {
            var m = StatusLine().Match(line);
            if (!m.Success)
            {
                f.Fail($"docs/phases/status.md: unparseable line '{line}'");
                continue;
            }

            if (m.Groups["state"].Value == "complete")
            {
                set.Add(m.Groups["p"].Value);
            }
        }

        return set;
    }

    /// <summary>The phase whose accepted report may stay "in progress": its open row is the person's cold walk of the README, which no report can close.</summary>
    internal const string OpenByDesign = "P0";

    /// <summary>
    /// P12-01, phase 11's acceptance: docs/phases/status.md had not been kept since phase 7, so the
    /// check on open rows promised to completed phases could not fire for four phases, and it hid a
    /// row left open after its phase said it closed. Both ways: an accepted report whose phase is not
    /// marked complete (S-status-1), and a phase marked complete with no report, or a report that does
    /// not say it is accepted (S-status-2), which is how a phase is closed without evidence, and which
    /// <c>gates reports</c> would not see either, since it certifies the reports that exist.
    /// </summary>
    internal static List<string> CompletionProblems(IReadOnlyDictionary<string, string?> reportStatus, IReadOnlySet<string> completed)
    {
        var problems = new List<string>();
        foreach (var (phase, status) in reportStatus.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            if (status is null)
            {
                problems.Add($"docs/phases/{phase}/report.md has no '**Status:' line");
            }
            else if (Accepted(status) && !completed.Contains(phase) && phase != OpenByDesign)
            {
                problems.Add($"{phase}: its report says it is accepted, and docs/phases/status.md does not mark it complete");
            }
        }

        foreach (var phase in completed.Order(StringComparer.Ordinal))
        {
            if (!reportStatus.TryGetValue(phase, out var status))
            {
                problems.Add($"{phase} marked complete without docs/phases/{phase}/report.md");
            }
            else if (status is not null && !Accepted(status))
            {
                problems.Add($"{phase} marked complete, and its report does not say it is accepted (its status: '{status}')");
            }
        }

        return problems;
    }

    private static bool Accepted(string status) => status.StartsWith("accepted", StringComparison.Ordinal);

    /// <summary>Each phase report's status, the text after '**Status:' on its first such line, or null when it has none.</summary>
    internal static Dictionary<string, string?> ReportStatuses(Repo repo)
    {
        var statuses = new Dictionary<string, string?>(StringComparer.Ordinal);
        var dir = repo.PathOf("docs/phases");
        if (!Directory.Exists(dir))
        {
            return statuses;
        }

        foreach (var report in Directory.GetDirectories(dir, "P*").Select(d => Path.Combine(d, "report.md")).Where(File.Exists))
        {
            var m = File.ReadLines(report).Select(l => ReportStatusLine().Match(l)).FirstOrDefault(m => m.Success);
            statuses[Path.GetFileName(Path.GetDirectoryName(report)!)] = m?.Groups["s"].Value.Trim();
        }

        return statuses;
    }

    internal static List<Row> Rows(Repo repo, Findings f)
    {
        var rows = new List<Row>();
        var path = repo.PathOf("docs/register.md");
        if (!File.Exists(path))
        {
            f.Fail("docs/register.md missing");
            return rows;
        }

        var inTable = false;
        foreach (var line in File.ReadAllLines(path))
        {
            if (!line.StartsWith('|'))
            {
                inTable = false;
                continue;
            }

            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (!inTable)
            {
                inTable = true;
                if (!cells.SequenceEqual(["Item", "Promised", "Status", "Evidence"]))
                {
                    f.Fail($"docs/register.md: table header must be | Item | Promised | Status | Evidence |, got '{line}'");
                }

                continue;
            }

            if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':')))
            {
                continue;
            }

            if (cells.Length != 4 || cells[0].Length == 0)
            {
                f.Fail($"docs/register.md: malformed row '{line}'");
                continue;
            }

            rows.Add(new Row(cells[0], cells[1], cells[2], cells[3]));
        }

        return rows;
    }

    /// <summary>
    /// "Namespace.Type.Method" for every method defined in the built assemblies of projects under a
    /// prefix; with testsOnly, only methods marked [Fact] or [Theory]. P5-00's sweep: a done row
    /// citing a helper method passed, because any method definition resolved. Whether a test ran is
    /// `gates testcount`'s (every written test must execute). Sabotage S-reg-5.
    /// </summary>
    internal static HashSet<string> BuiltMethods(Repo repo, string prefix, bool testsOnly = false)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, md) in Assemblies(repo, prefix))
        {
            foreach (var h in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(h);
                if (testsOnly && !IsTest(md, m))
                {
                    continue;
                }

                set.Add(TypeName(md, m.GetDeclaringType()) + "." + md.GetString(m.Name));
            }
        }

        return set;
    }

    /// <summary>
    /// User-facing names ("Namespace.Type.Method") of methods whose IL constructs a
    /// NotImplementedException. Compiler-generated nested types and lambda/local-function names
    /// are mapped back to the enclosing type and the source method.
    /// </summary>
    internal static List<string> NotImplementedSites(Repo repo, Findings f)
    {
        var sites = new SortedSet<string>(StringComparer.Ordinal);
        var assemblies = Assemblies(repo, "src/");
        f.Require(assemblies.Count > 0, "no built src/ assemblies to scan — build first");
        foreach (var (pe, md) in assemblies)
        {
            foreach (var h in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(h);
                if (m.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes()!;
                if (ConstructsNotImplemented(md, il))
                {
                    sites.Add(UserFacingName(md, m));
                }
            }
        }

        return sites.ToList();
    }

    private static bool ConstructsNotImplemented(MetadataReader md, byte[] il)
    {
        for (var i = 0; i < il.Length;)
        {
            var op = il[i] == 0xFE ? OpCodeTable.TwoByte[il[i + 1]] : OpCodeTable.OneByte[il[i]];
            var size = il[i] == 0xFE ? 2 : 1;
            if (op == OpCodes.Newobj)
            {
                var token = BitConverter.ToInt32(il, i + size);
                var handle = MetadataTokens.EntityHandle(token);
                if (handle.Kind == HandleKind.MemberReference)
                {
                    var parent = md.GetMemberReference((MemberReferenceHandle)handle).Parent;
                    if (parent.Kind == HandleKind.TypeReference)
                    {
                        var tr = md.GetTypeReference((TypeReferenceHandle)parent);
                        if (md.GetString(tr.Namespace) == "System" && md.GetString(tr.Name) == "NotImplementedException")
                        {
                            return true;
                        }
                    }
                }
            }

            i += size + OpCodeTable.OperandSize(op, il, i + size);
        }

        return false;
    }

    private static string UserFacingName(MetadataReader md, MethodDefinition m)
    {
        var method = md.GetString(m.Name);
        var type = m.GetDeclaringType();
        // Walk out of compiler-generated nested types (<>c, <>c__DisplayClass0_0, <M>d__1).
        while (true)
        {
            var td = md.GetTypeDefinition(type);
            var name = md.GetString(td.Name);
            if (!name.StartsWith('<'))
            {
                break;
            }

            var owner = GeneratedOwner().Match(name);
            if (owner.Success && owner.Groups["m"].Value.Length > 0)
            {
                method = owner.Groups["m"].Value;
            }

            type = td.GetDeclaringType();
        }

        var source = GeneratedOwner().Match(method);
        if (source.Success && source.Groups["m"].Value.Length > 0)
        {
            method = source.Groups["m"].Value;
        }

        return TypeName(md, type) + "." + method;
    }

    private static string TypeName(MetadataReader md, TypeDefinitionHandle h)
    {
        var td = md.GetTypeDefinition(h);
        var declaring = td.GetDeclaringType();
        return declaring.IsNil
            ? (md.GetString(td.Namespace) is { Length: > 0 } ns ? ns + "." : "") + md.GetString(td.Name)
            : TypeName(md, declaring) + "+" + md.GetString(td.Name);
    }

    private static bool IsTest(MetadataReader md, MethodDefinition m)
    {
        foreach (var h in m.GetCustomAttributes())
        {
            var ctor = md.GetCustomAttribute(h).Constructor;
            if (ctor.Kind == HandleKind.MemberReference && md.GetMemberReference((MemberReferenceHandle)ctor).Parent is { Kind: HandleKind.TypeReference } parent)
            {
                var name = md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name);
                if (name is "FactAttribute" or "TheoryAttribute")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<(PEReader Pe, MetadataReader Md)> Assemblies(Repo repo, string prefix)
    {
        var list = new List<(PEReader, MetadataReader)>();
        foreach (var project in repo.ProjectFiles().Where(p => p.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var dll = repo.PathOf(Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug", "net10.0", name + ".dll"));
            if (File.Exists(dll))
            {
                var pe = new PEReader(new MemoryStream(File.ReadAllBytes(dll)));
                list.Add((pe, pe.GetMetadataReader()));
            }
        }

        return list;
    }

    [GeneratedRegex(@"^\|\s*(?<n>\d+)\s*\|")]
    private static partial Regex SpecPhaseRow();

    [GeneratedRegex(@"^- (?<p>P\d+): (?<state>in progress|complete)\b")]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"^\*\*Status: (?<s>[^*]+)")]
    private static partial Regex ReportStatusLine();

    [GeneratedRegex(@"^<(?<m>[^>]*)>")]
    private static partial Regex GeneratedOwner();
}

/// <summary>Opcode lookup and operand sizes, from System.Reflection.Emit.OpCodes.</summary>
internal static class OpCodeTable
{
    public static readonly OpCode[] OneByte = new OpCode[256];
    public static readonly OpCode[] TwoByte = new OpCode[256];

    static OpCodeTable()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)field.GetValue(null)!;
            var value = (ushort)op.Value;
            if (op.Size == 1)
            {
                OneByte[value] = op;
            }
            else
            {
                TwoByte[value & 0xFF] = op;
            }
        }
    }

    public static int OperandSize(OpCode op, byte[] il, int at) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, at),
        _ => 4,
    };
}
