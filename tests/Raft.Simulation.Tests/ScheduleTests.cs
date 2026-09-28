using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Raft.SimRun;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P1-09: seed → schedule → runner, the text form, and the reproduction round trip exercised on
/// purpose. Vacuity risk: a runner that parses a field and ignores it passes every test using
/// generated schedules; guarded by a per-kind test that a schedule holding only that fault shows
/// its effect. Sabotages S-sched-1..3.
/// </summary>
public sealed class ScheduleTests
{
    private static readonly NodeId N1 = new(1), N2 = new(2), N3 = new(3);

    private static readonly Fault[] OneOfEach =
    [
        new Drop(10, N1, N2), new Duplicate(20, N2, N3), new Delay(30, N3, N1, 77), new Reorder(40, N1, N3),
        new Partition(50, N2, N1), new Heal(60, N2, N1), new Crash(70, N3, DiskLoss.Torn), new Restart(80, N3),
        new SlowDisk(90, N1, 33, 190), new BarrierViolation(100, N2, 200), new Pause(110, N3), new Unpause(120, N3),
        new Skew(0, N1, 11, 10), new Fifo(0), new CrashWhenInFlight(130, N2, 2, DiskLoss.Reordered, 400),
        new Isolate(140, N3, 900), new CrashAll(150, 700), new CrashMajority(160, 800),
    ];

    private static string Run(FaultSchedule s, ulong seed, long duration) =>
        new Simulator(new SimulationConfig { Duration = duration }, ctx => new EchoCounterNode(ctx), seed, s).Run().Text();

    [Fact]
    public void EveryFaultKindSurvivesTheTextRoundTrip()
    {
        var header = new ScheduleText.Header(9, 1_000, 3, "abc", "def");
        var text = ScheduleText.Write(header, new FaultSchedule(OneOfEach));
        var (h, parsed) = ScheduleText.Read(text);

        Assert.Equal(header, h);
        Assert.Equal(OneOfEach, parsed.Faults);
        Assert.Equal(OneOfEach.Length, typeof(Fault).Assembly.GetTypes().Count(t => t.IsSubclassOf(typeof(Fault)) && !t.IsAbstract));
    }

    [Fact]
    public void AParsedScheduleRunsExactlyLikeTheOriginal()
    {
        var original = Presets.Mix(20_000);
        var (_, parsed) = ScheduleText.Read(ScheduleText.Write(new ScheduleText.Header(7, 20_000, 3, "x", "y"), original));

        Assert.Equal(Run(original, 7, 20_000), Run(parsed, 7, 20_000));
    }

    [Fact]
    public void TheGeneratorIsDeterministicAndSensitiveToSeedAndConfig()
    {
        var config = new GeneratorConfig();
        string Text(ulong seed, GeneratorConfig c) => ScheduleText.Write(new ScheduleText.Header(seed, c.Duration, c.Nodes, c.Hash(), "-"), FaultGenerator.Generate(seed, c));

        Assert.Equal(Text(1, config), Text(1, config));
        Assert.NotEqual(Text(1, config), Text(2, config));
        Assert.NotEqual(config.Hash(), (config with { Drop = config.Drop + 1 }).Hash());
        Assert.True(FaultGenerator.Generate(1, config).Faults.Count > 20);
    }

    public static TheoryData<string> Kinds() => new(OneOfEach.Select(f => f.GetType().Name).Where(k => k is not ("Heal" or "Restart" or "Unpause")));

    /// <summary>A schedule holding only one kind of fault shows that kind's effect in the trace.</summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void EachFaultKindHasAnEffectWhenRunFromItsTextForm(string kind)
    {
        Fault[] faults = kind switch
        {
            "Drop" => [new Drop(5_000, N1, N2)],
            "Duplicate" => [new Duplicate(5_000, N1, N2)],
            "Delay" => [new Delay(5_000, N1, N2, 300)],
            "Reorder" => [new Reorder(5_000, N1, N2)],
            "Partition" => [new Partition(5_000, N1, N2), new Heal(8_000, N1, N2)],
            "Crash" => [new Crash(5_000, N1, DiskLoss.Pending), new Restart(6_000, N1)],
            "SlowDisk" => [new SlowDisk(5_000, N1, 150, 8_000)],
            "BarrierViolation" => [new SlowDisk(0, N1, 50, 20_000), new BarrierViolation(5_000, N1, 8_000)],
            "Pause" => [new Pause(5_000, N1), new Unpause(7_000, N1)],
            "Skew" => [new Skew(0, N1, 12, 10)],
            "Fifo" => [new Fifo(0)],
            "CrashWhenInFlight" => [new CrashWhenInFlight(5_000, N1, 1, DiskLoss.Pending, 1_000)],
            "Isolate" => [new Isolate(5_000, N1, 8_000)],
            "CrashAll" => [new CrashAll(5_000, 6_000)],
            "CrashMajority" => [new CrashMajority(5_000, 6_000)],
            _ => throw new ArgumentException(kind),
        };
        // Jitter reorders only when two messages on a link fall within a few units of each other,
        // which depends on the nodes' random phases: for FIFO, use a seed whose baseline does reorder.
        var seed = kind != "Fifo" ? 3UL : Enumerable.Range(1, 100).Select(i => (ulong)i).First(sd =>
            Coverage.Of(Run(FaultSchedule.Empty, sd, 20_000).Split('\n').Where(l => l.Length > 0).ToList()).Contains("delivered-after-later-send"));
        var (_, parsed) = ScheduleText.Read(ScheduleText.Write(new ScheduleText.Header(3, 20_000, 3, "x", "y"), new FaultSchedule(faults)));
        var t = TraceLine.Parse(Run(parsed, seed, 20_000).Split('\n').Where(l => l.Length > 0));
        var baseline = TraceLine.Parse(Run(FaultSchedule.Empty, seed, 20_000).Split('\n').Where(l => l.Length > 0));
        int Count(List<TraceLine> tr, string k) => tr.Count(l => l.Kind == k);

        var effect = kind switch
        {
            "Drop" => Count(t, "DROP") == 1,
            "Duplicate" => Count(t, "DUP") == 1,
            "Delay" => Count(t, "DELAYED") == 1,
            "Reorder" => Count(t, "HELD") == 1,
            "Partition" => Count(t, "BLOCKED") > 10,
            "Crash" => Count(t, "CRASH") == 1 && Count(t, "START") == 4,
            // A slow disk shows as writes that take at least its latency.
            "SlowDisk" => MaxWriteLatency(t) >= 150 && MaxWriteLatency(baseline) < 150,
            // Violating the barrier shows as sends while writes are in flight.
            "BarrierViolation" => SendsWhileWriting(t) > 0,
            "Pause" => Count(t, "RESUME") == 1 && Count(t, "BACKLOG") > 0,
            "Skew" => t.Count(l => l.Node == "n1" && l.Kind == "PERSIST") > baseline.Count(l => l.Node == "n1" && l.Kind == "PERSIST") * 11 / 10,
            // FIFO links: no reordering at all, where the jittered baseline reorders.
            // Placed by state: the crash lands with a write in flight, which a time-placed one rarely does.
            "CrashWhenInFlight" => Count(t, "FIRED") == 1 && Coverage.Of(t.Select(Line).ToList()).Contains("unsynced-write-lost"),
            "Isolate" => Coverage.Of(t.Select(Line).ToList()).Contains("node-isolated-for-a-timeout"),
            "CrashAll" => Coverage.Of(t.Select(Line).ToList()).Contains("all-down") && Count(t, "START") == 6,
            "CrashMajority" => Coverage.Of(t.Select(Line).ToList()).Contains("majority-down") && Count(t, "CRASH") == 2,
            "Fifo" => !Coverage.Of(t.Select(Line).ToList()).Contains("delivered-after-later-send") && Coverage.Of(baseline.Select(Line).ToList()).Contains("delivered-after-later-send"),
            _ => false,
        };
        Assert.True(effect, $"{kind}: no effect in the trace of its parsed schedule; coverage with [{string.Join(",", Coverage.Of(t.Select(Line).ToList()))}], without [{string.Join(",", Coverage.Of(baseline.Select(Line).ToList()))}]");
    }

    private static string Line(TraceLine l) =>
        $"{l.Time:D10} {l.Node} {l.Kind} " + string.Join(' ', l.Fields.Select(kv => $"{kv.Key}={kv.Value}"));

    private static long MaxWriteLatency(List<TraceLine> t)
    {
        var issued = new Dictionary<(string, long), long>();
        long max = 0;
        foreach (var l in t)
        {
            if (l.Kind == "PERSIST")
            {
                issued[(l.Node, l.Long("seq"))] = l.Time;
            }
            else if (l.Kind == "DURABLE" && issued.TryGetValue((l.Node, l.Long("seq")), out var at))
            {
                max = Math.Max(max, l.Time - at);
            }
        }

        return max;
    }

    private static int SendsWhileWriting(List<TraceLine> t)
    {
        var inFlight = 0;
        var count = 0;
        foreach (var l in t.Where(l => l.Node == "n1"))
        {
            inFlight += l.Kind switch { "PERSIST" => 1, "DURABLE" => -1, _ => 0 };
            if (l.Kind == "SEND" && inFlight > 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The round trip, on purpose (spec §7): a failing run prints its reproduction; that text,
    /// written to a file and run in a second process, gives the identical failing trace.
    /// </summary>
    [Fact]
    public void APrintedReproductionReproducesTheFailureExactlyInAnotherProcess()
    {
        var seed = Enumerable.Range(1, 200).Select(i => (ulong)i).First(s =>
            !EchoCounterChecks.Check(new Simulator(new SimulationConfig { Duration = 30_000 }, ctx => new EchoCounterNode(ctx), s,
                FaultGenerator.Generate(s, new GeneratorConfig { Duration = 30_000, Controls = true })).Run().Lines).Ok);

        var firstTrace = Temp();
        var (code1, out1) = SimRun("run", "--seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture), "--duration", "30000", "--generate", "--controls", "--trace", firstTrace);
        Assert.Equal(1, code1);
        var begin = out1.IndexOf("REPRODUCE-BEGIN\n", StringComparison.Ordinal);
        var end = out1.IndexOf("REPRODUCE-END", StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "a failing run must print its reproduction");
        var repro = Temp();
        File.WriteAllText(repro, out1[(begin + "REPRODUCE-BEGIN\n".Length)..end]);

        var secondTrace = Temp();
        var (code2, out2) = SimRun("run", "--schedule", repro, "--trace", secondTrace);

        Assert.Equal(1, code2);
        Assert.Contains("VIOLATION", out2, StringComparison.Ordinal);
        Assert.Equal(File.ReadAllText(firstTrace), File.ReadAllText(secondTrace));
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), $"sched-{Guid.NewGuid():N}");

    private static (int Code, string Output) SimRun(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(typeof(EchoCounterNode).Assembly.Location);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
