using System;
using System.IO;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// The prediction-order and trailer gate, against fixture histories. Each rule has a history that
/// must fail it; the clean history shows the rule is not failing everything.
/// Sabotages S-bd-3, S-bd-4, S-bd-5 disable a rule in the gate and must turn one of these red.
/// </summary>
public sealed class TrailersTests
{
    private static GitFixture Planned()
    {
        var g = new GitFixture();
        g.Write("README.md", "hi").Commit("docs: readme");
        g.Write("docs/phases/P9/breakdown.md", GitFixture.Breakdown()).Commit("docs: plan");
        return g;
    }

    [Fact]
    public void ACleanHistoryPasses()
    {
        using var g = Planned();
        g.Write("Raft.slnx", "<Solution />").Write("src/W.cs", "class W {}").Commit("feat: widget\n\nTask: P9-01");

        var f = Trailers.Run(g.Repo, []);

        Assert.Empty(f.Failures);
    }

    [Fact]
    public void ACodeCommitWithoutATrailerFails()
    {
        using var g = Planned();
        g.Write("Raft.slnx", "<Solution />").Write("src/W.cs", "class W {}").Commit("feat: widget");

        var f = Trailers.Run(g.Repo, []);

        Assert.Contains(f.Failures, m => m.Contains("has no Task: trailer", StringComparison.Ordinal));
    }

    [Fact]
    public void ADocumentationOnlyCommitNeedsNoTrailer()
    {
        using var g = Planned();
        g.Write("Raft.slnx", "<Solution />").Write("src/W.cs", "class W {}").Commit("feat: widget\n\nTask: P9-01");
        g.Write("docs/notes.md", "notes").Write("AGENTS.md", "rules").Commit("docs: notes");

        Assert.Empty(Trailers.Run(g.Repo, []).Failures);
    }

    [Fact]
    public void ATrailerNamingNoTaskFails()
    {
        using var g = Planned();
        g.Write("Raft.slnx", "<Solution />").Write("src/W.cs", "class W {}").Commit("feat: widget\n\nTask: P9-99");

        Assert.Contains(Trailers.Run(g.Repo, []).Failures, m => m.Contains("P9-99 names no task", StringComparison.Ordinal));
    }

    [Fact]
    public void ImplementationBeforeThePredictionFails()
    {
        using var g = new GitFixture();
        g.Write("docs/phases/P9/breakdown.md", GitFixture.Breakdown("- **Prediction:** placeholder. **Observable:** none.")).Commit("docs: plan");
        g.Write("Raft.slnx", "<Solution />").Write("src/W.cs", "class W {}").Commit("feat: widget\n\nTask: P9-01");
        g.Write("docs/phases/P9/breakdown.md", GitFixture.Breakdown()).Commit("docs: the real prediction, after the fact");

        Assert.Contains(Trailers.Run(g.Repo, []).Failures, m => m.Contains("is not after the commit that introduced its prediction", StringComparison.Ordinal));
    }

    [Fact]
    public void PredictionAndImplementationInOneCommitFails()
    {
        using var g = new GitFixture();
        g.Write("Raft.slnx", "<Solution />").Write("docs/phases/P9/breakdown.md", GitFixture.Breakdown())
            .Write("src/W.cs", "class W {}").Commit("feat: widget and plan\n\nTask: P9-01");

        Assert.Contains(Trailers.Run(g.Repo, []).Failures, m => m.Contains("is not after", StringComparison.Ordinal));
    }

    [Fact]
    public void ARootCommitThatAddsCodeNeedsATrailer()
    {
        using var g = new GitFixture();
        g.Write("Raft.slnx", "<Solution />").Write("src/W.cs", "class W {}").Write("docs/phases/P9/breakdown.md", GitFixture.Breakdown()).Commit("root: everything at once");

        Assert.Contains(Trailers.Run(g.Repo, []).Failures, m => m.Contains("has no Task: trailer", StringComparison.Ordinal));
    }

    [Fact]
    public void AShallowCloneFails()
    {
        using var g = Planned();
        g.Write("Raft.slnx", "<Solution />").Write("src/W.cs", "class W {}").Commit("feat: widget\n\nTask: P9-01");
        var shallow = Path.Combine(Path.GetTempPath(), "gates-shallow-" + Guid.NewGuid().ToString("N"));
        try
        {
            g.Git("clone", "-q", "--depth", "1", "file://" + g.Root, shallow);

            var f = Trailers.Run(Repo.Locate(shallow), []);

            Assert.Contains(f.Failures, m => m.Contains("shallow", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(shallow, recursive: true);
        }
    }
}
