using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raft.Core;
using Raft.Simulation;

namespace Raft.SimRun;

internal static class Program
{
    private static int Main(string[] args)
    {
        var a = args.ToList();
        if (a.Count == 0 || a[0] != "run")
        {
            Console.Error.WriteLine("usage: simrun run --seed N [--duration T] [--preset mix] [--trace FILE]");
            return 2;
        }

        var seed = ulong.Parse(Take(a, "--seed") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        var duration = long.Parse(Take(a, "--duration") ?? "600000", System.Globalization.CultureInfo.InvariantCulture);
        var traceFile = Take(a, "--trace");
        var preset = Take(a, "--preset");
        var schedule = preset switch
        {
            null => FaultSchedule.Empty,
            "mix" => Presets.Mix(duration),
            _ => throw new ArgumentException($"unknown preset '{preset}'"),
        };

        var sim = new Simulator(new SimulationConfig { Duration = duration }, ctx => new EchoCounterNode(ctx), seed, schedule);
        var trace = sim.Run();
        if (traceFile is not null)
        {
            File.WriteAllText(traceFile, trace.Text());
        }

        var result = EchoCounterChecks.Check(trace.Lines);
        Console.WriteLine($"seed={seed} steps={sim.Steps} sends={result.Sends} delivers={result.Delivers} persists={result.Persists} violations={result.Violations.Count}");
        foreach (var v in result.Violations)
        {
            Console.WriteLine("VIOLATION " + v);
        }

        return result.Ok ? 0 : 1;
    }

    private static string? Take(List<string> a, string name)
    {
        var i = a.IndexOf(name);
        if (i < 0 || i + 1 >= a.Count)
        {
            return null;
        }

        var v = a[i + 1];
        a.RemoveRange(i, 2);
        return v;
    }
}
