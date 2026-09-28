using System;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Raft.SimRun;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P3-04: the observations the invariant checkers read. Vacuity risk: an observation stream that
/// misses events (a delivery recorded when sent, a durable write never recorded) makes every checker
/// pass over less than happened. Guarded: the observations must match the canonical trace one for
/// one, and observing must not change the trace. Sabotage S-obs-1.
/// </summary>
public sealed class ObservationTests
{
    private static Simulator Run(bool observe)
    {
        var sim = new Simulator(new SimulationConfig { Duration = 5_000 }, ctx => new EchoCounterNode(ctx), 7, Presets.Mix(5_000)) { Observe = observe };
        sim.Run();
        return sim;
    }

    [Fact]
    public void ObservationsMatchTheTraceOneForOne()
    {
        var sim = Run(observe: true);
        int Lines(string kind) => sim.Trace.Lines.Count(l => l.Split(' ')[2] == kind);

        Assert.Equal(Lines("SEND"), sim.Observations.OfType<SentObservation>().Count());
        Assert.Equal(Lines("DELIVER"), sim.Observations.OfType<DeliveredObservation>().Count());
        Assert.Equal(Lines("CRASH"), sim.Observations.OfType<CrashObservation>().Count());
        Assert.Equal(Lines("START"), sim.Observations.OfType<StartObservation>().Count());
        Assert.True(Lines("SEND") > 100 && Lines("CRASH") > 0, "the run exercised too little to compare");

        // A delivery is observed after its send, never at the same instant or before (the minimum network delay is one unit).
        var sentAt = sim.Observations.OfType<SentObservation>().ToDictionary(s => s.Id, s => s.Time);
        Assert.All(sim.Observations.OfType<DeliveredObservation>(), d => Assert.True(d.Time > sentAt[d.Id], $"message {d.Id} delivered at {d.Time}, sent at {sentAt[d.Id]}"));

        Assert.NotEmpty(sim.Observations.OfType<DurableObservation>());
    }

    [Fact]
    public void ObservingDoesNotChangeTheRun() =>
        Assert.Equal(Run(observe: false).Trace.Text(), Run(observe: true).Trace.Text());
}
