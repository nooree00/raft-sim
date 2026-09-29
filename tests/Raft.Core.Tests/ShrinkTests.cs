using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P3-09: an election-safety failure shrunk end to end. The input is the positive control's lying
/// disk (P3-07) buried in generated noise; the shrinker must keep the failure's signature (the
/// property and where it failed, never "some failure"), end 1-minimal, end strictly smaller than its
/// input, and the shrunk schedule, written as text, must reproduce the identical trace in another
/// process. Vacuity risk: a shrink that "succeeds" because the input never failed, or because a
/// different failure was accepted; guarded by the shrinker refusing an input without the signature,
/// by the signature naming the node or term, and by the trace comparison. Sabotage S-shrink-5.
/// </summary>
public sealed class ShrinkTests
{
    private const long Duration = 3_000;
    private static readonly NodeId N1 = new(1), N2 = new(2), N3 = new(3);

    private static IReadOnlySet<string> SignaturesOf(ulong seed, FaultSchedule s) =>
        ElectionInvariants.Signatures(Cluster.Run(seed, Duration, s).History);

    private static FaultSchedule WithNoise(ulong seed, IEnumerable<Fault> construction) =>
        new(construction.Concat(FaultGenerator.Generate(seed, new GeneratorConfig { Duration = Duration }).Faults).OrderBy(f => f.At).ToList());

    private static (ulong Seed, FaultSchedule Input, string Signature, Shrinker.Result Result) ShrinkFirstFailing(Func<ulong, Fault[]> construction)
    {
        for (var seed = 1UL; seed <= 200; seed++)
        {
            var input = WithNoise(seed, construction(seed));
            var found = SignaturesOf(seed, input);
            var signature = found.FirstOrDefault(x => x.StartsWith("election-safety@", StringComparison.Ordinal))
                ?? found.FirstOrDefault(x => x == "vote-uniqueness@n2");
            if (signature is not null)
            {
                var s = seed;
                return (seed, input, signature, Shrinker.Shrink(input, signature, x => SignaturesOf(s, x)));
            }
        }

        throw new InvalidOperationException("no seed in 1..200 turned the construction with noise red");
    }

    [Fact]
    public void ALyingDiskInNoiseShrinksToAOneMinimalScheduleThatReproducesInAnotherProcess()
    {
        var state = ShrinkFirstFailing(_ => [new CrashAfterWrite(0, N2, DiskLoss.LoseSynced, 20), new Partition(0, N1, N3), new Partition(0, N3, N1)]);
        var timed = ShrinkFirstFailing(seed =>
        {
            var at = 150 + ((long)seed * 37 % 300);
            return [new Crash(at, N2, DiskLoss.LoseSynced), new Restart(at + 20, N2), new Partition(0, N1, N3), new Partition(0, N3, N1)];
        });

        var lines = new List<string>();
        foreach (var (name, (seed, input, signature, result)) in new[] { ("state-placed", state), ("time-placed", timed) })
        {
            Assert.True(result.OneMinimal, $"{name}: the shrunk schedule is not 1-minimal");
            Assert.True(result.Schedule.Faults.Count < input.Faults.Count, $"{name}: {result.Schedule.Faults.Count} events from {input.Faults.Count}, not smaller");
            Assert.Contains(signature, SignaturesOf(seed, result.Schedule));
            lines.Add($"{name}: seed {seed}, {signature}, {input.Faults.Count} events -> {result.Schedule.Faults.Count} in {result.Runs} runs; order-sensitive pairs {result.OrderSensitive.Count}");
            lines.AddRange(result.Schedule.Faults.Select(f => "    " + ScheduleText.Line(f)));
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "shrink.txt"), string.Join("\n", lines) + "\n");

        // The state-placed result, as text, in another process: the identical trace.
        var header = new ScheduleText.Header(state.Seed, Duration, Cluster.Nodes, "raft-default", "p3-09");
        var repro = Temp();
        File.WriteAllText(repro, ScheduleText.Write(header, state.Result.Schedule));
        var (_, again) = ScheduleText.Read(File.ReadAllText(repro));
        var here = Cluster.Run(state.Seed, Duration, again).Sim.Trace.Text();
        var traceFile = Temp();
        var (code, output) = SimRun("run", "--schedule", repro, "--raft", "--trace", traceFile);
        Assert.True(code == 0, output);
        Assert.Equal(here, File.ReadAllText(traceFile));
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), $"shrink-{Guid.NewGuid():N}");

    private static (int Code, string Output) SimRun(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(typeof(Raft.SimRun.EchoCounterNode).Assembly.Location);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}
