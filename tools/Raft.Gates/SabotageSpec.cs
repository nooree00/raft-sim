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
/// for the harness's own controls, S-meta-*).
/// </summary>
internal sealed record SabotageSpec(string Id, IReadOnlyDictionary<string, string> Fields, string PatchPath)
{
    public static readonly string[] Kinds = ["test", "command"];
    public static readonly string[] Expectations = ["caught", "survived", "build-error", "not-compiled-in"];

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

        return Directory.GetDirectories(dir).Order(StringComparer.Ordinal)
            .Select(d => Load(Path.GetFileName(d), d, f)).OfType<SabotageSpec>().ToList();
    }

    private static SabotageSpec? Load(string id, string dir, Findings f)
    {
        var txt = Path.Combine(dir, "sabotage.txt");
        var patch = Path.Combine(dir, "patch.diff");
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

        var spec = new SabotageSpec(id, fields, patch);
        f.Require(spec.Get("description") is { Length: > 0 }, $"{id}: no description");
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
