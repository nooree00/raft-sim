using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>Each client sends <see cref="PerClient"/> requests to one node, then stops.</summary>
internal sealed class FixedWorkload(int perClient, NodeId node) : IClientWorkload
{
    public const int PerClient = 10;

    public ClientCall? NextCall(int client, int sequence, IRandomSource random) =>
        sequence < perClient ? new ClientCall(node, Encoding.ASCII.GetBytes("c" + client.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture))) : null;
}

/// <summary>
/// P2-08: simulated clients over the simulated network. Vacuity risk: a history recorded from what
/// clients sent, not from what came back, would show responses that never arrived. Guarded:
/// a response is recorded only when it reaches the client, and a test cuts the reply link and
/// requires every operation to be indeterminate although the node handled each one.
/// Sabotages S-client-1..3.
/// </summary>
public sealed class ClientTests
{
    private static readonly NodeId N1 = new(1);
    private static readonly NodeId C1 = Simulator.ClientNode(1);

    private static Simulator Run(params Fault[] faults)
    {
        var sim = new Simulator(new SimulationConfig { Duration = 20_000, Clients = 2 }, _ => new PersistThenRespondNode(), 5, new FaultSchedule(faults))
        {
            Workload = new FixedWorkload(FixedWorkload.PerClient, N1),
        };
        sim.Run();
        return sim;
    }

    [Fact]
    public void WithoutFaultsEveryOperationIsAnsweredAfterItIsInvoked()
    {
        var sim = Run();

        Assert.Equal(2 * FixedWorkload.PerClient, sim.ClientLog.Count);
        Assert.All(sim.ClientLog, op => Assert.True(op.Response > op.Invoke, $"{op.RequestId}: {op.Invoke} → {op.Response}"));
        Assert.All(sim.ClientLog, op => Assert.Equal("ok", Encoding.ASCII.GetString(op.Reply.Span)));
        // A client is sequential: its next operation starts after the previous one ends.
        foreach (var ops in sim.ClientLog.GroupBy(o => o.Client))
        {
            var list = ops.ToList();
            Assert.All(list.Zip(list.Skip(1)), p => Assert.True(p.Second.Invoke >= p.First.Response));
        }
    }

    [Fact]
    public void ARequestDroppedOnTheWayIsIndeterminateAndTheClientMovesOn()
    {
        var sim = Run(new Drop(0, C1, N1));
        var first = sim.ClientLog.First(o => o.Client == 1);

        Assert.Null(first.Response);
        Assert.Contains(sim.Trace.Lines, l => l.Contains(" TIMEOUT ", StringComparison.Ordinal) && l.Contains("request=" + first.RequestId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.Equal(FixedWorkload.PerClient, sim.ClientLog.Count(o => o.Client == 1));
        Assert.Equal(FixedWorkload.PerClient - 1, sim.ClientLog.Count(o => o.Client == 1 && o.Response is not null));
    }

    [Fact]
    public void AReplyThatNeverArrivesLeavesTheOperationIndeterminateThoughTheNodeHandledIt()
    {
        var sim = Run(new Partition(0, N1, C1));
        var mine = sim.ClientLog.Where(o => o.Client == 1).ToList();

        Assert.All(mine, op => Assert.Null(op.Response));
        foreach (var op in mine)
        {
            Assert.Contains(sim.Trace.Lines, l => l.Contains(" RESPONSE ", StringComparison.Ordinal) && l.Contains("request=" + op.RequestId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }

        Assert.All(sim.ClientLog.Where(o => o.Client == 2), op => Assert.NotNull(op.Response));
    }

    [Fact]
    public void TheClientLogReproducesByteIdenticallyFromItsSchedule()
    {
        Fault[] faults = [new Drop(40, C1, N1), new Delay(20, N1, Simulator.ClientNode(2), 400), new Duplicate(60, C1, N1)];
        var (_, parsed) = ScheduleText.Read(ScheduleText.Write(new ScheduleText.Header(5, 20_000, 3, "x", "y"), new FaultSchedule(faults)));

        var a = Run(faults).ClientLogText();
        var b = Run([.. parsed.Faults]).ClientLogText();

        Assert.Equal(a, b);
        Assert.Contains("response=none", a, StringComparison.Ordinal);
        Assert.Equal(2 * FixedWorkload.PerClient, a.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void GeneratedSchedulesFaultClientLinksOnlyWhenThereAreClients()
    {
        var without = FaultGenerator.Generate(3, new GeneratorConfig { Duration = 20_000 });
        var with = FaultGenerator.Generate(3, new GeneratorConfig { Duration = 20_000, Clients = 2 });
        static bool TouchesClient(Fault f) => f is LinkFault l && (l.From.Value > Simulator.ClientBase || l.To.Value > Simulator.ClientBase);

        Assert.DoesNotContain(without.Faults, TouchesClient);
        Assert.Contains(with.Faults, TouchesClient);
    }
}
