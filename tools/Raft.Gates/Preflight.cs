using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Raft.Gates;

/// <summary>
/// Spec §3 and §10 ("a failure is evidence about the invocation first"): check the environment and
/// the pins before any build result is believed. Every expected value comes from a source other
/// than the thing being checked — the SDK against global.json, the workflow against
/// ci/image.digest, evaluated properties against constants here — never a value against itself.
/// </summary>
internal static partial class Preflight
{
    /// <summary>Evaluated per-project properties and the only acceptable value of each.</summary>
    internal static readonly (string Property, string Expected)[] RequiredProperties =
    [
        ("TargetFramework", "net10.0"),
        ("LangVersion", "14"),
        ("Nullable", "enable"),
        ("TreatWarningsAsErrors", "true"),
        ("AnalysisLevel", "10.0"),
        ("Deterministic", "true"),
        ("RestorePackagesWithLockFile", "true"),
    ];

    public static Findings Run(Repo repo, string[] args)
    {
        var rest = args.ToList();
        var allowDirty = Options.Flag(rest, "--allow-dirty");
        var f = new Findings();

        CheckGlobalJson(repo, f);
        CheckImageDigest(repo, f);
        CheckWorkflows(repo, f);
        CheckProjects(repo, f);
        CheckGit(repo, f, allowDirty);
        return f;
    }

    internal static void CheckGlobalJson(Repo repo, Findings f)
    {
        using var doc = JsonDocument.Parse(repo.ReadText("global.json"));
        var sdk = doc.RootElement.GetProperty("sdk");
        var pinned = sdk.GetProperty("version").GetString() ?? "";
        var roll = sdk.TryGetProperty("rollForward", out var r) ? r.GetString() : null;

        f.Require(ExactSdkVersion().IsMatch(pinned), $"global.json sdk.version '{pinned}' is not an exact version");
        f.Require(roll == "latestPatch", $"global.json rollForward is '{roll ?? "(absent)"}', must be 'latestPatch'");

        var actual = repo.Dotnet("--version");
        f.Require(actual.Ok, $"'dotnet --version' failed: {actual}");
        var version = actual.StdOut.Trim();
        f.Require(version == pinned, $"running SDK {version} != global.json {pinned}");
        f.Note($"SDK {version} (global.json {pinned}, rollForward {roll})");
    }

    internal static void CheckImageDigest(Repo repo, Findings f)
    {
        var digest = repo.ReadText("ci/image.digest").Trim();
        f.Require(DigestPinnedImage().IsMatch(digest), $"ci/image.digest '{digest}' is not pinned by sha256 digest");
    }

    internal static void CheckWorkflows(Repo repo, Findings f)
    {
        var dir = repo.PathOf(".github/workflows");
        var files = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.yml").Concat(Directory.EnumerateFiles(dir, "*.yaml")).Order(StringComparer.Ordinal).ToList()
            : [];
        f.Require(files.Count > 0, "no workflows under .github/workflows");

        var digest = repo.ReadText("ci/image.digest").Trim();
        var sdkImageSeen = false;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            foreach (var problem in WorkflowProblems(File.ReadAllLines(file), digest, ref sdkImageSeen))
            {
                f.Fail($"{name}: {problem}");
            }
        }

        f.Require(sdkImageSeen, "no workflow runs in the pinned SDK image");
    }

    /// <summary>
    /// Text checks over a workflow — a known weakness (a pattern covers its instances, not the
    /// property), kept because the workflow has no evaluated form to read instead.
    /// </summary>
    internal static IEnumerable<string> WorkflowProblems(IReadOnlyList<string> lines, string pinnedSdkImage, ref bool sdkImageSeen)
    {
        var problems = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var code = line.Split('#')[0];
            var n = i + 1;

            if (code.Contains("continue-on-error", StringComparison.Ordinal))
            {
                problems.Add($"line {n}: continue-on-error");
            }

            if (OrTrue().IsMatch(code) || code.Contains("set +e", StringComparison.Ordinal))
            {
                problems.Add($"line {n}: exit status suppressed");
            }

            foreach (Match m in ImageReference().Matches(code))
            {
                var image = m.Groups["img"].Value;
                if (image.StartsWith("mcr.microsoft.com/dotnet/sdk", StringComparison.Ordinal))
                {
                    sdkImageSeen = true;
                    if (image != pinnedSdkImage)
                    {
                        problems.Add($"line {n}: SDK image '{image}' != ci/image.digest '{pinnedSdkImage}'");
                    }
                }
                else if (!DigestPinnedImage().IsMatch(image))
                {
                    problems.Add($"line {n}: image '{image}' not pinned by digest");
                }
            }

            var uses = UsesAction().Match(code);
            var usesRef = uses.Groups["ref"].Value;
            if (uses.Success && !(usesRef.Length == 40 && usesRef.All(Uri.IsHexDigit)))
            {
                problems.Add($"line {n}: action '{uses.Groups["action"].Value}@{uses.Groups["ref"].Value}' not pinned by commit SHA");
            }
        }

        return problems;
    }

    internal static void CheckProjects(Repo repo, Findings f)
    {
        var projects = repo.ProjectFiles();
        f.Require(projects.Count > 0, "no projects found");

        foreach (var project in projects)
        {
            var args = new List<string> { "msbuild", project, "-nologo" };
            args.AddRange(RequiredProperties.Select(p => $"-getProperty:{p.Property}"));
            var result = repo.Dotnet(args.ToArray());
            if (!result.Ok)
            {
                f.Fail($"{project}: evaluation failed: {result}");
                continue;
            }

            using var doc = JsonDocument.Parse(result.StdOut);
            var props = doc.RootElement.GetProperty("Properties");
            foreach (var (property, expected) in RequiredProperties)
            {
                var actual = props.GetProperty(property).GetString() ?? "";
                f.Require(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
                    $"{project}: {property} evaluates to '{actual}', must be '{expected}'");
            }

            var lockFile = Path.Combine(Path.GetDirectoryName(repo.PathOf(project))!, "packages.lock.json");
            f.Require(File.Exists(lockFile), $"{project}: no packages.lock.json");
        }

        f.Note($"{projects.Count} projects evaluated");
    }

    internal static void CheckGit(Repo repo, Findings f, bool allowDirty)
    {
        var shallow = repo.Git("rev-parse", "--is-shallow-repository");
        f.Require(shallow.Ok && shallow.StdOut.Trim() == "false",
            $"repository is shallow (or git failed): {shallow.StdOut.Trim()} {shallow.StdErr.Trim()} — the history gates need full history");

        if (!allowDirty)
        {
            var status = repo.Git("status", "--porcelain");
            f.Require(status.Ok && status.StdOut.Length == 0, $"working tree is not clean:\n{status.StdOut}{status.StdErr}");
        }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex ExactSdkVersion();

    [GeneratedRegex(@"^[a-z0-9./_-]+(:[\w.-]+)?@sha256:[0-9a-f]{64}$")]
    private static partial Regex DigestPinnedImage();

    [GeneratedRegex(@"\|\|\s*(true|:)\b")]
    private static partial Regex OrTrue();

    [GeneratedRegex(@"(?:image:|container:|docker://|docker\s+run\b[^\n]*?\s)\s*(?<img>[a-z0-9][a-z0-9./_-]*/[a-z0-9./_-]+(?::[\w.-]+)?(?:@sha256:[0-9a-f]+)?)")]
    private static partial Regex ImageReference();

    [GeneratedRegex(@"uses:\s*(?<action>[\w.-]+/[\w./-]+)@(?<ref>[\w.-]+)")]
    private static partial Regex UsesAction();
}
