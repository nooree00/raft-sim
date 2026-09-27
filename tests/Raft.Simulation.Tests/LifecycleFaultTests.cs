using System.Collections.Generic;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Raft.SimRun;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P1-07: crash, restart, pause and clock skew, proven by effect. Vacuity risk: a pause implemented
/// as crash-and-restart passes every test that looks only at durable state; so the echo count — a
/// volatile counter — must survive a pause and must not survive a crash. Sabotages S-life-1..3.
/// </summary>
public sealed class LifecycleFaultTests
{
    private static readonly NodeId N1 = new(1), N2 = new(2);

    private static (List<TraceLine> Trace, Simulator Sim) Run(long end, params Fault[] faults)
    {
        var sim = new Simulator(new SimulationConfig { Duration = end }, ctx => new EchoCounterNode(ctx), 41, new FaultSchedule(faults));
        return (TraceLine.Parse(sim.Run().Lines), sim);
    }

    private static IEnumerable<TraceLine> Echoes(List<TraceLine> t) => t.Where(l => l.Node == "n1" && l.Kind == "EVENT" && l["name"] == "echo");

    [Fact]
    public void ACrashDiscardsVolatileStateAndRestartRecoversDurableState()
    {
        var (t, _) = Run(12_000, new Crash(5_000, N1, DiskLoss.Pending), new Restart(6_000, N1));

        var before = Echoes(t).Last(l => l.Time < 5_000).Long("n");
        var firstAfter = Echoes(t).First(l => l.Time >= 6_000).Long("n");
        Assert.True(before > 50);
        Assert.Equal(1, firstAfter);
        var durable = t.Count(l => l.Node == "n1" && l.Kind == "DURABLE" && l.Time < 5_000);
        Assert.Equal(durable, t.First(l => l.Node == "n1" && l.Kind == "EVENT" && l["name"] == "recovered" && l.Time >= 6_000).Long("value"));
        // While it is down, messages addressed to it are lost, not delivered.
        Assert.DoesNotContain(t, l => l.Node == "n1" && l.Kind == "DELIVER" && l.Time > 5_000 && l.Time < 6_000);
        Assert.Contains(t, l => l.Node == "n1" && l.Kind == "LOST" && l.Time > 5_000 && l.Time < 6_000);
    }

    [Fact]
    public void APausedNodeKeepsItsStateAndGetsOneTickThenItsBacklog()
    {
        var (t, _) = Run(12_000, new Pause(5_000, N1), new Unpause(7_000, N1));

        Assert.DoesNotContain(t, l => l.Node == "n1" && l.Kind is "DELIVER" or "EVENT" or "PERSIST" && l.Time > 5_000 && l.Time < 7_000);
        var resume = Assert.Single(t, l => l.Kind == "RESUME");
        // The pause at 5,000 precedes that instant's tick, so the last tick was at 4,999: one tick of 2,001.
        Assert.Equal(2_001, resume.Long("tick"));
        Assert.True(resume.Long("backlog") > 20, "messages sent to the paused node wait for it");

        var before = Echoes(t).Last(l => l.Time < 5_000).Long("n");
        var firstAfter = Echoes(t).First(l => l.Time >= 7_000).Long("n");
        Assert.Equal(before + 1, firstAfter);
        Assert.DoesNotContain(t, l => l.Node == "n1" && l.Kind == "EVENT" && l["name"] == "recovered" && l.Time > 1);
    }

    [Fact]
    public void ALongPauseCostsOneStepNotOnePerMissedTick()
    {
        var (_, paused) = Run(700_000, new Pause(10_000, N1), new Unpause(610_000, N1));
        var (_, running) = Run(700_000);

        // n1 misses 600,000 ticks: the paused run must take far fewer steps, not the same number.
        Assert.True(paused.Steps < running.Steps - 500_000, $"paused {paused.Steps} steps, running {running.Steps}");
    }

    [Fact]
    public void SkewedClocksDivergeInTheirRatio()
    {
        var (t, _) = Run(60_000, new Skew(0, N1, 3, 2));
        double Increments(string n) => t.Count(l => l.Node == n && l.Kind == "PERSIST");

        var ratio = Increments("n1") / Increments("n2");
        Assert.InRange(ratio, 1.47, 1.53);
    }
}
