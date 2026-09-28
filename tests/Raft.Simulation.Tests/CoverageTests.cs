using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Raft.Core;
using Raft.Simulation;
using Raft.SimRun;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P1-10, restated in P2-02: the generated distribution, measured as effects the algorithm depends
/// on. Vacuity risk: a dimension named after a fault counts the injection and passes when the fault
/// fires but changes nothing, which is how three events sat at zero behind a green gate. Guarded:
/// every dimension is computed from effect lines; for each dimension a fault fires with no effect
/// and the dimension must stay unhit; and the traceability table's "stressed by" names must be
/// dimensions that clear the floor. Sabotages S-cov-1..6, S-rare-1/2.
/// </summary>
public sealed partial class CoverageTests
{
    private const int Executions = 200;
    private static readonly NodeId N1 = new(1), N2 = new(2);

    /// <summary>Dimensions at or above 95%, each with the reason the other case is rare.</summary>
    /// <summary>
    /// None today. "delivered-after-later-send" sits at 94% (P1's 189/200 on a second look: most of it
    /// is delay jitter on non-FIFO links, not the Reorder fault); if it crosses 95% the rule makes
    /// someone decide whether the generator should produce more of the other case.
    /// </summary>
    private static readonly Dictionary<string, string> AlwaysOn = new(StringComparer.Ordinal);

    private static readonly Dictionary<(string, string), string> AllowedIdentical = new();

    private static readonly Lazy<Dictionary<string, HashSet<int>>> Measured = new(() =>
    {
        var config = new GeneratorConfig { Duration = 20_000 };
        var hits = Coverage.Dimensions.ToDictionary(d => d, _ => new HashSet<int>(), StringComparer.Ordinal);
        for (var seed = 1UL; seed <= Executions; seed++)
        {
            var trace = new Simulator(new SimulationConfig { Duration = config.Duration }, ctx => new EchoCounterNode(ctx), seed, FaultGenerator.Generate(seed, config)).Run();
            foreach (var d in Coverage.Of(trace.Lines))
            {
                hits[d].Add((int)seed);
            }
        }

        return hits;
    });

    [GeneratedRegex(@"^\| (?<id>(F2|F13|S6|S8)-\d+) \|(?<rest>.*)\|\s*$")]
    private static partial Regex TableRow();

    [Fact]
    public void TheGeneratedDistributionCoversEveryDimension()
    {
        var hits = Measured.Value;
        var report = string.Join("\n", hits.Select(kv => $"  {kv.Key,-42} {kv.Value.Count,4} / {Executions}  ({100.0 * kv.Value.Count / Executions:F0}%)"));
        var (failures, notes) = Coverage.Evaluate(hits.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<int>)kv.Value, StringComparer.Ordinal), Executions, AlwaysOn, AllowedIdentical);
        report += notes.Count == 0 ? "" : "\n  notes:\n    " + string.Join("\n    ", notes);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "coverage-report.txt"), report + "\n");

        Assert.True(failures.Count == 0, "coverage:\n" + report + "\nfailures:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// The list of events is owned by the Figure 2 rules, not by the faults: every effect the
    /// traceability table says a rule depends on is a dimension, and the generated runs produce it.
    /// </summary>
    [Fact]
    public void EveryEffectTheTraceabilityTableNamesIsADimensionThatClearsTheFloor()
    {
        var table = File.ReadAllLines(RepoFile("docs/design/node-interface.md")).Select(l => TableRow().Match(l)).Where(m => m.Success).ToList();
        Assert.True(table.Count >= 50, $"only {table.Count} table rows parsed");
        var hits = Measured.Value;
        var problems = new List<string>();
        foreach (var m in table)
        {
            var id = m.Groups["id"].Value;
            var stressedBy = m.Groups["rest"].Value.Split('|')[^1].Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (stressedBy.Count == 0)
            {
                problems.Add($"{id}: names no effect");
            }

            foreach (var e in stressedBy.Where(e => !Coverage.Controls.Contains(e)))
            {
                if (!Coverage.Dimensions.Contains(e))
                {
                    problems.Add($"{id}: '{e}' is not a coverage dimension (effects, not fault names)");
                }
                else if (hits[e].Count < Coverage.Floor)
                {
                    problems.Add($"{id}: '{e}' is hit in only {hits[e].Count} of {Executions} executions");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Raft.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("no Raft.slnx above the test directory"), relative);
    }

    private static IReadOnlySet<string> Hits(out List<TraceLine> trace, params Fault[] faults)
    {
        var lines = new Simulator(new SimulationConfig { Duration = 20_000 }, ctx => new EchoCounterNode(ctx), 1, new FaultSchedule([new Fifo(0), .. faults])).Run().Lines;
        trace = TraceLine.Parse(lines);
        return Coverage.Of(lines);
    }

    private static long HeldAt(Fault reorder)
    {
        Hits(out var t, reorder);
        return t.First(l => l.Kind == "HELD").Time;
    }

    /// <summary>A fault fires and changes nothing: its effect dimension must stay unhit.</summary>
    public static TheoryData<string> Inert() => new(
        "delivered-later-than-normal-delay", "delivered-after-later-send", "unsynced-write-lost", "partial-record-left-on-disk",
        "one-way-reachability", "node-isolated-for-a-timeout", "step-after-silence-longer-than-a-timeout",
        "write-slower-than-normal-latency", "clock-rate-diverged", "lost-in-transit-to-a-live-receiver", "restarted-from-disk");

    [Theory]
    [MemberData(nameof(Inert))]
    public void AFaultThatFiresWithoutItsEffectDoesNotCount(string dimension)
    {
        var (faults, fired) = dimension switch
        {
            // A delay within the normal spread: delivered no later than an undelayed message could be.
            "delivered-later-than-normal-delay" => ((Fault[])[new Delay(5_000, N1, N2, 1)], "DELAYED"),
            // On FIFO links, a Reorder holds n1's next message to n2, and n1 crashes for good one unit
            // later (placed by a pre-run): no later message on the link ever overtakes the held one.
            "delivered-after-later-send" => ([new Reorder(5_000, N1, N2), new Crash(HeldAt(new Reorder(5_000, N1, N2)) + 1, N1, DiskLoss.Pending)], "HELD"),
            // Crashes that meet no write in flight (at time 50 no write has been issued yet).
            "unsynced-write-lost" => ([new Crash(50, N1, DiskLoss.Pending)], "CRASH"),
            "partial-record-left-on-disk" => ([new Crash(50, N1, DiskLoss.Torn)], "CRASH"),
            // A partition healed at the moment it starts blocks nothing.
            "one-way-reachability" => ([new Partition(5_000, N1, N2), new Heal(5_000, N1, N2)], "FAULT"),
            // Isolated for less than a timeout.
            "node-isolated-for-a-timeout" => ([new Isolate(5_000, N1, 5_050)], "BLOCKED"),
            // Paused for less than a timeout.
            "step-after-silence-longer-than-a-timeout" => ([new Pause(5_000, N1), new Unpause(5_050, N1)], "RESUME"),
            // A slow disk no slower than the normal latency.
            "write-slower-than-normal-latency" => ([new SlowDisk(5_000, N1, 3, 8_000)], "FAULT"),
            // A skew of one to one.
            "clock-rate-diverged" => ([new Skew(0, N1, 1, 1)], "FAULT"),
            // Messages to a crashed node are lost to a dead receiver, not withheld from a live one.
            "lost-in-transit-to-a-live-receiver" => ([new Crash(5_000, N2, DiskLoss.Pending)], "LOST"),
            // A state-placed crash whose state never comes (five writes in flight at once): unfired.
            "restarted-from-disk" => ([new CrashWhenInFlight(5_000, N1, 5, DiskLoss.Pending, 100)], "UNFIRED"),
            _ => throw new ArgumentException(dimension),
        };

        var hit = Hits(out var trace, faults);

        Assert.Contains(trace, l => l.Kind == fired);
        Assert.DoesNotContain(dimension, hit);
    }

    [Fact]
    public void TheThreeEventsPhaseOneNeverProducedAreReachable()
    {
        Assert.Contains("node-isolated-for-a-timeout", Hits(out _, new Isolate(5_000, N1, 8_000)));
        Assert.Contains("all-down", Hits(out _, new CrashAll(5_000, 6_000)));

        // Writes reordered at a crash need two in flight: a slow disk makes writes overlap (at 150
        // against a 100-tick period, two are in flight whenever the crash fires, so MinPending = 2
        // is not what places it here; the UNFIRED case above tests MinPending), and a Reordered
        // loss keeps a random subset. Some seed's subset keeps the later write and loses the earlier.
        var found = Enumerable.Range(1, 60).Any(seed =>
        {
            var lines = new Simulator(new SimulationConfig { Duration = 20_000 }, ctx => new EchoCounterNode(ctx), (ulong)seed,
                new FaultSchedule([new SlowDisk(0, N1, 150, 20_000), new CrashWhenInFlight(5_000, N1, 2, DiskLoss.Reordered, 500)])).Run().Lines;
            return Coverage.Of(lines).Contains("writes-completed-out-of-order-at-crash");
        });
        Assert.True(found, "no seed of 60 reordered writes across a crash with two writes in flight");
    }

    [Fact]
    public void TheRulesRejectTheFloorNearAlwaysAndIdenticalSetsButNotCoincidentalCounts()
    {
        // Dimension i is hit by executions 0 .. 10+i-1 of 100: all distinct sets, all in range.
        IReadOnlySet<int> Range(int from, int count) => Enumerable.Range(from, count).ToHashSet();
        Dictionary<string, IReadOnlySet<int>> Base() => Coverage.Dimensions.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => Range(0, 10 + x.i), StringComparer.Ordinal);
        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        var noPairs = new Dictionary<(string, string), string>();
        IReadOnlyList<string> Fail(Dictionary<string, IReadOnlySet<int>> h, Dictionary<string, string>? on = null, Dictionary<(string, string), string>? pairs = null, Dictionary<string, string>? rare = null) =>
            Coverage.Evaluate(h, 100, on ?? none, pairs ?? noPairs, rare).Failures;
        const string A = "majority-down", B = "all-down";

        Assert.Empty(Fail(Base()));
        var low = Base(); low[A] = Range(0, Coverage.Floor - 1);
        Assert.Contains(Fail(low), f => f.StartsWith(A + ": 2 of 100 — below the floor", StringComparison.Ordinal));
        Assert.Empty(Fail(low, rare: new Dictionary<string, string> { [A] = "reason" }));
        var near = Base(); near[A] = Range(0, 95);
        Assert.Contains(Fail(near), f => f.StartsWith(A + ": 95 of 100 (95% or more)", StringComparison.Ordinal));
        Assert.Empty(Fail(near, new Dictionary<string, string> { [A] = "reason" }));

        var same = Base(); same[A] = Range(50, 42); same[B] = Range(50, 42);
        Assert.Contains(Fail(same), f => f.Contains("exactly the same", StringComparison.Ordinal));
        Assert.Empty(Fail(same, pairs: new Dictionary<(string, string), string> { [(A, B)] = "reason" }));

        var coincidence = Base(); coincidence[A] = Range(0, 42); coincidence[B] = Range(50, 42);
        var (failures, notes) = Coverage.Evaluate(coincidence, 100, none, noPairs);
        Assert.Empty(failures);
        Assert.Contains(notes, n => n.Contains("equal counts", StringComparison.Ordinal));
    }
}
