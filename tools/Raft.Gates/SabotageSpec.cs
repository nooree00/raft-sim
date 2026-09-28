using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Raft.Gates;

/// <summary>
/// One sabotage/&lt;id&gt;/ entry: a patch and a sabotage.txt of "key: value" lines.
/// kind: test — `project` and `target` (a test method's full name) must be recorded Failed.
/// kind: command — `command` must exit non-zero with `message` in its output; `baseline`
/// (default: the command) must pass unpatched. `build: yes|no` says whether to build after
/// patching. `expect: caught | survived | build-error | not-compiled-in` (all but the first only
/// for the harness's own controls, S-meta-* and S-audit-*).
/// Since P3-01 every entry names its `mechanism:`, the behaviour its patch breaks. A test entry may
/// state a `reason:`, text its target's failure message must contain, and may carry `control.diff`,
/// the patch with its mechanism removed, under which the target must stay green (`control-expect:
/// red` only for the harness's own control). An entry whose target is on the broad-target list
/// (ci/broad-targets.txt: tests any behaviour change turns red) needs a reason or a control.
/// </summary>
internal sealed record SabotageSpec(string Id, IReadOnlyDictionary<string, string> Fields, string PatchPath, string? ControlPath)
{
    public const string BroadTargetsFile = "ci/broad-targets.txt";
    public static readonly string[] Kinds = ["test", "command"];
    public static readonly string[] Expectations = ["caught", "survived", "build-error", "not-compiled-in", "wrong-reason"];

    public string ControlExpect => Fields.GetValueOrDefault("control-expect", "green");

    public string Kind => Fields.GetValueOrDefault("kind", "");

    public string Expect => Fields.GetValueOrDefault("expect", "");

    public bool Build => Fields.GetValueOrDefault("build", "yes") == "yes";

    /// <summary>
    /// "no" for a patch whose target reads files at run time (a csproj, the solution) and so leaves
    /// every assembly unchanged by design; the harness then skips its not-compiled-in guard.
    /// </summary>
    /// <summary>"host" for entries that need the runner host (docker); run by scripts/host-sabotages.sh.</summary>
    public bool RunsOnHost => Fields.GetValueOrDefault("runner", "container") == "host";

    /// <summary>
    /// "force" for a patch that changes the dependency graph (a project or package reference): the
    /// harness re-evaluates restore itself, with locked mode off for this build only, so the patch
    /// need not carry lock-file changes — which went stale with every new project.
    /// </summary>
    public bool ForceRestore => Fields.GetValueOrDefault("restore", "locked") == "force";

    public bool ChangesAssemblies => Fields.GetValueOrDefault("changes-assemblies", "yes") == "yes";

    public string? Get(string key) => Fields.TryGetValue(key, out var v) ? v : null;

    public static IReadOnlyList<SabotageSpec> LoadAll(Repo repo, Findings f)
    {
        var dir = repo.PathOf("sabotage");
        if (!Directory.Exists(dir))
        {
            f.Fail("no sabotage/ directory");
            return [];
        }

        var broadPath = repo.PathOf(BroadTargetsFile);
        var broad = File.Exists(broadPath) ? ParseBroadTargets(File.ReadAllText(broadPath)) : [];
        f.Require(broad.Count > 0, $"{BroadTargetsFile} is missing or lists nothing: every target would count as narrow");
        return Directory.GetDirectories(dir).Order(StringComparer.Ordinal)
            .Select(d => Load(Path.GetFileName(d), d, broad, f)).OfType<SabotageSpec>().ToList();
    }

    /// <summary>One test name (or name prefix) per line; '#' starts a comment.</summary>
    public static IReadOnlyList<string> ParseBroadTargets(string text) =>
        text.Split('\n').Select(l => (l.IndexOf('#', StringComparison.Ordinal) is var i and >= 0 ? l[..i] : l).Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>The files a unified diff changes (its "+++ b/" and "--- a/" paths).</summary>
    public static IReadOnlySet<string> ChangedFiles(string diff) =>
        diff.Split('\n').Where(l => l.StartsWith("+++ b/", StringComparison.Ordinal) || l.StartsWith("--- a/", StringComparison.Ordinal))
            .Select(l => l[6..].Trim()).ToHashSet(StringComparer.Ordinal);

    internal static SabotageSpec? Load(string id, string dir, IReadOnlyList<string> broad, Findings f)
    {
        var txt = Path.Combine(dir, "sabotage.txt");
        var patch = Path.Combine(dir, "patch.diff");
        var control = Path.Combine(dir, "control.diff");
        if (!File.Exists(txt) || !File.Exists(patch))
        {
            f.Fail($"{id}: needs sabotage.txt and patch.diff");
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(txt).Where(l => l.Trim().Length > 0))
        {
            var i = line.IndexOf(": ", StringComparison.Ordinal);
            if (i <= 0 || !fields.TryAdd(line[..i], line[(i + 2)..].Trim()))
            {
                f.Fail($"{id}: malformed or duplicate line '{line}'");
            }
        }

        var spec = new SabotageSpec(id, fields, patch, File.Exists(control) ? control : null);
        f.Require(spec.Get("description") is { Length: > 0 }, $"{id}: no description");
        f.Require(spec.Get("mechanism") is { Length: > 0 }, $"{id}: no mechanism (the behaviour the patch breaks; P3-01)");
        f.Require(spec.Get("reason") is null || spec.Kind == "test", $"{id}: a reason is checked against a target's failure message, so only a test entry has one");
        f.Require(spec.Get("control-expect") is null or "green" or "red", $"{id}: control-expect must be green or red");
        if (spec.ControlPath is { } c)
        {
            var controlText = File.ReadAllText(c);
            var patchText = File.ReadAllText(patch);
            f.Require(spec.Kind == "test", $"{id}: a control is run against a target test, so only a test entry has one");
            f.Require(controlText.Trim().Length > 0, $"{id}: an empty control controls for nothing");
            f.Require(controlText != patchText, $"{id}: the control is the patch itself");
            f.Require(ChangedFiles(controlText).Overlaps(ChangedFiles(patchText)), $"{id}: the control shares no file with the patch, so it cannot keep the patch's incidental changes");
        }

        var target = spec.Get("target");
        if (target is not null && broad.Any(b => target.StartsWith(b, StringComparison.Ordinal)))
        {
            f.Require(spec.Get("reason") is not null || spec.ControlPath is not null,
                $"{id}: target {target} is a broad test ({BroadTargetsFile}); it goes red at almost any change, so the entry needs a reason or a control");
        }
        f.Require(Kinds.Contains(spec.Kind), $"{id}: kind '{spec.Kind}' not one of {string.Join("/", Kinds)}");
        f.Require(Expectations.Contains(spec.Expect), $"{id}: expect '{spec.Expect}' not one of {string.Join("/", Expectations)}");
        f.Require(spec.Get("build") is null or "yes" or "no", $"{id}: build must be yes or no");
        f.Require(spec.Get("changes-assemblies") is null or "yes" or "no", $"{id}: changes-assemblies must be yes or no");
        f.Require(spec.Get("runner") is null or "container" or "host", $"{id}: runner must be container or host");
        f.Require(spec.Get("restore") is null or "locked" or "force", $"{id}: restore must be locked or force");
        f.Require(!spec.RunsOnHost || spec.Kind == "command", $"{id}: a host entry is a command");
        if (spec.Kind == "test")
        {
            f.Require(spec.Get("project") is { Length: > 0 } && spec.Get("target") is { Length: > 0 }, $"{id}: kind test needs project and target");
            f.Require(spec.Build, $"{id}: kind test needs build: yes");
        }
        else if (spec.Kind == "command")
        {
            f.Require(spec.Get("command") is { Length: > 0 }, $"{id}: kind command needs command");
            f.Require(spec.Expect != "caught" || spec.Get("message") is { Length: > 0 }, $"{id}: an expected catch needs a message, or any non-zero exit would count");
        }

        return spec;
    }
}
