using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Raft.Gates;

/// <summary>
/// The sabotage harness (spec §12, breakdown P0-09). Every entry under sabotage/ runs on every
/// push, in one worktree at HEAD built once, sequentially in a fixed path so incremental builds
/// stay valid. A result counts only if it is the expected one for the expected reason:
///   - a patch that does not apply, or a target test that did not run, is a harness error;
///   - a build failure is `build-error`, never "caught";
///   - a code patch whose build leaves every assembly byte-identical is `not-compiled-in`,
///     never "survived". Deterministic builds make the comparison exact; the dll embeds its PDB
///     id, which hashes the source text, so any edit to a compiled file registers — the guard
///     detects a patch the build never consumed, not a patch that happens to compile to the same
///     IL. Entries whose target reads files at run time declare `changes-assemblies: no`;
///   - after each revert the tree is rebuilt and must reproduce the baseline hashes exactly,
///     or every later result is suspect and the run stops.
/// Controls S-meta-1..3 expect survived, build-error and not-compiled-in respectively.
/// </summary>
internal static class Sabotage
{
    private sealed record Outcome(string Result, string Detail, TimeSpan Elapsed);

    public static Findings Run(Repo repo, string[] args)
    {
        var rest = args.ToList();
        var only = Options.Take(rest, "--only")?.Split(',', StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var ceiling = TimeSpan.FromMinutes(double.Parse(Options.Take(rest, "--ceiling-minutes") ?? "15", System.Globalization.CultureInfo.InvariantCulture));
        var summary = Options.Take(rest, "--summary") ?? Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        var f = new Findings();
        var clock = Stopwatch.StartNew();

        var status = repo.Git("status", "--porcelain");
        if (!status.Ok || status.StdOut.Length > 0)
        {
            f.Fail($"refusing to run on an uncommitted tree (sabotage runs from a committed tree):\n{status.StdOut}{status.StdErr}");
            return f;
        }

        var all = SabotageSpec.LoadAll(repo, f).Where(s => only is null || only.Contains(s.Id)).ToList();
        var hosted = all.Where(s => s.RunsOnHost).Select(s => s.Id).ToList();
        if (hosted.Count > 0)
        {
            f.Note($"deferred to the secrets job (runner: host): {string.Join(", ", hosted)}");
        }

        var specs = all.Where(s => !s.RunsOnHost).ToList();
        if (f.Failures.Count > 0 || specs.Count == 0)
        {
            f.Require(specs.Count > 0, "no sabotages selected");
            return f;
        }

        var wt = Path.Combine(Path.GetTempPath(), "raft-sabotage-worktree");
        repo.Git("worktree", "remove", "--force", wt);
        if (Directory.Exists(wt))
        {
            Directory.Delete(wt, recursive: true);
        }

        repo.Git("worktree", "prune");
        var add = repo.Git("worktree", "add", "--detach", wt, "HEAD");
        if (!add.Ok)
        {
            f.Fail($"worktree add failed: {add}");
            return f;
        }

        try
        {
            RunAll(repo, wt, specs, f);
        }
        finally
        {
            repo.Git("worktree", "remove", "--force", wt);
        }

        f.Note($"total {clock.Elapsed.TotalSeconds:F0}s for {specs.Count} sabotages (ceiling {ceiling.TotalMinutes:F0} min)");
        f.Require(clock.Elapsed <= ceiling, $"harness took {clock.Elapsed.TotalMinutes:F1} min, over the {ceiling.TotalMinutes:F0}-minute ceiling — the manifest has outgrown the design; decide, do not run it less often");
        if (summary is not null)
        {
            File.AppendAllText(summary, $"\nSabotage harness: {specs.Count} sabotages in {clock.Elapsed.TotalSeconds:F0}s\n");
        }

        return f;
    }

    private static void RunAll(Repo repo, string wt, IReadOnlyList<SabotageSpec> specs, Findings f)
    {
        var env = new Dictionary<string, string> { ["GATES"] = typeof(Sabotage).Assembly.Location };
        var projects = Repo.Locate(wt).ProjectFiles();

        var build = Build(wt);
        if (!build.Ok)
        {
            f.Fail($"baseline build failed:\n{Tail(build)}");
            return;
        }

        var baseline = Hashes(wt, projects);
        f.Note($"baseline: {baseline.Count} assemblies hashed");
        f.Require(baseline.Count == projects.Count, $"hashed {baseline.Count} assemblies for {projects.Count} projects");

        // Every target must pass unpatched, or a red result says nothing about the patch.
        var baselineFailures = BaselineChecks(wt, specs, env);
        foreach (var bf in baselineFailures)
        {
            f.Fail(bf);
        }

        if (baselineFailures.Count > 0)
        {
            return;
        }

        foreach (var spec in specs)
        {
            var outcome = RunOne(wt, spec, baseline, projects, env);
            var ok = outcome.Result == spec.Expect;
            var line = $"{spec.Id,-11} {outcome.Result,-15} expected {spec.Expect,-15} {outcome.Elapsed.TotalSeconds,5:F1}s  {outcome.Detail}";
            if (ok)
            {
                f.Note(line);
            }
            else
            {
                f.Fail(line);
            }

            // Revert, and prove the tree is back: rebuild if anything changed, then exact hashes.
            Proc.Run("git", wt, "checkout", "-q", "--", ".");
            Proc.Run("git", wt, "clean", "-fdq");
            if (!SameHashes(Hashes(wt, projects), baseline))
            {
                var rebuild = Build(wt);
                if (!rebuild.Ok || !SameHashes(Hashes(wt, projects), baseline))
                {
                    f.Fail($"{spec.Id}: after revert the tree does not reproduce the baseline assemblies — stopping, later results would be suspect\n{Tail(rebuild)}");
                    return;
                }
            }
        }
    }

    private static List<string> BaselineChecks(string wt, IReadOnlyList<SabotageSpec> specs, IReadOnlyDictionary<string, string> env)
    {
        var failures = new List<string>();

        foreach (var group in specs.Where(s => s.Kind == "test").GroupBy(s => s.Get("project")!))
        {
            var results = RunTests(wt, group.Key);
            foreach (var spec in group)
            {
                var mine = Matching(results, spec.Get("target")!);
                if (mine.Count == 0)
                {
                    failures.Add($"{spec.Id}: target {spec.Get("target")} did not run in the baseline");
                }
                else if (mine.Any(r => r.Outcome != "Passed"))
                {
                    failures.Add($"{spec.Id}: target {spec.Get("target")} does not pass unpatched");
                }
            }
        }

        foreach (var group in specs.Where(s => s.Kind == "command").GroupBy(s => s.Get("baseline") ?? s.Get("command")!, StringComparer.Ordinal))
        {
            var r = Bash(wt, group.Key, env);
            if (!r.Ok)
            {
                failures.Add($"{string.Join(", ", group.Select(s => s.Id))}: baseline command fails unpatched: {group.Key}\n{Tail(r)}");
            }
        }

        return failures;
    }

    private static Outcome RunOne(string wt, SabotageSpec spec, IReadOnlyDictionary<string, string> baseline, IReadOnlyList<string> projects, IReadOnlyDictionary<string, string> env)
    {
        var clock = Stopwatch.StartNew();
        Outcome Done(string result, string detail) => new(result, detail, clock.Elapsed);

        if (new FileInfo(spec.PatchPath).Length > 0)
        {
            var apply = Proc.Run("git", wt, "apply", "--whitespace=nowarn", spec.PatchPath);
            if (!apply.Ok)
            {
                return Done("apply-failed", apply.StdErr.Trim());
            }
        }
        else if (spec.Get("baseline") is null)
        {
            return Done("empty-patch", "an empty patch needs a separate baseline command, or nothing is sabotaged");
        }

        if (spec.Build)
        {
            var build = Build(wt, forceRestore: spec.ForceRestore);
            if (!build.Ok)
            {
                return Done("build-error", FirstError(build));
            }

            if (spec.ChangesAssemblies && SameHashes(Hashes(wt, projects), baseline))
            {
                return Done("not-compiled-in", "every assembly is byte-identical to the baseline");
            }
        }

        if (spec.Kind == "test")
        {
            var results = RunTests(wt, spec.Get("project")!);
            var mine = Matching(results, spec.Get("target")!);
            if (mine.Count == 0)
            {
                return Done("target-not-run", spec.Get("target")!);
            }

            var neighbours = results.Where(r => r.Outcome == "Failed" && !mine.Contains(r)).Select(r => r.Name).ToList();
            var detail = neighbours.Count == 0 ? "" : $"neighbours also red: {string.Join(", ", neighbours.Take(3))}{(neighbours.Count > 3 ? $" (+{neighbours.Count - 3})" : "")}";
            return Done(mine.Any(r => r.Outcome == "Failed") ? "caught" : "survived", detail);
        }

        var run = Bash(wt, spec.Get("command")!, env);
        var message = spec.Get("message");
        var output = run.StdOut + run.StdErr;
        if (run.Ok)
        {
            return Done("survived", "command exited 0");
        }

        return message is not null && output.Contains(message, StringComparison.Ordinal)
            ? Done("caught", $"exit {run.ExitCode}, message found")
            : Done("wrong-reason", $"exit {run.ExitCode} without '{message}': {Tail(run, 3)}");
    }

    private sealed record TestResult(string Name, string Outcome);

    private static List<TestResult> RunTests(string wt, string project)
    {
        var dir = Path.Combine(wt, "TestResults", "sabotage");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Proc.Run("dotnet", wt, "test", "--project", project, "--no-build", "--report-xunit-trx", "--results-directory", dir);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.trx").SelectMany(t => XDocument.Load(t).Descendants(ns + "UnitTestResult"))
                .Select(e => new TestResult((string?)e.Attribute("testName") ?? "", (string?)e.Attribute("outcome") ?? "")).ToList()
            : [];
    }

    private static List<TestResult> Matching(IEnumerable<TestResult> results, string target) =>
        results.Where(r => r.Name == target || r.Name.StartsWith(target + "(", StringComparison.Ordinal)).ToList();

    private static ProcessResult Build(string wt, bool forceRestore = false)
    {
        if (!forceRestore)
        {
            return Proc.Run("dotnet", wt, "build", "Raft.slnx", "-nologo", "-v:q", "-warnaserror");
        }

        var restore = Proc.Run("dotnet", wt, "restore", "Raft.slnx", "--force-evaluate", "-p:RestoreLockedMode=false", "-nologo", "-v:q");
        return restore.Ok
            ? Proc.Run("dotnet", wt, "build", "Raft.slnx", "--no-restore", "-nologo", "-v:q", "-warnaserror")
            : restore;
    }

    private static ProcessResult Bash(string wt, string command, IReadOnlyDictionary<string, string> env) =>
        Proc.RunWithEnv("bash", wt, env, "-c", command);

    private static Dictionary<string, string> Hashes(string wt, IReadOnlyList<string> projects)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in projects)
        {
            var name = Path.GetFileNameWithoutExtension(p);
            var dll = Path.Combine(wt, Path.GetDirectoryName(p)!, "bin", "Debug", "net10.0", name + ".dll");
            if (File.Exists(dll))
            {
                map[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)));
            }
        }

        return map;
    }

    private static bool SameHashes(Dictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string FirstError(ProcessResult r) =>
        (r.StdOut + r.StdErr).Split('\n').FirstOrDefault(l => l.Contains(" error ", StringComparison.Ordinal))?.Trim() ?? Tail(r, 2);

    private static string Tail(ProcessResult r, int lines = 12) =>
        string.Join('\n', (r.StdOut + r.StdErr).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines));
}
