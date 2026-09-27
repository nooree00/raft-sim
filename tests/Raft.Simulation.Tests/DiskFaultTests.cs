using System.Collections.Generic;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Raft.SimRun;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P1-06: disk faults and the persist barrier, each proven by its effect on what a restarted node
/// recovers, or on when a send leaves. Vacuity risk: crashes that happen only when nothing is in
/// flight, so loss is never exercised — every crash here is placed inside a write window found by a
/// pre-run (faults at later times cannot change the run before them). Sabotages S-disk-1..4.
/// </summary>
public sealed class DiskFaultTests
{
    private static readonly NodeId N1 = new(1), N2 = new(2);
    private const long End = 30_000;

    private static List<TraceLine> Run(params Fault[] faults) =>
        TraceLine.Parse(new Simulator(new SimulationConfig { Duration = End }, ctx => new EchoCounterNode(ctx), 31, new FaultSchedule(faults)).Run().Lines);

    /// <summary>The first PERSIST at n1 at or after <paramref name="after"/>, under a slow disk of the given latency.</summary>
    private static long PersistAt(long after, long latency) =>
        Run(new SlowDisk(0, N1, latency, End)).First(l => l.Node == "n1" && l.Kind == "PERSIST" && l.Time >= after).Time;

    private static long DurableBefore(List<TraceLine> t, long time) => t.Count(l => l.Node == "n1" && l.Kind == "DURABLE" && l.Time < time);

    private static long RecoveredAfter(List<TraceLine> t, long time) =>
        t.First(l => l.Node == "n1" && l.Kind == "EVENT" && l["name"] == "recovered" && l.Time >= time).Long("value");

    [Fact]
    public void ACrashInsideTheWriteWindowLosesTheUnsyncedWrite()
    {
        var p = PersistAt(5_000, 50);
        var t = Run(new SlowDisk(0, N1, 50, End), new Crash(p + 10, N1, DiskLoss.Pending), new Restart(p + 200, N1));

        var crash = Assert.Single(t, l => l.Kind == "CRASH");
        Assert.Equal(1, crash.Long("lost"));
        Assert.Equal(DurableBefore(t, crash.Time), RecoveredAfter(t, p + 200));
    }

    [Fact]
    public void ATornWriteLeavesAPrefixThatRecoveryTruncates()
    {
        var p = PersistAt(5_000, 50);
        var t = Run(new SlowDisk(0, N1, 50, End), new Crash(p + 10, N1, DiskLoss.Torn), new Restart(p + 200, N1),
            new Crash(p + 2_000, N1, DiskLoss.Pending), new Restart(p + 2_200, N1));

        var crash = t.First(l => l.Kind == "CRASH");
        Assert.True(crash.Long("kept") < crash.Long("of"), "a torn write keeps a strict prefix");
        Assert.Equal(DurableBefore(t, crash.Time), RecoveredAfter(t, p + 200));
        Assert.Contains(t, l => l.Node == "n1" && l.Kind == "PERSIST" && l["op"] == "PersistTruncate" && l.Time >= p + 200);
        // After truncation the next crash-and-restart recovers everything durable: no garbage mid-file.
        Assert.True(EchoCounterChecks.Check(t.Select(Line).ToList()).Ok);
        // Every durable write before the second crash is a counter record except the one truncate.
        var truncates = t.Count(l => l.Node == "n1" && l.Kind == "DURABLE" && l.Time < p + 2_000 && IsTruncate(t, l));
        Assert.Equal(1, truncates);
        Assert.Equal(DurableBefore(t, p + 2_000) - truncates, RecoveredAfter(t, p + 2_200));
    }

    [Fact]
    public void ReorderedCompletionCanKeepALaterWriteWithoutAnEarlierOne()
    {
        // With a 350-unit disk and a write every 100, several writes are in flight at once.
        for (var k = 0; k < 40; k++)
        {
            var p = PersistAt(5_000 + (k * 100), 350);
            var t = Run(new SlowDisk(0, N1, 350, End), new Crash(p + 20, N1, DiskLoss.Reordered), new Restart(p + 1_000, N1));
            var crash = t.Single(l => l.Kind == "CRASH");
            var survived = List(crash["survived"]);
            var of = List(crash["pending"]);
            if (survived.Count == 0 || survived.SequenceEqual(of.Take(survived.Count)))
            {
                continue; // a prefix (or nothing) survived: not a reordering; try the next crash point
            }

            // The hole: the last survivor's record is on disk while an earlier write is not, so the
            // node recovers the last survivor's value.
            var position = of.IndexOf(survived[^1]) + 1;
            Assert.Equal(DurableBefore(t, p + 20) + position, RecoveredAfter(t, p + 1_000));
            return;
        }

        Assert.Fail("no crash point in 40 left a non-prefix set of surviving writes");
    }

    [Fact]
    public void PositiveControlLosingAnFsyncedWriteBreaksDurability()
    {
        var p = PersistAt(5_000, 50);
        // By p + 70 the write is durable (p + 50), announced, and received by a peer.
        var lost = Run(new SlowDisk(0, N1, 50, End), new Crash(p + 70, N1, DiskLoss.LoseSynced), new Restart(p + 200, N1));
        var kept = Run(new SlowDisk(0, N1, 50, End), new Crash(p + 70, N1, DiskLoss.Pending), new Restart(p + 200, N1));

        Assert.False(EchoCounterChecks.Check(lost.Select(Line).ToList()).Ok, "a disk that loses fsynced data must break the durability property");
        Assert.True(EchoCounterChecks.Check(kept.Select(Line).ToList()).Ok, "the same crash without the lie must not");
    }

    [Fact]
    public void ASlowDiskDelaysTheSendThatFollowsAPersist()
    {
        var t = Run(new SlowDisk(5_000, N1, 40, 8_000));
        var persists = t.Where(l => l.Node == "n1" && l.Kind == "PERSIST" && l.Time >= 5_000 && l.Time < 8_000).ToList();

        Assert.True(persists.Count >= 25);
        foreach (var p in persists)
        {
            var next = t.First(l => l.Node == "n1" && l.Kind == "SEND" && l.Time >= p.Time && t.IndexOf(l) > t.IndexOf(p));
            Assert.True(next.Time >= p.Time + 40, $"persist at {p.Time}, next send at {next.Time}");
        }
    }

    [Fact]
    public void TheBarrierHoldsALaterStepsSendBehindAnEarlierStepsPersist()
    {
        // n2's disk is slow; its echoes (sent without persisting) must still wait for its pending counter write.
        var t = Run(new SlowDisk(0, N2, 60, End));
        var heldEchoes = 0;
        var pending = new Queue<long>();
        foreach (var l in t.Where(l => l.Node == "n2"))
        {
            if (l.Kind == "PERSIST")
            {
                pending.Enqueue(l.Long("seq"));
            }
            else if (l.Kind == "DURABLE")
            {
                pending.Dequeue();
            }
            else if (l.Kind == "SEND")
            {
                Assert.True(pending.Count == 0, $"{l.Time}: n2 sent while writes {string.Join(',', pending)} were in flight");
            }
            else if (l.Kind == "DELIVER" && pending.Count > 0)
            {
                heldEchoes++;
            }
        }

        Assert.True(heldEchoes > 50, $"only {heldEchoes} deliveries arrived while a write was in flight");
    }

    [Fact]
    public void PositiveControlViolatingTheBarrierBreaksDurability()
    {
        var p = PersistAt(5_000, 50);
        // Sends leave at once, are received by p + 10, and the write they announce is lost at p + 20.
        var t = Run(new SlowDisk(0, N1, 50, End), new BarrierViolation(0, N1, End), new Crash(p + 20, N1, DiskLoss.Pending), new Restart(p + 200, N1));

        Assert.False(EchoCounterChecks.Check(t.Select(Line).ToList()).Ok, "sends released before their persist is durable must break durability");
    }

    private static bool IsTruncate(List<TraceLine> t, TraceLine durable) =>
        t.Any(l => l.Node == durable.Node && l.Kind == "PERSIST" && l["op"] == "PersistTruncate" && l.Long("seq") == durable.Long("seq"));

    private static List<long> List(string value) =>
        value == "none" ? [] : value.Split(',').Select(v => long.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToList();

    private static string Line(TraceLine l) =>
        $"{l.Time:D10} {l.Node} {l.Kind} " + string.Join(' ', l.Fields.Select(kv => $"{kv.Key}={kv.Value}"));
}
