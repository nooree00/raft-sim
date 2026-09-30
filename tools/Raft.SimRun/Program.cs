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
            Console.Error.WriteLine("usage: simrun run (--seed N [--duration T] [--preset mix | --generate [--controls]] | --schedule FILE) [--raft] [--trace FILE]");
            return 2;
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var traceFile = Take(a, "--trace");
        var raft = a.Remove("--raft");
        var scheduleFile = Take(a, "--schedule");
        ScheduleText.Header header;
        FaultSchedule schedule;
        if (scheduleFile is not null)
        {
            (header, schedule) = ScheduleText.Read(File.ReadAllText(scheduleFile));
        }
        else
        {
            var seed = ulong.Parse(Take(a, "--seed") ?? "1", inv);
            var duration = long.Parse(Take(a, "--duration") ?? "600000", inv);
            var preset = Take(a, "--preset");
            var generate = a.Remove("--generate");
            var controls = a.Remove("--controls");
            var config = new GeneratorConfig { Duration = duration, Controls = controls };
            schedule = generate ? FaultGenerator.Generate(seed, config)
                : preset == "mix" ? Presets.Mix(duration)
                : preset is null ? FaultSchedule.Empty
                : throw new ArgumentException($"unknown preset '{preset}'");
            header = new ScheduleText.Header(seed, duration, config.Nodes, generate ? config.Hash() : "none", Commit());
        }

        NodeFactory factory = raft ? ctx => new RaftNode(ctx, RaftOptions.Default, new Raft.Kv.KvStateMachine()) : ctx => new EchoCounterNode(ctx);
        var sim = new Simulator(new SimulationConfig { Duration = header.Duration, Nodes = header.Nodes }, factory, header.Seed, schedule);
        var trace = sim.Run();
        if (traceFile is not null)
        {
            File.WriteAllText(traceFile, trace.Text());
        }

        if (raft)
        {
            // Raft's invariants are checked over observations, in Raft.Scale.Tests; here the run is
            // reproduced, and its trace is the evidence that it is the same run (P3-09).
            Console.WriteLine($"seed={header.Seed} faults={schedule.Faults.Count} steps={sim.Steps} raft");
            return 0;
        }

        var result = EchoCounterChecks.Check(trace.Lines);
        Console.WriteLine($"seed={header.Seed} faults={schedule.Faults.Count} steps={sim.Steps} sends={result.Sends} delivers={result.Delivers} persists={result.Persists} violations={result.Violations.Count}");
        foreach (var v in result.Violations)
        {
            Console.WriteLine("VIOLATION " + v);
        }

        if (!result.Ok)
        {
            // The reproduction artifact (spec §7): rerun with `simrun run --schedule FILE`.
            Console.WriteLine("REPRODUCE-BEGIN");
            Console.Write(ScheduleText.Write(header, schedule));
            Console.WriteLine("REPRODUCE-END");
        }

        return result.Ok ? 0 : 1;
    }

    /// <summary>The commit being run, for the reproduction artifact; "unknown" outside a checkout.</summary>
    private static string Commit()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD") { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var sha = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && sha.Length == 40 ? sha : "unknown";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
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
