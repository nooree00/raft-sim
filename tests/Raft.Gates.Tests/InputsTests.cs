using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P13-01: every input a gate reads is registered with what verifies it. The inputs are found in
/// the tree, not listed, so a gate reading a new file fails until it is registered. Sabotage
/// S-input-1 (the found inputs never compared with the registry).
/// </summary>
public sealed class InputsTests
{
    private static readonly string[] Gates = ["register", "testcount", "preflight"];

    private static readonly string[] Tracked = ["ci/inputs.txt", "ci/test-baseline.txt", "ci/sabotage-costs.txt", "ci/sabotage-touched.txt", "ci/budget-seeds.txt", "measurements/m-1.json", "docs/register.md", "Raft.slnx"];

    private static IReadOnlyList<Inputs.Line> Lines(string text, Findings f) => Inputs.Parse(text, f);

    private const string Registry = """
        ci/inputs.txt             subject       this registry
        ci/test-baseline.txt      construction  testcount: both ways
        measurements              currency      record-commit: ancestors
        ci/sabotage-touched.txt   currency      touched-run: the stage's run
        ci/sabotage-costs.txt     currency      github-run: the run's total
        ci/budget-seeds.txt       currency      soak-ranking: the soak's ten hardest
        docs/register.md          row           "the register's own row" nothing checks it
        """;

    private static Dictionary<string, SortedSet<string>> Found(params string[] paths) =>
        paths.ToDictionary(p => p, _ => new SortedSet<string>(StringComparer.Ordinal) { "tools/Raft.Gates/X.cs" }, StringComparer.Ordinal);

    private static Findings Evaluate(string registry, IReadOnlyDictionary<string, SortedSet<string>> found, string[]? openRows = null)
    {
        var f = new Findings();
        Inputs.Evaluate(Lines(registry, f), found, Tracked, Gates, openRows ?? ["the register's own row is open"], f);
        return f;
    }

    [Fact]
    public void ARegistryCoveringEveryInputFoundPasses() =>
        Assert.Empty(Evaluate(Registry, Found("ci/inputs.txt", "ci/test-baseline.txt", "docs/register.md")).Failures);

    /// <summary>The fifth input: a gate reads a file nobody registered. Sabotage S-input-1.</summary>
    [Fact]
    public void AnInputNotInTheRegistryFails()
    {
        var f = Evaluate(Registry, Found("ci/inputs.txt", "Raft.slnx"));

        var failure = Assert.Single(f.Failures);
        Assert.Equal("Raft.slnx: read by tools/Raft.Gates/X.cs and not in ci/inputs.txt: register it with what verifies it (if nothing does yet, a row)", failure);
    }

    [Fact]
    public void ALineNamingNoTrackedFileFails() =>
        Assert.Contains(Evaluate(Registry + "\nci/gone.txt decision a value", Found()).Failures, x => x.Contains("ci/gone.txt names no tracked file", StringComparison.Ordinal));

    [Fact]
    public void ACurrencyLineNamingNoObserverFailsAndTheObserverItLeftOutToo()
    {
        var f = Evaluate(Registry.Replace("soak-ranking:", "soak-order:", StringComparison.Ordinal), Found());

        Assert.Contains(f.Failures, x => x.Contains("no observer 'soak-order'", StringComparison.Ordinal));
        Assert.Contains(f.Failures, x => x.Contains("observer soak-ranking", StringComparison.Ordinal) && x.Contains("named by 0 registry lines", StringComparison.Ordinal));
    }

    [Fact]
    public void AConstructionLineNamingNoGateFails() =>
        Assert.Contains(Evaluate(Registry.Replace("testcount:", "countall:", StringComparison.Ordinal), Found()).Failures, x => x.Contains("no gate 'countall'", StringComparison.Ordinal));

    [Fact]
    public void ARowMatchingNoOpenRegisterRowFails() =>
        Assert.Contains(Evaluate(Registry, Found(), openRows: ["something else"]).Failures, x => x.Contains("matches 0 open register rows", StringComparison.Ordinal));

    [Fact]
    public void ALineThatDoesNotParseFails()
    {
        var f = new Findings();

        Inputs.Parse("ci/x.txt maybe something", f);

        Assert.Contains(f.Failures, x => x.Contains("is not PATH KIND DETAIL", StringComparison.Ordinal));
    }

    /// <summary>What is found: every ci/ file, a ci/ path a script or project names, and a path a gate's string literal begins with; not a bare directory name, nor a document's mention.</summary>
    [Fact]
    public void TheInputsAreFoundInTheTree()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ci/a.txt"] = "",
            ["scripts/run.sh"] = "cat ci/b.txt.\n",
            ["tests/X/X.csproj"] = "<None Include=\"../../ci/c.txt\" />",
            ["tools/Raft.Gates/G.cs"] = "var p = \"docs/phases/{phase}/report.md\"; var q = \"ci\"; var r = $\"docs/x.md missing\"; var s = \"Raft.slnx\"; var t = \"nothing/here\";",
            ["docs/notes.md"] = "see ci/d.txt",
            ["tests/Raft.Gates.Tests/F.cs"] = "g.Write(\"ci/e.txt\", \"\");",
            ["Raft.slnx"] = "",
        };

        var found = Inputs.Discover([.. files.Keys], p => files[p]);

        Assert.Equal(["Raft.slnx", "ci/a.txt", "ci/b.txt", "ci/c.txt", "docs/phases/*/report.md", "docs/x.md"], found.Keys.Order(StringComparer.Ordinal).ToArray());
    }
}
