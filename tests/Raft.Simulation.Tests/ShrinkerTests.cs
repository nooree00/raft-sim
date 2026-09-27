using System.Collections.Generic;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Raft.SimRun;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P1-11: the shrinker, proven on planted bugs. Vacuity risk: a shrinker that accepts any failing
/// candidate converges on whatever fails most easily — guarded by a second planted bug with a
/// different signature in the same schedule, and by checking 1-minimality here rather than trusting
/// the shrinker's own flag. The ordering case (added at review): a bug caused by the order of two
/// faults. Sabotages S-shrink-1..3.
/// </summary>
public sealed class ShrinkerTests
{
    private static readonly NodeId N1 = new(1), N2 = new(2);
    private const long End = 20_000;

    /// <summary>
    /// The environment every run gets outside the schedule being shrunk: FIFO links, so gaps,
    /// repeats and regressions come only from faults, never from delay jitter.
    /// </summary>
    private static readonly Fault Environment = new Fifo(0);

    private static List<TraceLine> Trace(Bugs bugs, IEnumerable<Fault> faults) =>
        TraceLine.Parse(new Simulator(new SimulationConfig { Duration = End }, ctx => new PlantedNode(ctx, bugs), 5,
            new FaultSchedule([Environment, .. faults.OrderBy(f => f.At)])).Run().Lines);

    private static HashSet<string> Signatures(Bugs bugs, IEnumerable<Fault> faults)
    {
        var lines = new Simulator(new SimulationConfig { Duration = End }, ctx => new PlantedNode(ctx, bugs), 5,
            new FaultSchedule([Environment, .. faults])).Run().Lines;
        return EchoCounterChecks.Check(lines).Violations.Select(v => v.Signature).ToHashSet();
    }

    /// <summary>A generated schedule of at least 100 events, at eight times the default rates.</summary>
    private static List<Fault> Noise(ulong seed)
    {
        var config = new GeneratorConfig
        {
            Duration = End, Drop = 800, Duplicate = 800, Delay = 800, Reorder = 800, Partition = 1_600,
            Crash = 1_600, Pause = 1_200, SlowDisk = 1_200,
        };
        return FaultGenerator.Generate(seed, config).Faults.Where(f => f is not Fifo).ToList();
    }

    private static void AssertOneMinimal(FaultSchedule s, System.Func<IEnumerable<Fault>, bool> fails)
    {
        Assert.True(fails(s.Faults));
        for (var i = 0; i < s.Faults.Count; i++)
        {
            Assert.False(fails(s.Faults.Where((_, j) => j != i)), $"removing {s.Faults[i]} still fails: not 1-minimal");
        }
    }

    [Fact]
    public void ASyntheticFailureShrinksToExactlyItsCause()
    {
        var faults = Enumerable.Range(0, 100).Select(t => (Fault)new Drop(t, N1, N2)).ToList();
        IReadOnlySet<string> Sig(FaultSchedule s) =>
            s.Faults.Any(f => f.At == 17) && s.Faults.Any(f => f.At == 63) ? new HashSet<string> { "p@n1" } : new HashSet<string>();

        var r = Shrinker.Shrink(new FaultSchedule(faults), "p@n1", Sig);

        Assert.Equal([17L, 63L], r.Schedule.Faults.Select(f => f.At));
        Assert.True(r.OneMinimal);
        Assert.Empty(r.OrderSensitive);
    }

    [Fact]
    public void AMissingSignatureIsRefused() =>
        Assert.Throws<System.ArgumentException>(() => Shrinker.Shrink(new FaultSchedule([new Drop(1, N1, N2)]), "p@n1", _ => new HashSet<string>()));

    [Fact]
    public void ParametersShrinkToTheSimplestValueThatStillFails()
    {
        // Fails iff a crash is present and the extra delay is at least 10.
        Fault[] faults = [new Crash(5, N1, DiskLoss.Reordered), new Restart(9, N1), new Delay(7, N1, N2, 200)];
        IReadOnlySet<string> Sig(FaultSchedule s) =>
            s.Faults.OfType<Crash>().Any() && s.Faults.OfType<Delay>().Any(d => d.Extra >= 10) ? new HashSet<string> { "p@n1" } : new HashSet<string>();

        var r = Shrinker.Shrink(new FaultSchedule(faults), "p@n1", Sig);

        Assert.Equal(2, r.Schedule.Faults.Count);
        Assert.Equal(DiskLoss.Pending, Assert.Single(r.Schedule.Faults.OfType<Crash>()).Loss);
        Assert.InRange(Assert.Single(r.Schedule.Faults.OfType<Delay>()).Extra, 10, 19);
    }

    /// <summary>
    /// The planted duplicate-then-crash bug at n2, placed inside a 100-plus-event schedule: the
    /// duplicate goes first, and the crash is put one unit after the persist that the bug sends
    /// ahead of — found by a pre-run, since faults at later times cannot change the run before them.
    /// </summary>
    private static (List<Fault> Schedule, Fault[] Plant) PlantDuplicateThenCrash(List<Fault> noise)
    {
        foreach (var td in new long[] { 12_000, 13_000, 14_000, 15_000, 16_000 })
        {
            var dup = new Duplicate(td, N1, N2);
            var t = Trace(Bugs.DuplicateThenCrash, [.. noise, dup]);
            var dupLine = t.FirstOrDefault(l => l.Kind == "DUP" && l.Time >= td && l["to"] == "n2");
            if (dupLine is null)
            {
                continue;
            }

            var second = t.Where(l => l.Kind == "DELIVER" && l.Node == "n2" && l["id"] == dupLine["id"]).Skip(1).FirstOrDefault();
            var persist = second is null ? null : t.FirstOrDefault(l => l.Node == "n2" && l.Kind == "PERSIST" && l.Time > second.Time);
            if (persist is null || persist["op"] != "PersistAppend")
            {
                continue;
            }

            Fault[] plant = [dup, new Crash(persist.Time + 1, N2, DiskLoss.Reordered), new Restart(persist.Time + 500, N2)];
            var planted = noise.Concat(plant).OrderBy(f => f.At).ToList();
            if (Signatures(Bugs.DuplicateThenCrash | Bugs.RegressionEcho, planted).Contains("durability@n2"))
            {
                return (planted, plant);
            }
        }

        throw new Xunit.Sdk.XunitException("no placement of the planted bug fails; the plant is broken");
    }

    [Fact]
    public void ThePlantedTwoFaultBugShrinksToAHandfulOfEventsContainingBoth()
    {
        const Bugs bugs = Bugs.DuplicateThenCrash | Bugs.RegressionEcho;
        var noise = Noise(3);
        Assert.DoesNotContain("durability@n2", Signatures(bugs, noise));
        var (schedule, _) = PlantDuplicateThenCrash(noise);
        Assert.True(schedule.Count >= 100, $"only {schedule.Count} events");
        var before = Signatures(bugs, schedule);
        Assert.Contains(before, s => s.StartsWith("echo-valid@", System.StringComparison.Ordinal)); // the second bug is present

        var r = Shrinker.Shrink(new FaultSchedule(schedule), "durability@n2", s => Signatures(bugs, s.Faults));

        Assert.True(r.Schedule.Faults.Count is >= 2 and <= 5, $"{r.Runs} runs: " + string.Join("; ", r.Schedule.Faults));
        Assert.Contains(r.Schedule.Faults, f => f is Duplicate { To.Value: 2 });
        var crash = r.Schedule.Faults.OfType<Crash>().Last(c => c.Node == N2);
        Assert.True(crash.Loss == DiskLoss.Pending, $"{r.Runs} runs: " + string.Join("; ", r.Schedule.Faults)); // shrunk from Reordered
        AssertOneMinimal(r.Schedule, f => Signatures(bugs, f).Contains("durability@n2"));
        Assert.True(r.OneMinimal);
    }

    /// <summary>
    /// Noise that cannot itself produce a gap or a repeat on the n1–n2 pair: no link faults between
    /// them and no crashes of either (a restarted node forgets what it saw, so its next message is a gap).
    /// </summary>
    private static List<Fault> NoiseAwayFrom(ulong seed) => Noise(seed).Where(f => f switch
    {
        LinkFault l => !((l.From == N1 && l.To == N2) || (l.From == N2 && l.To == N1)),
        Crash c => c.Node != N1 && c.Node != N2,
        Restart r => r.Node != N1 && r.Node != N2,
        _ => true,
    }).ToList();

    [Fact]
    public void TheOrderingCaseShrinksToBothFaultsInOrderAndTheSwapProbeMarksThem()
    {
        const Bugs bugs = Bugs.GapThenRepeat;
        var noise = NoiseAwayFrom(4);
        Fault drop = new Drop(8_000, N1, N2), dup = new Duplicate(9_000, N1, N2);
        bool Fails(IEnumerable<Fault> f) => Signatures(bugs, f.OrderBy(x => x.At)).Contains("echo-valid@n1");

        // The case as planted: fails only with the drop first; either alone, or swapped, passes.
        Assert.True(Fails([.. noise, drop, dup]));
        Assert.False(Fails([.. noise, drop]));
        Assert.False(Fails([.. noise, dup]));
        Assert.False(Fails([.. noise, drop with { At = 9_000 }, dup with { At = 8_000 }]));

        var schedule = noise.Append(drop).Append(dup).OrderBy(f => f.At).ToList();
        var r = Shrinker.Shrink(new FaultSchedule(schedule), "echo-valid@n1", s => Signatures(bugs, s.Faults));

        Assert.Equal([drop, dup], r.Schedule.Faults);
        Assert.Equal([(0, 1)], r.OrderSensitive);
        AssertOneMinimal(r.Schedule, Fails);
    }

    [Fact]
    public void TheSwapProbeLeavesOrderIndependentPairsUnmarked()
    {
        // Fails iff both faults are present, in either order.
        Fault[] faults = [new Drop(10, N1, N2), new Duplicate(20, N2, N1)];
        IReadOnlySet<string> Sig(FaultSchedule s) => s.Faults.Count == 2 ? new HashSet<string> { "p@n1" } : new HashSet<string>();

        var r = Shrinker.Shrink(new FaultSchedule(faults), "p@n1", Sig);

        Assert.Equal(2, r.Schedule.Faults.Count);
        Assert.Empty(r.OrderSensitive);
    }
}
