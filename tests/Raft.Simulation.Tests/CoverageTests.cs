using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Raft.SimRun;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P1-10: the generated distribution, measured from what happened. Vacuity risk: counting the
/// schedule's intentions shows full coverage of faults the runner never applied; every dimension
/// is computed from trace effects, and S-cov-2 proves it (the runner drops a fault the schedule
/// still contains, and its dimension falls to zero). Sabotages S-cov-1..3.
/// </summary>
public sealed class CoverageTests
{
    private const int Executions = 200;

    private static readonly HashSet<string> AlwaysOn = new(StringComparer.Ordinal);

    private static readonly Dictionary<(string, string), string> AllowedIdentical = new();

    [Fact]
    public void TheGeneratedDistributionCoversEveryDimension()
    {
        var config = new GeneratorConfig { Duration = 20_000 };
        var hits = Coverage.Dimensions.ToDictionary(d => d, _ => new HashSet<int>(), StringComparer.Ordinal);
        var withPartition = 0;
        for (var seed = 1UL; seed <= Executions; seed++)
        {
            var schedule = FaultGenerator.Generate(seed, config);
            withPartition += schedule.Faults.Any(f => f is Partition) ? 1 : 0;
            var trace = new Simulator(new SimulationConfig { Duration = config.Duration }, ctx => new EchoCounterNode(ctx), seed, schedule).Run();
            foreach (var d in Coverage.Of(trace.Lines))
            {
                hits[d].Add((int)seed);
            }
        }

        var report = string.Join("\n", hits.Select(kv => $"  {kv.Key,-20} {kv.Value.Count,4} / {Executions}  ({100.0 * kv.Value.Count / Executions:F0}%)"))
            + $"\n  (schedules containing a partition: {withPartition} / {Executions})";
        var (failures, notes) = Coverage.Evaluate(hits.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<int>)kv.Value, StringComparer.Ordinal), Executions, AlwaysOn, AllowedIdentical);
        report += notes.Count == 0 ? "" : "\n  notes:\n    " + string.Join("\n    ", notes);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "coverage-report.txt"), report + "\n");

        Assert.True(failures.Count == 0, "coverage:\n" + report + "\nfailures:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void DimensionsComeFromEffectsNotFromTheSchedule()
    {
        NodeId n1 = new(1), n2 = new(2);
        // A torn crash that meets no write in flight, and a partition healed before any message crosses it.
        var schedule = new FaultSchedule([new Crash(5_000, n1, DiskLoss.Torn), new Restart(6_000, n1), new Partition(9_000, n1, n2), new Heal(9_000, n1, n2)]);
        var trace = new Simulator(new SimulationConfig { Duration = 20_000 }, ctx => new EchoCounterNode(ctx), 1, schedule).Run();
        var crash = trace.Lines.Single(l => l.Contains(" CRASH ", StringComparison.Ordinal));
        var hit = Coverage.Of(trace.Lines);

        Assert.Contains("crashed", hit);
        Assert.Equal(crash.Contains("kept=", StringComparison.Ordinal), hit.Contains("torn-record"));
        Assert.DoesNotContain("asymmetric-block", hit);
    }

    [Fact]
    public void TheRulesRejectZeroFullAndIdenticalSetsButNotCoincidentalCounts()
    {
        // Dimension i is hit by executions 0 .. 10+i-1 of 100: all distinct sets.
        IReadOnlySet<int> Range(int from, int count) => Enumerable.Range(from, count).ToHashSet();
        Dictionary<string, IReadOnlySet<int>> Base() => Coverage.Dimensions.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => Range(0, 10 + x.i), StringComparer.Ordinal);
        var none = new HashSet<string>(StringComparer.Ordinal);
        var noPairs = new Dictionary<(string, string), string>();
        IReadOnlyList<string> Fail(Dictionary<string, IReadOnlySet<int>> h, IReadOnlySet<string>? on = null, Dictionary<(string, string), string>? pairs = null) =>
            Coverage.Evaluate(h, 100, on ?? none, pairs ?? noPairs).Failures;

        Assert.Empty(Fail(Base()));
        var zero = Base(); zero["paused"] = Range(0, 0);
        Assert.Contains(Fail(zero), f => f.StartsWith("paused: 0", StringComparison.Ordinal));
        var full = Base(); full["paused"] = Range(0, 100);
        Assert.Contains(Fail(full), f => f.StartsWith("paused: 100%", StringComparison.Ordinal));
        Assert.Empty(Fail(full, new HashSet<string>(StringComparer.Ordinal) { "paused" }));

        var same = Base(); same["crashed"] = Range(50, 42); same["crash-lost-writes"] = Range(50, 42);
        Assert.Contains(Fail(same), f => f.Contains("exactly the same", StringComparison.Ordinal));
        Assert.Empty(Fail(same, pairs: new Dictionary<(string, string), string> { [("crashed", "crash-lost-writes")] = "reason" }));

        var coincidence = Base(); coincidence["dropped"] = Range(0, 42); coincidence["clock-skewed"] = Range(50, 42);
        var (failures, notes) = Coverage.Evaluate(coincidence, 100, none, noPairs);
        Assert.Empty(failures);
        Assert.Contains(notes, n => n.Contains("equal counts", StringComparison.Ordinal));
    }
}
