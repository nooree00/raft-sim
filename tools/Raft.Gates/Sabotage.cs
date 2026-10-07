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
/// push. Entries are dealt round-robin to up to four workers; each worker has its own worktree at
/// HEAD, at a fixed path so incremental builds stay valid, built once, and runs its share
/// sequentially. (Phase 1: one worker reached 781 s of the 900 s ceiling at 68 entries; the ceiling
/// forced this change rather than running the harness less often.) Since P2-01 the manifest is
/// split into shards (<see cref="ShardPlan"/>), one CI job each, and the 15-minute ceiling applies
/// per shard: the shard count grows with the manifest instead of the ceiling. A result counts only if it is the expected one for the expected reason:
///   - a patch that does not apply, or a target test that did not run, is a harness error;
///   - a build failure is `build-error`, never "caught";
///   - a code patch whose build leaves every assembly byte-identical is `not-compiled-in`,
///     never "survived". Deterministic builds make the comparison exact; the dll embeds its PDB
///     id, which hashes the source text, so any edit to a compiled file registers — the guard
///     detects a patch the build never consumed, not a patch that happens to compile to the same
///     IL. Entries whose target reads files at run time declare `changes-assemblies: no`;
///   - after each revert the tree is rebuilt and must reproduce the baseline hashes exactly,
///     or every later result is suspect and the run stops;
///   - a test entry with a `reason:` is caught only if its target fails with a message containing
///     it, otherwise `wrong-reason`: red is not enough, it must be red for the stated reason (P3-01);
///   - a test entry with a `control.diff` (the patch with its mechanism removed, its incidental
///     changes kept) is run a second time with the control applied, and its target must stay green:
///     a target the control turns red is red for something other than the mechanism.
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
        // (With --touched, the accepted plan's ceiling replaces it below.)
        var summary = Options.Take(rest, "--summary") ?? Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        var workersOption = Options.Take(rest, "--workers");
        var shardOption = Options.Take(rest, "--shard");
        var baselineFirst = rest.Remove("--baseline-first");
        var touched = Options.Take(rest, "--touched");
        var acceptEstimate = rest.Remove("--accept-estimate");
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
        if (touched is not null)
        {
            // P9-00: the entries the push makes worth running, decided and printed before anything runs.
            var diff = repo.Git("diff", "--name-only", touched + "..HEAD");
            if (!diff.Ok)
            {
                f.Fail($"--touched {touched}: git diff failed: {diff.StdErr}");
                return f;
            }

            var changed = diff.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            var (rate, threshold) = Touched.ParseConfig(File.ReadAllText(repo.PathOf(Touched.ConfigFile)));
            var chosen = Touched.Select(specs.Select(s => new TouchedEntry(s.Id, File.ReadAllText(s.PatchPath), s.ControlPath is null ? null : File.ReadAllText(s.ControlPath))), changed).ToHashSet(StringComparer.Ordinal);
            var plan = Touched.PlanFor(chosen.Count, specs.Count, rate, threshold, acceptEstimate);
            Console.WriteLine($"[sabotage] {changed.Count} files changed in {touched}..HEAD; {plan.Line}");
            Console.Out.Flush();
            f.Note(plan.Line);
            if (!plan.Run)
            {
                f.Require(chosen.Count == 0, plan.Line);
                return f;
            }

            specs = specs.Where(s => chosen.Contains(s.Id)).ToList();
            ceiling = plan.Ceiling;
        }
        if (shardOption is not null && only is null)
        {
            // The plan is recomputed here from the committed manifest: a shard job started with a
            // stale count (entries added since the plan was made) fails rather than skipping entries.
            var size = ShardPlan.ParseSize(File.ReadAllText(repo.PathOf(ShardPlan.SizeFile)));
            var mine = ShardPlan.Select(specs.Select(PlanEntryOf).ToList(), shardOption, size, LoadCosts(repo), f);
            if (mine is null)
            {
                return f;
            }

            var chosen = mine.ToHashSet(StringComparer.Ordinal);
            specs = specs.Where(s => chosen.Contains(s.Id)).ToList();
            f.Note($"shard {shardOption}: {specs.Count} of the manifest's harness entries");
        }

        if (f.Failures.Count > 0 || specs.Count == 0)
        {
            f.Require(specs.Count > 0, "no sabotages selected");
            return f;
        }

        // Parallel workers, each with its own worktree at a fixed path (so incremental builds stay
        // valid) and its own baseline. Entries are dealt round-robin in id order: deterministic.
        var workers = Math.Clamp(workersOption is null ? Environment.ProcessorCount : int.Parse(workersOption, System.Globalization.CultureInfo.InvariantCulture), 1, 4);
        workers = Math.Min(workers, specs.Count);
        // P10-00: the longest entry first to the worker that would finish soonest, its baseline build
        // and units counted; the plan models the same deal, so a shard's modelled time is this run's.
        var costs = LoadCosts(repo);
        var byId = specs.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var shares = ShardPlan.WorkerShares(specs.Select(PlanEntryOf).ToList(), workers, costs).Select(share => share.Select(e => byId[e.Id]).ToList()).ToList();
        var timings = new System.Collections.Concurrent.ConcurrentDictionary<string, double>(StringComparer.Ordinal);

        // Baseline checks once per shard (P4-12): each target project, and each command group, is
        // checked by one worker, against all of the shard's targets in it.
        var units = BaselineUnits(specs);
        var uncovered = Uncovered(specs, units);
        if (uncovered.Count > 0)
        {
            f.Fail($"no baseline check covers {string.Join(", ", uncovered)}: their targets would never be shown to pass unpatched");
            return f;
        }

        var baselineShares = AssignBaselines(units.Select(u => u.Key).ToList(), workers);
        var trees = Enumerable.Range(0, workers).Select(k => Path.Combine(Path.GetTempPath(), $"raft-sabotage-worktree-{k}")).ToList();
        foreach (var wt in trees)
        {
            repo.Git("worktree", "remove", "--force", wt);
            if (Directory.Exists(wt))
            {
                Directory.Delete(wt, recursive: true);
            }
        }

        repo.Git("worktree", "prune");
        foreach (var wt in trees)
        {
            var add = repo.Git("worktree", "add", "--detach", wt, "HEAD");
            if (!add.Ok)
            {
                f.Fail($"worktree add failed: {add}");
                return f;
            }
        }

        var worktrees = clock.Elapsed;
        var results = new Findings[workers];
        var ready = new TimeSpan[workers];

        // P7-12's measurement: with --baseline-first no worker starts an entry until every worker's
        // baseline checks are done, so no baseline check runs beside other workers' entries.
        using var barrier = baselineFirst ? new System.Threading.Barrier(workers) : null;
        try
        {
            System.Threading.Tasks.Parallel.For(0, workers, k =>
            {
                results[k] = new Findings();
                var mineUnits = baselineShares[k].Select(key => units[key]).ToList();
                ready[k] = RunAll(trees[k], shares[k], mineUnits, results[k], clock, barrier, timings);
            });
        }
        finally
        {
            foreach (var wt in trees)
            {
                repo.Git("worktree", "remove", "--force", wt);
            }
        }

        foreach (var r in results)
        {
            foreach (var n in r.Notes.Order(StringComparer.Ordinal))
            {
                f.Note(n);
            }

            foreach (var e in r.Failures)
            {
                f.Fail(e);
            }
        }

        // The reviewer's condition on recorded costs (P10-00): in a shard run, every entry's time
        // beside its recorded cost, and a failure when one is off by more than the stale factor.
        if (shardOption is not null)
        {
            CheckCosts(timings, costs, f);
        }

        // Fixed cost: worktrees, baseline builds and baseline checks, until the slowest worker is
        // ready to run its first entry (P2-01's prediction is about this number).
        f.Note($"fixed cost {ready.Max().TotalSeconds:F0}s (worktrees, baseline builds, baseline checks; slowest worker), of which setup (manifest, worktrees) {worktrees.TotalSeconds:F0}s");
        f.Note($"total {clock.Elapsed.TotalSeconds:F0}s for {specs.Count} sabotages on {workers} workers (ceiling {ceiling.TotalMinutes:F0} min)");
        f.Require(clock.Elapsed <= ceiling, $"harness took {clock.Elapsed.TotalMinutes:F1} min, over the {ceiling.TotalMinutes:F0}-minute ceiling — the manifest has outgrown the design; decide, do not run it less often");
        if (summary is not null)
        {
            File.AppendAllText(summary, $"\nSabotage harness: {specs.Count} sabotages in {clock.Elapsed.TotalSeconds:F0}s on {workers} workers\n");
        }

        return f;
    }

    /// <summary>Runs one worker's share; returns the harness clock when the worker was ready for its first entry.</summary>
    private static TimeSpan RunAll(string wt, IReadOnlyList<SabotageSpec> specs, IReadOnlyList<IReadOnlyList<SabotageSpec>> baselineUnits, Findings f, Stopwatch clock, System.Threading.Barrier? barrier, System.Collections.Concurrent.ConcurrentDictionary<string, double> entryTimes)
    {
        var env = new Dictionary<string, string> { ["GATES"] = typeof(Sabotage).Assembly.Location };
        var projects = Repo.Locate(wt).ProjectFiles();

        var started = clock.Elapsed;
        var build = Build(wt);
        var built = clock.Elapsed;
        if (!build.Ok)
        {
            f.Fail($"baseline build failed in {wt}:\n{Tail(build)}");
            barrier?.RemoveParticipant();
            return clock.Elapsed;
        }

        var baseline = Hashes(wt, projects);
        f.Require(baseline.Count == projects.Count, $"hashed {baseline.Count} assemblies for {projects.Count} projects");

        // Every target must pass unpatched, or a red result says nothing about the patch.
        var timings = new List<string>();
        var baselineFailures = BaselineChecks(wt, baselineUnits.SelectMany(u => u).ToList(), env, timings);
        foreach (var bf in baselineFailures)
        {
            f.Fail(bf);
        }

        // The fixed cost's split, per worker (P6 row "Harness fixed cost", first step; P5-07).
        var ready = clock.Elapsed;
        f.Note($"fixed cost split, {Path.GetFileName(wt)}: ready at {ready.TotalSeconds:F0}s; baseline build {(built - started).TotalSeconds:F0}s; baseline checks {(ready - built).TotalSeconds:F0}s ({(timings.Count == 0 ? "none" : string.Join("; ", timings))})");
        if (baselineFailures.Count > 0)
        {
            barrier?.RemoveParticipant();
            return ready;
        }

        if (barrier is not null)
        {
            barrier.SignalAndWait();
            f.Note($"fixed cost split, {Path.GetFileName(wt)}: entries start at {clock.Elapsed.TotalSeconds:F0}s, after every worker's baseline checks");
        }

        foreach (var spec in specs)
        {
            var outcome = RunOne(wt, spec, baseline, projects, env);
            entryTimes.AddOrUpdate(spec.Id, outcome.Elapsed.TotalSeconds, (_, t) => t + outcome.Elapsed.TotalSeconds);
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

            if (!Revert(wt, spec, baseline, projects, f))
            {
                return ready;
            }

            if (spec.ControlPath is { } control)
            {
                var c = RunControl(wt, spec, control, baseline, projects);
                entryTimes.AddOrUpdate(spec.Id, c.Elapsed.TotalSeconds, (_, t) => t + c.Elapsed.TotalSeconds);
                var line2 = $"{spec.Id,-11} control {c.Result,-7} expected {spec.ControlExpect,-7} {c.Elapsed.TotalSeconds,5:F1}s  {c.Detail}";
                if (c.Result == spec.ControlExpect)
                {
                    f.Note(line2);
                }
                else
                {
                    f.Fail(line2);
                }

                if (!Revert(wt, spec, baseline, projects, f))
                {
                    return ready;
                }
            }
        }

        return ready;
    }

    /// <summary>Revert, and prove the tree is back: rebuild if anything changed, then exact hashes.</summary>
    private static bool Revert(string wt, SabotageSpec spec, IReadOnlyDictionary<string, string> baseline, IReadOnlyList<string> projects, Findings f)
    {
        Proc.Run("git", wt, "checkout", "-q", "--", ".");
        Proc.Run("git", wt, "clean", "-fdq");
        if (SameHashes(Hashes(wt, projects), baseline))
        {
            return true;
        }

        var rebuild = Build(wt, BuildScope(spec));
        if (rebuild.Ok && SameHashes(Hashes(wt, projects), baseline))
        {
            return true;
        }

        f.Fail($"{spec.Id}: after revert the tree does not reproduce the baseline assemblies — stopping this worker, later results would be suspect\n{Tail(rebuild)}");
        return false;
    }

    /// <summary>The patch with its mechanism removed: the target must stay green ("green"), or the entry measures something else.</summary>
    private static Outcome RunControl(string wt, SabotageSpec spec, string control, IReadOnlyDictionary<string, string> baseline, IReadOnlyList<string> projects)
    {
        var clock = Stopwatch.StartNew();
        Outcome Done(string result, string detail) => new(result, detail, clock.Elapsed);

        var apply = Proc.Run("git", wt, "apply", "--whitespace=nowarn", control);
        if (!apply.Ok)
        {
            return Done("apply-failed", apply.StdErr.Trim());
        }

        var build = Build(wt, BuildScope(spec), forceRestore: spec.ForceRestore);
        if (!build.Ok)
        {
            return Done("build-error", FirstError(build));
        }

        if (spec.ChangesAssemblies && SameHashes(Hashes(wt, projects), baseline))
        {
            return Done("not-compiled-in", "the control leaves every assembly byte-identical: it controls for nothing");
        }

        var mine = Matching(RunTests(wt, spec.Get("project")!), spec.Get("target")!);
        if (mine.Count == 0)
        {
            return Done("target-not-run", spec.Get("target")!);
        }

        var failed = mine.Where(r => r.Outcome == "Failed").ToList();
        return failed.Count == 0
            ? Done("green", "the target passes with the mechanism removed")
            : Done("red", "the target fails with the mechanism removed: " + Short(string.Join(" | ", failed.Select(r => r.Message))));
    }

    private static List<string> BaselineChecks(string wt, IReadOnlyList<SabotageSpec> specs, IReadOnlyDictionary<string, string> env, List<string> timings)
    {
        var failures = new List<string>();
        var unit = Stopwatch.StartNew();

        foreach (var group in specs.Where(s => s.Kind == "test").GroupBy(s => s.Get("project")!))
        {
            unit.Restart();
            var results = RunTests(wt, group.Key, group.Select(s => TargetMethod(s.Get("target")!)).Distinct(StringComparer.Ordinal).ToList());
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

            timings.Add($"{Path.GetFileName(group.Key.TrimEnd('/'))} {unit.Elapsed.TotalSeconds:F0}s");
        }

        foreach (var group in specs.Where(s => s.Kind == "command").GroupBy(s => s.Get("baseline") ?? s.Get("command")!, StringComparer.Ordinal))
        {
            unit.Restart();
            var r = Bash(wt, group.Key, env);
            if (!r.Ok)
            {
                failures.Add($"{string.Join(", ", group.Select(s => s.Id))}: baseline command fails unpatched: {group.Key}\n{Tail(r)}");
            }

            timings.Add($"'{group.Key}' {unit.Elapsed.TotalSeconds:F0}s");
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
            var build = Build(wt, BuildScope(spec), forceRestore: spec.ForceRestore);
            if (!build.Ok)
            {
                return Done("build-error", FirstError(build));
            }

            if (spec.ChangesAssemblies && SameHashes(Hashes(wt, projects), baseline))
            {
                return Done("not-compiled-in", spec.Kind == "test"
                    ? $"every assembly is byte-identical to the baseline after building {spec.Get("project")}: the patch is outside its target's build"
                    : "every assembly is byte-identical to the baseline");
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
            var failed = mine.Where(r => r.Outcome == "Failed").ToList();
            if (failed.Count == 0)
            {
                return Done("survived", detail);
            }

            // Red is not enough: the target's own failure message is printed, and a stated reason must be in it.
            var why = string.Join(" | ", failed.Select(r => r.Message));
            if (spec.Get("reason") is { } reason && !why.Contains(reason, StringComparison.Ordinal))
            {
                return Done("wrong-reason", $"target failed without '{reason}': {Short(why)}");
            }

            return Done("caught", $"reason: {Short(why)}{(detail.Length == 0 ? "" : "; " + detail)}");
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

    private sealed record TestResult(string Name, string Outcome, string Message);

    private static List<TestResult> RunTests(string wt, string project, IReadOnlyList<string>? only = null)
    {
        var dir = Path.Combine(wt, "TestResults", "sabotage");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        var args = new List<string> { "test", "--project", project, "--no-build", "--report-xunit-trx", "--results-directory", dir };
        foreach (var method in only ?? [])
        {
            args.Add("--filter-method");
            args.Add(method);
        }

        Proc.Run("dotnet", wt, [.. args]);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.trx").SelectMany(t => XDocument.Load(t).Descendants(ns + "UnitTestResult"))
                .Select(e => new TestResult((string?)e.Attribute("testName") ?? "", (string?)e.Attribute("outcome") ?? "",
                    e.Element(ns + "Output")?.Element(ns + "ErrorInfo")?.Element(ns + "Message")?.Value ?? "")).ToList()
            : [];
    }

    private static List<TestResult> Matching(IEnumerable<TestResult> results, string target) =>
        results.Where(r => r.Name == target || r.Name.StartsWith(target + "(", StringComparison.Ordinal)).ToList();

    /// <summary>
    /// What an entry builds (P4-12): a test entry, its target project and that project's references;
    /// anything else, the solution. A patch outside a test entry's build leaves every assembly as it
    /// was, which the not-compiled-in guard reports.
    /// </summary>
    internal static string BuildScope(SabotageSpec spec) => spec.Kind == "test" ? spec.Get("project")! : "Raft.slnx";

    private static ProcessResult Build(string wt, string scope = "Raft.slnx", bool forceRestore = false)
    {
        if (!forceRestore)
        {
            return Proc.Run("dotnet", wt, "build", scope, "-nologo", "-v:q", "-warnaserror");
        }

        var restore = Proc.Run("dotnet", wt, "restore", "Raft.slnx", "--force-evaluate", "-p:RestoreLockedMode=false", "-nologo", "-v:q");
        return restore.Ok
            ? Proc.Run("dotnet", wt, "build", scope, "--no-restore", "-nologo", "-v:q", "-warnaserror")
            : restore;
    }

    /// <summary>A target's method, without a theory case's arguments: what a baseline filter names.</summary>
    internal static string TargetMethod(string target) => target.Contains('(', StringComparison.Ordinal) ? target[..target.IndexOf('(', StringComparison.Ordinal)] : target;

    /// <summary>The shard's baseline checks: one unit per target project (test entries), one per baseline command (command entries).</summary>
    /// <summary>The baseline unit an entry belongs to: its test project, or its baseline (else its) command.</summary>
    internal static string UnitKey(SabotageSpec s) => s.Kind == "test" ? "test:" + s.Get("project") : "command:" + (s.Get("baseline") ?? s.Get("command"));

    private static PlanEntry PlanEntryOf(SabotageSpec s) => new(s.Id, UnitKey(s));

    /// <summary>The recorded costs (P10-00); an absent file is a refusal, not equal costs.</summary>
    private static ShardCosts LoadCosts(Repo repo) => ShardCosts.Parse(File.ReadAllText(repo.PathOf(ShardPlan.CostFile)));

    /// <summary>Each entry's time beside its recorded cost; a failure for each beyond the stale factor (P10-00).</summary>
    internal static void CheckCosts(IReadOnlyDictionary<string, double> timings, ShardCosts costs, Findings f)
    {
        // The furthest from its line either way: a ratio of 0.4 is further than one of 2.
        var (worst, worstId, worstDistance) = (1.0, "none", 1.0);
        foreach (var (id, actual) in timings.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var recorded = costs.Entry(id);
            var ratio = recorded > 0 && actual > 0 ? actual / recorded : double.PositiveInfinity;
            var distance = Math.Max(ratio, 1 / ratio);
            if (distance > worstDistance)
            {
                (worst, worstId, worstDistance) = (ratio, id, distance);
            }

            if (ShardPlan.Stale(actual, recorded, costs.StaleFactor, costs.StaleFloor))
            {
                f.Fail($"{id}: took {actual:F1}s against {recorded:F1}s recorded in {ShardPlan.CostFile}, beyond the stale factor {costs.StaleFactor} (and the {costs.StaleFloor}-s floor): correct its line");
            }
        }

        f.Note($"recorded costs: {timings.Count} entries timed; the furthest from its line is {worstId} at {worst:F2} times its recorded cost (stale beyond {costs.StaleFactor} either way, and {costs.StaleFloor} s)");
    }

    internal static Dictionary<string, IReadOnlyList<SabotageSpec>> BaselineUnits(IReadOnlyList<SabotageSpec> specs) =>
        specs.GroupBy(UnitKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SabotageSpec>)g.ToList(), StringComparer.Ordinal);

    /// <summary>The entries no baseline unit holds (P7-12): each target must be shown to pass unpatched by some unit.</summary>
    internal static List<string> Uncovered(IReadOnlyList<SabotageSpec> specs, IReadOnlyDictionary<string, IReadOnlyList<SabotageSpec>> units)
    {
        var covered = units.Values.SelectMany(u => u).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        return specs.Select(s => s.Id).Where(id => !covered.Contains(id)).ToList();
    }

    /// <summary>Every baseline unit to exactly one worker, round-robin in ordinal order: deterministic.</summary>
    internal static List<List<string>> AssignBaselines(IReadOnlyList<string> units, int workers)
    {
        var shares = Enumerable.Range(0, workers).Select(_ => new List<string>()).ToList();
        var ordered = units.Order(StringComparer.Ordinal).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            shares[i % workers].Add(ordered[i]);
        }

        return shares;
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

    /// <summary>A failure message on one line, cut to a readable length.</summary>
    private static string Short(string message)
    {
        var one = string.Join(' ', message.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        return one.Length <= 160 ? one : one[..160] + "…";
    }

    private static string FirstError(ProcessResult r) =>
        (r.StdOut + r.StdErr).Split('\n').FirstOrDefault(l => l.Contains(" error ", StringComparison.Ordinal))?.Trim() ?? Tail(r, 2);

    private static string Tail(ProcessResult r, int lines = 12) =>
        string.Join('\n', (r.StdOut + r.StdErr).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines));
}
