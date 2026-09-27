using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Raft.Simulation;
using Raft.SimRun;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P1-08: the same run in two separate processes produces byte-identical traces (per-process
/// hash randomization is invisible to an in-process repeat); a committed golden trace guards
/// against drift across SDK patches; different seeds give different traces. Vacuity risk: a trace
/// too coarse to differ — guarded by seed sensitivity and by requiring every fault's effect in it.
/// Sabotages S-det-1..4 (S-det-4 is expected to survive: P1-08's prediction).
/// </summary>
public sealed class DeterminismTests
{
    private const long GoldenDuration = 3_000;

    private static string SimRun => typeof(EchoCounterNode).Assembly.Location;

    private static byte[] RunInProcess(ulong seed, long duration)
    {
        var path = Path.Combine(Path.GetTempPath(), $"simrun-{Guid.NewGuid():N}.trace");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { SimRun, "run", "--seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture), "--duration", duration.ToString(System.Globalization.CultureInfo.InvariantCulture), "--preset", "mix", "--trace", path })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(File.Exists(path), $"simrun wrote no trace: exit {p.ExitCode}\n{output}");
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    private static string Text(ulong seed, long duration) =>
        new Simulator(new SimulationConfig { Duration = duration }, ctx => new EchoCounterNode(ctx), seed, Presets.Mix(duration)).Run().Text();

    [Fact]
    public void TwoSeparateProcessesProduceByteIdenticalTraces()
    {
        var first = RunInProcess(7, 20_000);
        var second = RunInProcess(7, 20_000);

        Assert.True(first.Length > 100_000, $"trace of {first.Length} bytes is too small to mean anything");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(first)), Convert.ToHexString(SHA256.HashData(second)));
        Assert.Equal(System.Text.Encoding.UTF8.GetString(first), Text(7, 20_000));
    }

    [Fact]
    public void TheTraceShowsEveryFaultsEffect()
    {
        var kinds = Text(7, 20_000).Split('\n').Where(l => l.Length > 0).Select(l => l.Split(' ')[2]).ToHashSet(StringComparer.Ordinal);

        foreach (var k in new[] { "DROP", "DUP", "DELAYED", "HELD", "BLOCKED", "CRASH", "BACKLOG", "RESUME", "PERSIST", "DURABLE", "LOST" })
        {
            Assert.Contains(k, kinds);
        }
    }

    [Fact]
    public void DifferentSeedsGiveDifferentTraces() =>
        Assert.NotEqual(Text(7, 20_000), Text(8, 20_000));

    [Fact]
    public void TheGoldenTraceIsReproducedExactly()
    {
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "seed7-mix-3000.trace"));
        var actual = Text(7, GoldenDuration);

        Assert.True(golden == actual,
            "the golden trace changed: a reproducibility break. If intended, regenerate golden/seed7-mix-3000.trace in the same commit and say why.");
    }
}
