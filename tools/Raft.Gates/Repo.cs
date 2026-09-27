using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Raft.Gates;

/// <summary>The repository a gate runs against: by default the one containing the working directory.</summary>
internal sealed class Repo
{
    private Repo(string root) => Root = root;

    public string Root { get; }

    public static Repo Locate(string? explicitRoot)
    {
        var start = new DirectoryInfo(explicitRoot ?? Environment.CurrentDirectory);
        for (var dir = start; dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Raft.slnx")))
            {
                return new Repo(dir.FullName);
            }
        }

        throw new InvalidOperationException($"no Raft.slnx at or above {start.FullName}");
    }

    public string PathOf(string relative) => Path.Combine(Root, relative);

    public string ReadText(string relative) => File.ReadAllText(PathOf(relative));

    /// <summary>Every *.csproj under the root, excluding build output, as repo-relative paths.</summary>
    public IReadOnlyList<string> ProjectFiles() =>
        Directory.EnumerateFiles(Root, "*.csproj", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/'))
            .Where(p => !p.Split('/').Any(s => s is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToList();

    public ProcessResult Git(params string[] args) => Proc.Run("git", Root, args);

    public ProcessResult Dotnet(params string[] args) => Proc.Run("dotnet", Root, args);
}
