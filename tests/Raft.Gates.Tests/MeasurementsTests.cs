using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// P10-01: a measurement record carries every configuration field, and a report's number cites a
/// record or a CI run. Every rule has a case that must fail it. Sabotages S-meas-1 (a missing field
/// accepted) and S-meas-2 (an uncited number accepted).
/// </summary>
public sealed class MeasurementsTests
{
    private static JsonElement Record(string id, Action<Dictionary<string, string>>? edit = null)
    {
        var config = Measurements.ConfigFields.ToDictionary(f => f, f => "x-" + f, StringComparer.Ordinal);
        edit?.Invoke(config);
        return JsonSerializer.SerializeToElement(new { id, task = "P10-02", config, results = new Dictionary<string, double> { ["p50_us"] = 412 } });
    }

    [Fact]
    public void ACompleteRecordPasses() => Assert.Empty(Measurements.RecordProblems("sync-1", Record("sync-1")));

    [Theory]
    [InlineData("configuration")]
    [InlineData("cpuLimit")]
    [InlineData("commit")]
    public void ARecordMissingAConfigurationFieldFails(string field)
    {
        var problems = Measurements.RecordProblems("sync-1", Record("sync-1", c => c.Remove(field)));

        Assert.Contains(problems, p => p.Contains($"config.{field} is missing or empty", StringComparison.Ordinal));
    }

    [Fact]
    public void ARecordWithAnEmptyFieldFails() =>
        Assert.Contains(Measurements.RecordProblems("sync-1", Record("sync-1", c => c["image"] = " ")), p => p.Contains("config.image", StringComparison.Ordinal));

    [Fact]
    public void ARecordWhoseIdIsNotItsFileNameFails() =>
        Assert.Contains(Measurements.RecordProblems("sync-2", Record("sync-1")), p => p.Contains("its id is not its file name", StringComparison.Ordinal));

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal) { "sync-1" };

    [Fact]
    public void AReportNumberWithNoCitationFails()
    {
        const string report = "| Measure | Value |\n|---|---|\n| sync median | 412 µs |\n";

        var problem = Assert.Single(Measurements.ReportProblems(report, Known));

        Assert.Contains("line 3: a number with no record or run cited", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AReportCitingAnUnknownRecordFails() =>
        Assert.Contains(Measurements.ReportProblems("| a | b |\n|---|---|\n| sync median | 412 µs (m:sync-9) |\n", Known), p => p.Contains("cites m:sync-9", StringComparison.Ordinal));

    [Fact]
    public void RowsCitingARecordOrARunPassAndHeadersAndProseAreNotHeld()
    {
        const string report = "Prose with 900 operations.\n\n| Shard 1 | Shard 2 |\n|:---|---:|\n| 412 µs (m:sync-1) | 789 s (run:37543529337) |\n| no numbers here | none |\n";

        Assert.Empty(Measurements.ReportProblems(report, Known));
    }
}
