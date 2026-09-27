using System.Collections.Generic;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Raft.SimRun;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P1-05: each network fault proven by its effect at the receiver — never by the injection site's
/// own trace line alone. Vacuity risk: a fault that is "applied" to a copy while the message is
/// still delivered; guarded by asserting on the receiver's DELIVER lines. Sabotages S-net-1..5.
/// </summary>
public sealed class NetworkFaultTests
{
    private static readonly NodeId N1 = new(1), N2 = new(2);

    private static List<TraceLine> Run(params Fault[] faults)
    {
        var sim = new Simulator(new SimulationConfig { Duration = 20_000 }, ctx => new EchoCounterNode(ctx), 21, new FaultSchedule(faults));
        return TraceLine.Parse(sim.Run().Lines);
    }

    private static TraceLine Marked(List<TraceLine> t, string kind) => Assert.Single(t, l => l.Kind == kind);

    private static List<TraceLine> DeliveriesOf(List<TraceLine> t, string id) => t.Where(l => l.Kind == "DELIVER" && l["id"] == id).ToList();

    [Fact]
    public void ADroppedMessageNeverReachesItsReceiver()
    {
        var t = Run(new Drop(5_000, N1, N2));
        var drop = Marked(t, "DROP");

        Assert.Empty(DeliveriesOf(t, drop["id"]));
        Assert.Contains(t, l => l.Kind == "SEND" && l["id"] == drop["id"] && l["to"] == "n2");
        // Everything else on the link still arrives.
        Assert.True(t.Count(l => l.Kind == "DELIVER" && l.Node == "n2" && l["from"] == "n1") > 100);
    }

    [Fact]
    public void ADuplicatedMessageArrivesExactlyTwice()
    {
        var t = Run(new Duplicate(5_000, N1, N2));
        var dup = Marked(t, "DUP");

        Assert.Equal(2, DeliveriesOf(t, dup["id"]).Count);
        Assert.All(DeliveriesOf(t, dup["id"]), d => Assert.Equal("n2", d.Node));
    }

    [Fact]
    public void AReorderedMessageArrivesAfterTheNextOneOnItsLink()
    {
        var t = Run(new Reorder(5_000, N1, N2));
        var held = Marked(t, "HELD");
        var heldAt = t.IndexOf(held);
        var next = t.Skip(heldAt + 1).First(l => l.Kind == "SEND" && l.Node == "n1" && l["to"] == "n2");

        var heldDelivery = t.FindIndex(l => l.Kind == "DELIVER" && l["id"] == held["id"]);
        var nextDelivery = t.FindIndex(l => l.Kind == "DELIVER" && l["id"] == next["id"]);
        Assert.True(heldDelivery > 0 && nextDelivery > 0, "both messages must arrive");
        Assert.True(nextDelivery < heldDelivery, "the held message must arrive after the one sent after it");
    }

    [Fact]
    public void ADelayedMessageArrivesAtLeastTheExtraLater()
    {
        var t = Run(new Delay(5_000, N1, N2, 500));
        var delayed = Marked(t, "DELAYED");

        var delivery = Assert.Single(DeliveriesOf(t, delayed["id"]));
        Assert.True(delivery.Time >= delayed.Time + 500, $"sent {delayed.Time}, delivered {delivery.Time}");
    }

    [Fact]
    public void AnAsymmetricPartitionBlocksOneDirectionOnlyAndHealingRestoresIt()
    {
        var t = Run(new Partition(5_000, N2, N1), new Heal(9_000, N2, N1));
        bool From(TraceLine l, string to, string from) => l.Kind == "DELIVER" && l.Node == to && l["from"] == from;
        // Allow in-flight messages sent just before the cut, and just after the heal, to settle.
        var inWindow = t.Where(l => l.Time >= 5_100 && l.Time < 9_000).ToList();
        var afterHeal = t.Where(l => l.Time >= 9_100).ToList();

        Assert.DoesNotContain(inWindow, l => From(l, "n1", "n2"));
        // n1 announces to n2 every 100 units (~39 in the window); n2's announcements cannot reach n1, so no echoes.
        Assert.True(inWindow.Count(l => From(l, "n2", "n1")) >= 35, "the other direction must keep flowing");
        Assert.Contains(t, l => l.Kind == "BLOCKED" && l.Node == "n2" && l["to"] == "n1");
        Assert.True(afterHeal.Count(l => From(l, "n1", "n2")) > 50, "healing must restore delivery");
    }

    [Fact]
    public void SameTimeEventsKeepInsertionOrderWhateverElseIsQueued()
    {
        // The shrinker's premise: removing an unrelated event must not reorder two others.
        var rng = new Streams(99).For("agenda-test");
        for (var trial = 0; trial < 300; trial++)
        {
            var agenda = new Agenda();
            var order = new List<string>();
            var others = (int)rng.NextLong(20);
            for (var i = 0; i < others; i++)
            {
                agenda.Enqueue(rng.NextLong(4) * 25, () => order.Add("x"));
            }

            agenda.Enqueue(50, () => order.Add("A"));
            for (var i = 0; i < others; i++)
            {
                agenda.Enqueue(rng.NextLong(4) * 25, () => order.Add("y"));
            }

            agenda.Enqueue(50, () => order.Add("B"));
            while (agenda.TryDequeue(out var action, out _))
            {
                action();
            }

            Assert.True(order.IndexOf("A") < order.IndexOf("B"), $"trial {trial}: B came out before A");
        }
    }
}
