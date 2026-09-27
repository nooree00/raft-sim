using System;
using System.Linq;
using Raft.Simulation;
using Raft.SimRun;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P1-04: three echo-counter nodes, ten simulated minutes, no faults. Vacuity risk: a protocol that
/// sends nothing makes every later fault test act on nothing; so the counts of sends, deliveries
/// and persists are asserted, from the trace. Sabotages S-sim-1 (a message delivered twice with no
/// fault), S-sim-2 (the persist barrier ignored).
/// </summary>
public sealed class RunnerTests
{
    private static (Simulator Sim, EchoCounterChecks.Result Result) Run(ulong seed = 11, long duration = 600_000)
    {
        var sim = new Simulator(new SimulationConfig { Duration = duration }, ctx => new EchoCounterNode(ctx), seed);
        var trace = sim.Run();
        return (sim, EchoCounterChecks.Check(trace.Lines));
    }

    [Fact]
    public void TenSimulatedMinutesMoveRealTraffic()
    {
        var (_, r) = Run();

        // 3 nodes × 6,000 periods × 2 peers announcements, each echoed: about 72,000 sends.
        Assert.InRange(r.Sends, 70_000, 74_000);
        Assert.InRange(r.Persists, 17_900, 18_100);
        Assert.Equal(3, r.Starts);
        Assert.True(r.Ok, string.Join("\n", r.Violations));
    }

    [Fact]
    public void WithNoFaultsEveryMessageIsDeliveredExactlyOnce()
    {
        var sim = new Simulator(new SimulationConfig { Duration = 60_000 }, ctx => new EchoCounterNode(ctx), 3);
        var trace = sim.Run().Lines;
        string Id(string l) => l.Split(' ').First(p => p.StartsWith("id=", StringComparison.Ordinal));

        var sent = trace.Where(l => l.Split(' ')[2] == "SEND").Select(Id).ToList();
        var delivered = trace.Where(l => l.Split(' ')[2] == "DELIVER").Select(Id).ToList();

        Assert.NotEmpty(sent);
        Assert.Equal(sent.Count, sent.Distinct().Count());
        Assert.Equal(delivered.Count, delivered.Distinct().Count());
        // Messages still in flight at the end of the run are the only ones undelivered.
        Assert.InRange(sent.Count - delivered.Count, 0, 12);
        Assert.Empty(delivered.Except(sent));
    }

    [Fact]
    public void EveryAnnouncedValueWasDurableAtItsSenderBeforeAPeerReceivedIt()
    {
        // Each increment is one persist, so when a peer receives value k from X, X must already
        // have k durable writes: the persist barrier, checked directly from the trace.
        var sim = new Simulator(new SimulationConfig { Duration = 60_000 }, ctx => new EchoCounterNode(ctx), 5);
        var durable = new System.Collections.Generic.Dictionary<string, long>(StringComparer.Ordinal);
        var checkedReceipts = 0;
        foreach (var l in sim.Run().Lines)
        {
            var p = l.Split(' ');
            if (p[2] == "DURABLE")
            {
                durable[p[1]] = durable.TryGetValue(p[1], out var d) ? d + 1 : 1;
            }
            else if (p[2] == "EVENT" && p[3] == "name=got")
            {
                var from = p[4]["from=".Length..];
                var value = long.Parse(p[5]["value=".Length..], System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(durable.TryGetValue(from, out var have) && have >= value, $"{l}: {from} had {have} durable writes");
                checkedReceipts++;
            }
        }

        Assert.True(checkedReceipts > 1_000, $"only {checkedReceipts} receipts checked");
    }

    /// <summary>
    /// The cost of ten simulated minutes, bounded by what drives it: steps, about one per tick per
    /// node. Not wall time — a wall-clock bound failed under the sabotage harness's parallel load
    /// (10.1–11.4 s against 1.6 s alone; docs/findings.md).
    /// </summary>
    [Fact]
    public void TenSimulatedMinutesCostAboutOneStepPerTickPerNode()
    {
        var (sim, _) = Run(seed: 12);

        Assert.InRange(sim.Steps, 3 * 600_000, 3 * 660_000);
    }
}
