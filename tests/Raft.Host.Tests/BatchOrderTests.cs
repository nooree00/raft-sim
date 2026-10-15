using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Core;
using Raft.Host;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P15-05: the loop hands a batch's client requests to the node after its other inputs, so an answer's
/// outputs come before the requests' persists (class B becomes C); every source's inputs keep their order.
/// Sabotages S-early-3 (arrival order again), S-early-4 (two messages from one peer swapped).
/// </summary>
public sealed class BatchOrderTests
{
    private static Receive From(int peer, string tag) => new(new NodeId(peer), new byte[] { (byte)tag[0] });

    private static string Name(Input i) => i switch
    {
        ClientRequest c => "c" + c.RequestId,
        Receive r => $"p{r.From.Value}{(char)r.Payload.Span[0]}",
        Tick t => "t" + t.Elapsed,
        _ => "?",
    };

    /// <summary>Sabotage S-early-3.</summary>
    [Fact]
    public void ABatchsClientRequestsAreHandedOverAfterItsOtherInputs()
    {
        var batch = new List<Input> { new ClientRequest(1, new byte[1]), From(2, "a"), new ClientRequest(2, new byte[1]), new Tick(10), From(3, "b") };

        NodeHost.ClientRequestsLast(batch, []);

        Assert.Equal(["p2a", "t10", "p3b", "c1", "c2"], batch.Select(Name));
    }

    /// <summary>Sabotage S-early-4: a peer's two messages, and a client's two requests, each in their arrival order.</summary>
    [Fact]
    public void EachSourcesInputsKeepTheirOrder()
    {
        var batch = new List<Input> { From(2, "a"), new ClientRequest(5, new byte[1]), From(3, "x"), From(2, "b"), new ClientRequest(6, new byte[1]) };

        NodeHost.ClientRequestsLast(batch, []);

        Assert.Equal(["p2a", "p3x", "p2b", "c5", "c6"], batch.Select(Name));
    }
}
