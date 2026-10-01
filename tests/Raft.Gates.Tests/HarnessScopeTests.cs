using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P4-12: what a harness entry builds, and who runs a shard's baseline checks. Vacuity risk: a
/// baseline split across workers that drops a project would let a target that fails unpatched go
/// unnoticed; so every unit must land on exactly one worker, for any worker count. Sabotage
/// S-harness-2.
/// </summary>
public sealed class HarnessScopeTests
{
    private static SabotageSpec Spec(string id, params (string Key, string Value)[] fields) =>
        new(id, fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal), "patch.diff", null);

    [Fact]
    public void ATestEntryBuildsItsTargetProjectAndACommandEntryTheSolution()
    {
        Assert.Equal("tests/Raft.Core.Tests", Sabotage.BuildScope(Spec("S-a-1", ("kind", "test"), ("project", "tests/Raft.Core.Tests"))));
        Assert.Equal("Raft.slnx", Sabotage.BuildScope(Spec("S-a-2", ("kind", "command"), ("command", "scripts/ci-test.sh"))));
    }

    [Fact]
    public void EveryBaselineUnitIsAssignedToExactlyOneWorker()
    {
        var specs = new[]
        {
            Spec("S-a-1", ("kind", "test"), ("project", "tests/A")),
            Spec("S-a-2", ("kind", "test"), ("project", "tests/B")),
            Spec("S-a-3", ("kind", "test"), ("project", "tests/A")),
            Spec("S-a-4", ("kind", "command"), ("command", "x")),
            Spec("S-a-5", ("kind", "command"), ("command", "y"), ("baseline", "x")),
            Spec("S-a-6", ("kind", "test"), ("project", "tests/C")),
        };
        var units = Sabotage.BaselineUnits(specs);
        Assert.Equal(["command:x", "test:tests/A", "test:tests/B", "test:tests/C"], units.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["S-a-4", "S-a-5"], units["command:x"].Select(s => s.Id));

        foreach (var workers in new[] { 1, 2, 3, 4, 7 })
        {
            var shares = Sabotage.AssignBaselines(units.Keys.ToList(), workers);
            var assigned = shares.SelectMany(s => s).ToList();
            Assert.True(assigned.Count == units.Count && assigned.ToHashSet(StringComparer.Ordinal).SetEquals(units.Keys),
                $"{workers} workers: {assigned.Count} assignments for {units.Count} baseline units — a unit dropped or doubled");
        }
    }

    [Fact]
    public void ATheoryTargetsBaselineFilterNamesItsMethod() =>
        Assert.Equal("A.B.C", Sabotage.TargetMethod("A.B.C(limit: \"heartbeat\")"));
}
