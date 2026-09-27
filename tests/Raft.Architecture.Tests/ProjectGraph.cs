using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace Raft.Architecture.Tests;

/// <summary>
/// The project graph as MSBuild evaluates it — not as the csproj text reads — so references
/// injected by Directory.Build.props/targets or conditions are seen.
/// </summary>
internal sealed record EvaluatedProject(
    string Name,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> AssemblyReferences,
    IReadOnlyList<string> ExplicitFrameworkReferences);

internal static class ProjectGraph
{
    /// <summary>Every *.csproj under the repository, excluding build output.</summary>
    public static IReadOnlyList<string> ProjectFilesOnDisk() =>
        Directory.EnumerateFiles(RepoRoot.Path, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .Select(p => Path.GetRelativePath(RepoRoot.Path, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Every project listed in Raft.slnx.</summary>
    public static IReadOnlyList<string> ProjectFilesInSolution() =>
        XDocument.Load(Path.Combine(RepoRoot.Path, "Raft.slnx"))
            .Descendants("Project")
            .Select(e => (string?)e.Attribute("Path") ?? throw new InvalidOperationException("Project without Path"))
            .Select(p => p.Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    public static EvaluatedProject Evaluate(string relativeProjectPath)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepoRoot.Path,
        };
        foreach (var a in new[]
                 {
                     "msbuild", relativeProjectPath, "-nologo",
                     "-getItem:ProjectReference", "-getItem:PackageReference",
                     "-getItem:Reference", "-getItem:FrameworkReference",
                 })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start dotnet");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"msbuild evaluation of {relativeProjectPath} failed ({p.ExitCode}):\n{stdout}\n{stderr}");
        }

        using var doc = JsonDocument.Parse(stdout);
        var items = doc.RootElement.GetProperty("Items");

        IEnumerable<JsonElement> Get(string kind) =>
            items.TryGetProperty(kind, out var arr) ? arr.EnumerateArray() : Enumerable.Empty<JsonElement>();

        static string Str(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

        return new EvaluatedProject(
            Name: Path.GetFileNameWithoutExtension(relativeProjectPath),
            ProjectReferences: Get("ProjectReference")
                .Select(e => Path.GetFileNameWithoutExtension(Str(e, "FullPath"))).Order(StringComparer.Ordinal).ToList(),
            PackageReferences: Get("PackageReference")
                .Where(e => Str(e, "IsImplicitlyDefined") != "true")
                .Select(e => Str(e, "Identity")).Order(StringComparer.Ordinal).ToList(),
            AssemblyReferences: Get("Reference")
                .Select(e => Str(e, "Identity")).Order(StringComparer.Ordinal).ToList(),
            ExplicitFrameworkReferences: Get("FrameworkReference")
                .Where(e => Str(e, "IsImplicitlyDefined") != "true")
                .Select(e => Str(e, "Identity")).Order(StringComparer.Ordinal).ToList());
    }
}
