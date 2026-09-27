using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Raft.Architecture.Tests;

/// <summary>
/// Spec §4: the dependency partial order, checked against the evaluated project graph.
///
/// Vacuity risk: the test discovers zero projects (wrong root inside a container), loops over
/// nothing, and passes. Guard: the discovered set must equal the table exactly, in both
/// directions, so a missing or an unlisted project fails. Second risk: reading csproj text
/// misses a reference injected by Directory.Build.props; guard: MSBuild evaluation.
/// Sabotages: S-layer-1..4, S-skel-1.
/// </summary>
public sealed class LayeringTests
{
    private static readonly string[] None = [];

    /// <summary>Allowed direct project references. Production projects: exactly the spec §4 order.</summary>
    private static readonly Dictionary<string, (string Path, string[] AllowedProjectRefs, string[] AllowedPackages)> Table = new()
    {
        ["Raft.Core"] = ("src/Raft.Core/Raft.Core.csproj", None, None),
        ["Raft.Checker"] = ("src/Raft.Checker/Raft.Checker.csproj", None, None),
        ["Raft.Kv"] = ("src/Raft.Kv/Raft.Kv.csproj", ["Raft.Core"], None),
        ["Raft.Simulation"] = ("src/Raft.Simulation/Raft.Simulation.csproj", ["Raft.Core"], None),
        ["Raft.Host"] = ("src/Raft.Host/Raft.Host.csproj", ["Raft.Core", "Raft.Kv"], None),
        ["Raft.Gates"] = ("tools/Raft.Gates/Raft.Gates.csproj", None, None),
        ["Raft.Simulation.Tests"] = ("tests/Raft.Simulation.Tests/Raft.Simulation.Tests.csproj", ["Raft.Core", "Raft.Simulation", "Raft.SimRun"], ["xunit.v3.mtp-v2"]),
        ["Raft.SimRun"] = ("tools/Raft.SimRun/Raft.SimRun.csproj", ["Raft.Core", "Raft.Simulation"], None),
        ["Raft.Checker.Tests"] = ("tests/Raft.Checker.Tests/Raft.Checker.Tests.csproj", ["Raft.Checker"], ["xunit.v3.mtp-v2"]),
        ["Raft.Gates.Tests"] = ("tests/Raft.Gates.Tests/Raft.Gates.Tests.csproj", ["Raft.Gates"], ["xunit.v3.mtp-v2"]),
        ["Raft.Architecture.Tests"] = ("tests/Raft.Architecture.Tests/Raft.Architecture.Tests.csproj", ["Raft.Core"], ["xunit.v3.mtp-v2"]),
    };

    [Fact]
    public void EveryProjectOnDiskIsInTheTableAndViceVersa()
    {
        var onDisk = ProjectGraph.ProjectFilesOnDisk();
        var inTable = Table.Values.Select(v => v.Path).Order(StringComparer.Ordinal).ToList();

        Assert.NotEmpty(onDisk);
        Assert.Equal(inTable, onDisk);
    }

    [Fact]
    public void EveryProjectInTheTableIsInTheSolution()
    {
        var inSolution = ProjectGraph.ProjectFilesInSolution();
        var inTable = Table.Values.Select(v => v.Path).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(inTable, inSolution);
    }

    public static TheoryData<string> Projects() => new(Table.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Projects))]
    public void ProjectReferencesFollowThePartialOrder(string name)
    {
        var (path, allowed, _) = Table[name];
        var evaluated = ProjectGraph.Evaluate(path);

        var illegal = evaluated.ProjectReferences.Except(allowed, StringComparer.Ordinal).ToList();
        Assert.True(illegal.Count == 0, $"{name} references {string.Join(", ", illegal)}; allowed: [{string.Join(", ", allowed)}]");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void NoPackageAssemblyOrFrameworkReferencesOutsideTheTable(string name)
    {
        var (path, _, allowedPackages) = Table[name];
        var evaluated = ProjectGraph.Evaluate(path);

        var illegalPackages = evaluated.PackageReferences.Except(allowedPackages, StringComparer.Ordinal).ToList();
        Assert.True(illegalPackages.Count == 0, $"{name} has package references {string.Join(", ", illegalPackages)}");
        Assert.True(evaluated.AssemblyReferences.Count == 0, $"{name} has raw assembly references {string.Join(", ", evaluated.AssemblyReferences)}");
        Assert.True(evaluated.ExplicitFrameworkReferences.Count == 0, $"{name} has framework references {string.Join(", ", evaluated.ExplicitFrameworkReferences)}");
    }

    [Fact]
    public void CoreReferencesNothing()
    {
        var evaluated = ProjectGraph.Evaluate(Table["Raft.Core"].Path);

        Assert.Empty(evaluated.ProjectReferences);
        Assert.Empty(evaluated.PackageReferences);
    }
}
