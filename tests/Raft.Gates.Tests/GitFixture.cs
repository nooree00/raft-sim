using System;
using System.IO;
using Raft.Gates;

namespace Raft.Gates.Tests;

/// <summary>A throwaway git repository shaped enough like this one for the history gates to run.</summary>
internal sealed class GitFixture : IDisposable
{
    public const string Prediction = "- **Prediction:** The widget overflows. **Observable:** a red test.";

    public GitFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "gates-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Git("init", "-q", "-b", "main");
    }

    public string Root { get; }

    public Repo Repo => Repo.Locate(Root);

    public static string Breakdown(string prediction = Prediction) => $"""
        # P9

        ### P9-01 — Widget

        - **Task:** Build the widget.
        - **Vacuity:** It could pass with no widget.
        - **Sabotage:** S-widget-1
        - **Verifiable here:** yes — locally.
        {prediction}
        - **Outcome:** pending
        """;

    public GitFixture Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return this;
    }

    public string Commit(string message)
    {
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", message);
        return Git("rev-parse", "HEAD").Trim();
    }

    public string Git(params string[] args)
    {
        var r = Proc.Run("git", Root, args);
        if (!r.Ok)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {r}");
        }

        return r.StdOut;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
