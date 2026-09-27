using System;
using System.IO;

namespace Raft.Architecture.Tests;

internal static class RepoRoot
{
    /// <summary>The directory containing Raft.slnx, found by walking up from the test binary.</summary>
    public static string Path { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Raft.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Raft.slnx not found above {AppContext.BaseDirectory}");
    }
}
