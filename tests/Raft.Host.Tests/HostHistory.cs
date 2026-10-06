using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Host;
using Raft.Simulation;

namespace Raft.Host.Tests;

/// <summary>The real client's history as the client log the checker's adapter reads (P9-06): one attempt, one entry.</summary>
internal static class HostHistory
{
    public static List<ClientOp> ToClientLog(IEnumerable<HistoryEntry> entries) =>
        entries.OrderBy(e => e.Invoke).ThenBy(e => e.RequestId)
            .Select(e => new ClientOp(e.Client, e.RequestId, new NodeId(e.Node), Encoding.ASCII.GetBytes(e.Request), e.Invoke, e.Response, Encoding.ASCII.GetBytes(e.Reply)))
            .ToList();
}
