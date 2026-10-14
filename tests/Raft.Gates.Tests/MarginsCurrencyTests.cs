using System;
using System.Collections.Generic;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P14-06: each row of a bench margins table carries the commit it was measured at, and a row whose
/// control's sources changed after that commit is stale; it fails a phase report at the head and is
/// noted on any other push (the observer margins-commit). Sabotage S-margin-2 (each row's commit read
/// as current).
/// </summary>
public sealed class MarginsCurrencyTests
{
    private static readonly string[] Table =
    [
        "# P12-08's audit (scripts/margins.sh): ...; at 3a1056b.",
        "# at 7f4b02c: handoff-low, handoff-others (measured again alone)",
        "# check\tbound\tpass\tkind\tunpatched\tpatched\tnote",
        "stall-open-p95\t150000\tabove\ttiming\t800000\t3000\tn",
        "handoff-low\t0.5\tabove\tratio\t1.0\t0.01\tn",
        "handoff-others\t0.4\tbelow\tratio\t0.05\t1.0\tn",
    ];

    [Fact]
    public void EachRowTakesTheHeadersCommitUnlessALineNamesItsOwn()
    {
        var commits = Margins.RowCommits(Table, Margins.Parse(Table).ConvertAll(r => r.Check));
        Assert.Equal("3a1056b", commits["stall-open-p95"]);
        Assert.Equal("7f4b02c", commits["handoff-low"]);
        Assert.Equal("7f4b02c", commits["handoff-others"]);
    }

    /// <summary>Its control's sources last changed at "c2", which 7f4b02c contains and 3a1056b does not.</summary>
    private static Findings Check(bool atReport)
    {
        var f = new Findings();
        var rows = Margins.RowCommits(Table, Margins.Parse(Table).ConvertAll(r => r.Check));
        var contains = new HashSet<(string, string)> { ("c2", "7f4b02c") };
        Margins.CheckCurrency("t.tsv", rows, _ => "c2", (ancestor, commit) => contains.Contains((ancestor, commit)), atReport, "head000", f);
        return f;
    }

    /// <summary>Sabotage S-margin-2.</summary>
    [Fact]
    public void ARowMeasuredBeforeItsControlLastChangedFailsAReport()
    {
        var failure = Assert.Single(Check(atReport: true).Failures);
        Assert.StartsWith("docs/phases/*/margins-bench.tsv: stall-open-p95 claims 3a1056b; git, the last change to each row's control: its control's sources last changed at c2, after it", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void OnAPushWithoutAReportAStaleRowIsNotedNotFailed()
    {
        var f = Check(atReport: false);
        Assert.Empty(f.Failures);
        Assert.Contains(f.Notes, n => n.StartsWith("would fail a report: docs/phases/*/margins-bench.tsv: stall-open-p95 claims 3a1056b", StringComparison.Ordinal));
    }
}
