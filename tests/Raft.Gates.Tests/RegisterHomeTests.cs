using System;
using System.Collections.Generic;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P15-09: a register row's home. An open row may have none (a deferred item the reviewer chose not to
/// schedule); a done or dropped row names the phase that closed it; an open row's home is a phase not
/// yet complete. Sabotage S-reg-8 (an unhomed row accepted whatever its status).
/// </summary>
public sealed class RegisterHomeTests
{
    private static readonly HashSet<string> Phases = new(["P15", "P16"], StringComparer.Ordinal);
    private static readonly HashSet<string> Completed = new(["P15"], StringComparer.Ordinal);

    private static IReadOnlyList<string> Check(string phase, string status)
    {
        var f = new Findings();
        Register.CheckHome(new Register.Row("an item", phase, status, "—"), Phases, Completed, f);
        return f.Failures;
    }

    /// <summary>Sabotage S-reg-8.</summary>
    [Fact]
    public void AnOpenRowMayBeUnhomedButNotAClosedOne()
    {
        Assert.Empty(Check(Register.Unhomed, "open"));
        Assert.Contains(Check(Register.Unhomed, "done"), e => e.Contains("only an open row may be unhomed", StringComparison.Ordinal));
        Assert.Contains(Check(Register.Unhomed, "dropped"), e => e.Contains("only an open row may be unhomed", StringComparison.Ordinal));
        Assert.Empty(Check("P16", "open"));
        Assert.Contains(Check("P15", "open"), e => e.Contains("which is complete", StringComparison.Ordinal));
        Assert.Contains(Check("P99", "open"), e => e.Contains("unknown phase 'P99'", StringComparison.Ordinal));
    }
}
