using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Raft.Checker;
using Raft.Core.Tests;
using Raft.Simulation;
using Xunit;

namespace Raft.Scale.Tests;

/// <summary>Probe branch only: the WGL search on one soak key at the budget in RAFT_PROBE_BUDGET, with its peak memory.</summary>
public sealed class ProbeBudget
{
    [Fact]
    public void Measure()
    {
        var budget = long.Parse(Environment.GetEnvironmentVariable("RAFT_PROBE_BUDGET") ?? "1000000", CultureInfo.InvariantCulture);
        var seed = int.Parse(Environment.GetEnvironmentVariable("RAFT_PROBE_SEED") ?? "7723", CultureInfo.InvariantCulture);
        var key = Environment.GetEnvironmentVariable("RAFT_PROBE_KEY") ?? "k3";
        var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = SoakTests.FaultsUntil });
        var (sim, _) = Cluster.Run((ulong)seed, SoakTests.Duration, schedule, SoakTests.Clients, new RaftWorkload(int.MaxValue, retry: true, think: SoakTests.Think));
        var sub = ClientHistory.From(sim.ClientLog).History.Where(o => o.Key == key).ToList();
        var sw = Stopwatch.StartNew();
        var r = WglChecker.Check(sub, budget);
        var peak = Process.GetCurrentProcess().PeakWorkingSet64 / 1_000_000;
        Console.WriteLine(FormattableString.Invariant($"PROBE seed {seed} {key} budget {budget}: {r.Verdict} {r.StatesExplored} states {sw.Elapsed.TotalSeconds:F1}s peak {peak} MB"));
        System.IO.File.AppendAllText("/tmp/probe.txt", FormattableString.Invariant($"seed {seed} {key} budget {budget}: {r.Verdict} {r.StatesExplored} states {sw.Elapsed.TotalSeconds:F1}s peak {peak} MB\n"));
    }
}
