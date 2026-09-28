using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Simulation;
using Raft.SimRun;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>A node that persists each client request, then responds (the barrier holds the response until the persist is durable).</summary>
internal sealed class PersistThenRespondNode : INode
{
    public IReadOnlyList<Effect> Handle(Input input) => input switch
    {
        ClientRequest r => [new PersistAppend("requests.log", r.Payload), new ClientResponse(r.RequestId, Encoding.ASCII.GetBytes("ok"))],
        _ => [],
    };
}

/// <summary>
/// A node that every 500 ticks writes a snapshot the way Figure 13 and spec §8 do: chunks written at
/// offsets into a temporary file, the temporary file renamed over the snapshot, the old log truncated
/// and deleted.
/// </summary>
internal sealed class SnapshotNode(NodeContext ctx) : INode
{
    private long _since = ctx.Random.NextLong(500);
    private long _count;

    public IReadOnlyList<Effect> Handle(Input input)
    {
        if (input is not Tick t)
        {
            return [];
        }

        _since += t.Elapsed;
        if (_since < 500)
        {
            return [];
        }

        _since = 0;
        _count++;
        var chunk = Encoding.ASCII.GetBytes("chunk" + _count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return [new PersistAppend("log", chunk), new PersistWriteAt("snap.tmp", 0, chunk), new PersistWriteAt("snap.tmp", chunk.Length, chunk),
            new PersistRename("snap.tmp", "snap"), new PersistTruncate("log", 0), new PersistDelete("log"), new Emit("snapshot", [new Field("n", _count.ToString(System.Globalization.CultureInfo.InvariantCulture))])];
    }
}

/// <summary>
/// P2-03: every Core input and effect reaches the world in some simulator run — use, not existence.
/// The census counts what the simulator delivered to a node and what it acted on; a response a node
/// builds and the simulator discards (phase 1's bug) never counts. Sabotages S-iface-1..3.
/// </summary>
public sealed class InterfaceCensusTests
{
    private static readonly NodeId N1 = new(1);

    private static Simulator ClientRun(params Fault[] faults)
    {
        var sim = new Simulator(new SimulationConfig { Duration = 5_000 }, _ => new PersistThenRespondNode(), 1, new FaultSchedule(faults));
        for (var i = 1; i <= 5; i++)
        {
            sim.Submit(i * 500, N1, i, Encoding.ASCII.GetBytes("req" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        sim.Run();
        return sim;
    }

    [Fact]
    public void EveryCoreInputAndEffectReachesTheWorldInSomeRun()
    {
        var runs = new List<Simulator>
        {
            ClientRun(),
            new(new SimulationConfig { Duration = 20_000 }, ctx => new EchoCounterNode(ctx), 7, Presets.Mix(20_000)),
            new(new SimulationConfig { Duration = 5_000 }, ctx => new SnapshotNode(ctx), 3),
        };
        foreach (var r in runs.Skip(1))
        {
            r.Run();
        }

        var used = runs.SelectMany(r => r.Census).Where(kv => kv.Value > 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var core = typeof(INode).Assembly.GetTypes().Where(t => !t.IsAbstract && t.IsClass).ToList();
        var wanted = core.Where(t => t.IsSubclassOf(typeof(Input))).Select(t => "input:" + t.Name)
            .Concat(core.Where(t => t.IsSubclassOf(typeof(Effect))).Select(t => "effect:" + t.Name)).ToList();

        Assert.True(wanted.Count >= 11, $"only {wanted.Count} Core input/effect types found");
        var unused = wanted.Where(w => !used.Contains(w)).ToList();
        Assert.True(unused.Count == 0, "Core members no simulator run drives: " + string.Join(", ", unused));
    }

    [Fact]
    public void AClientResponseReachesItsClientAfterItsPersistIsDurable()
    {
        var sim = ClientRun(new SlowDisk(0, N1, 50, 5_000));
        var t = TraceLine.Parse(sim.Trace.Lines);

        Assert.Equal([1L, 2, 3, 4, 5], sim.Responses.Select(r => r.RequestId));
        foreach (var r in sim.Responses)
        {
            var request = t.Single(l => l.Kind == "REQUEST" && l.Long("request") == r.RequestId);
            Assert.True(r.Time - request.Time >= 50, $"request {r.RequestId} answered {r.Time - request.Time} after arriving, before its persist could be durable");
        }
    }

    [Fact]
    public void AResponseHeldByTheBarrierWhenTheNodeCrashesNeverReachesTheClient()
    {
        // Request 3 arrives at 1,500 on a slow disk; the node crashes at 1,520, before the persist is durable.
        var sim = ClientRun(new SlowDisk(0, N1, 50, 5_000), new Crash(1_520, N1, DiskLoss.Pending), new Restart(1_600, N1));

        Assert.DoesNotContain(sim.Responses, r => r.RequestId == 3);
        Assert.DoesNotContain(sim.Trace.Lines, l => l.Contains(" RESPONSE ", StringComparison.Ordinal) && l.Contains("request=3", StringComparison.Ordinal));
        Assert.Contains(sim.Responses, r => r.RequestId == 4);
    }

    [Fact]
    public void ARequestToADownNodeIsRecordedAsLost()
    {
        var sim = ClientRun(new Crash(900, N1, DiskLoss.Pending), new Restart(1_200, N1));

        Assert.Contains(sim.Trace.Lines, l => l.Contains(" REQUEST-LOST ", StringComparison.Ordinal) && l.Contains("request=2", StringComparison.Ordinal));
        Assert.DoesNotContain(sim.Responses, r => r.RequestId == 2);
    }
}
