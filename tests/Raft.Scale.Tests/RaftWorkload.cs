using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Core.Tests;
using Raft.Simulation;
using Xunit;

namespace Raft.Scale.Tests;

/// <summary>
/// Random single-key KV operations (spec §6: all five, `Delete` since P5 decision 5) from simulated clients, each sent to a random node (P4
/// decision 5: a non-leader refuses, and the client's next operation goes elsewhere), or all to one
/// node. Values are unique per operation. <c>think</c> is the pause before each new operation.
/// </summary>
internal sealed class RaftWorkload(int perClient, NodeId? target = null, int keys = 3, bool retry = false, long think = 0) : IClientWorkload
{
    /// <summary>With <c>retry</c>, a timed-out command is sent again, same bytes, to a node drawn afresh (P4-07): the source of duplicates.</summary>
    public ClientCall? Retry(int client, ClientCall timedOut, IRandomSource random) =>
        retry ? new ClientCall(target ?? new NodeId(1 + (int)random.NextLong(Cluster.Nodes)), timedOut.Request) : null;

    public ClientCall? NextCall(int client, int sequence, IRandomSource random)
    {
        if (sequence >= perClient)
        {
            return null;
        }

        var node = target ?? new NodeId(1 + (int)random.NextLong(Cluster.Nodes));
        var key = "k" + random.NextLong(keys).ToString(CultureInfo.InvariantCulture);
        var value = "c" + client.ToString(CultureInfo.InvariantCulture) + "s" + sequence.ToString(CultureInfo.InvariantCulture);
        var command = random.NextLong(5) switch
        {
            0 => "Put|" + key + "|" + value,
            1 => "Append|" + key + "|" + value,
            2 => "Get|" + key,
            3 => "Delete|" + key,
            _ => "Cas|" + key + "|-|" + value,
        };
        return new ClientCall(node, Encoding.ASCII.GetBytes(command), think);
    }
}
