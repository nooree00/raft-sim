using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Core;
using Raft.Core.Tests;
using Raft.Simulation;
using Xunit;

namespace Raft.Scale.Tests;

/// <summary>
/// P11-01: the work replication does per committed entry, which no invariant constrains. Phase 10
/// found the leader resending every unacknowledged entry on every client write and every response:
/// every replica agreed and every history was linearizable while the cluster completed almost
/// nothing. The soak could not see it: its clients are closed loop, three per execution, so at most
/// three entries are ever unacknowledged. This check keeps many operations in flight against the
/// leader, with no faults, and bounds the entries the leader sends to each follower per committed
/// entry. What it covers is amplification of replication traffic; a performance pathology with no
/// invariant signature that does not send more entries (a slow apply, a lock, an unbounded queue) is
/// outside it. Vacuity risk: a workload that never builds a backlog passes any implementation, so the
/// backlog it measured is asserted too. Sabotages S-cost-1 (the reading counts the followers' sends,
/// which carry no entries) and S-cost-2 (the backlog assertion removed).
/// </summary>
public sealed class ReplicationCostTests
{
    private const long Duration = 5_000;
    private static readonly NodeId[] Nodes = [new(1), new(2), new(3)];

    /// <summary>What the leader's sends say: entries sent to each follower, the highest commit index it announced, and the largest backlog at any send.</summary>
    internal sealed record Cost(IReadOnlyDictionary<NodeId, long> EntriesSent, long Committed, long Backlog)
    {
        public double WorstRatio => Committed == 0 ? double.PositiveInfinity : EntriesSent.Values.Max() / (double)Committed;
    }

    /// <summary>
    /// The reading, from the leader's `Send` effects alone (spec §9: a counter derived from what
    /// happened, none in `RaftNode`): each `AppendEntries` it sent decoded, its entries counted to its
    /// follower; committed entries are the highest commit index it announced; the backlog at a send is
    /// the last index it carried (or, empty, its previous index) less the commit index it announced.
    /// </summary>
    internal static Cost Read(Simulator sim, NodeId leader)
    {
        var sent = Nodes.Where(n => n != leader).ToDictionary(n => n, _ => 0L);
        long committed = 0, backlog = 0;
        foreach (var s in sim.Observations.OfType<SentObservation>().Where(s => s.Node == leader))
        {
            if (MessageCodec.Decode(s.Payload.ToArray()) is not AppendEntries ae)
            {
                continue;
            }

            sent[s.To] += ae.Entries.Count;
            committed = Math.Max(committed, ae.LeaderCommit);
            backlog = Math.Max(backlog, ae.PrevLogIndex + ae.Entries.Count - ae.LeaderCommit);
        }

        return new Cost(sent, committed, backlog);
    }

    /// <summary>One run: the leader found by a probe of the same seed, then <paramref name="inFlight"/> clients with no think time, every operation sent to it.</summary>
    internal static (Cost Cost, int Elections) Run(ulong seed, int inFlight)
    {
        var (_, probe) = Cluster.Run(seed, 1_000);
        var leader = probe.Elections().OrderBy(e => e.Value).Select(e => e.Key.Candidate).First();
        var (sim, h) = Cluster.Run(seed, Duration, clients: inFlight, workload: new RaftWorkload(int.MaxValue, target: leader));
        return (Read(sim, leader), h.Elections().Count);
    }

    /// <summary>
    /// With many operations in flight and no faults, the leader sends each follower at most 2 entries
    /// per committed entry: one send each, and room for a heartbeat re-sending an append still in
    /// flight. On the code before P11-02 this failed (its outcome records the ratios).
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    public void TheLeaderSendsEachFollowerABoundedNumberOfEntriesPerCommittedEntry(int inFlight)
    {
        var (cost, elections) = Run(1, inFlight);
        var problems = Problems(cost, elections, inFlight);

        // P12-08: the values the check's bounds are read against, for the margin audit (scripts/margins.sh).
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"replication-cost-{inFlight}.txt"), FormattableString.Invariant($"committed {cost.Committed}, worst {cost.WorstRatio:F4}, backlog {cost.Backlog}, elections {elections}\n"));
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>The check's verdict on one run: the guards that it measured what it claims, then the bound.</summary>
    internal static List<string> Problems(Cost cost, int elections, int inFlight)
    {
        var line = FormattableString.Invariant($"{inFlight} in flight: {cost.Committed} committed, entries sent {string.Join(", ", cost.EntriesSent.Select(e => $"{e.Key}={e.Value}"))}, worst {cost.WorstRatio:F2} per committed entry, backlog up to {cost.Backlog}");
        var problems = new List<string>();
        if (elections != 1)
        {
            problems.Add($"{elections} elections: the run did not keep one leader, so the reading mixes leaders ({line})");
        }

        if (cost.Committed < 100)
        {
            problems.Add($"only {cost.Committed} entries committed: too few to read a ratio from ({line})");
        }

        if (cost.Backlog < inFlight / 2)
        {
            problems.Add($"the backlog never reached {inFlight / 2}: the workload did not keep operations in flight, and any implementation passes ({line})");
        }

        if (cost.WorstRatio > 2)
        {
            problems.Add($"the leader sent a follower {cost.WorstRatio:F2} entries per committed entry, over 2: replication's work grows with the backlog ({line})");
        }

        return problems;
    }

    /// <summary>The vacuity guard on its own: a run whose backlog never built is refused, however good its ratio. Sabotage S-cost-2.</summary>
    [Fact]
    public void ARunThatNeverBuiltABacklogIsRefused()
    {
        var idle = new Cost(new Dictionary<NodeId, long> { [Nodes[1]] = 500, [Nodes[2]] = 500 }, 500, 1);

        Assert.Contains(Problems(idle, 1, 32), p => p.Contains("the backlog never reached 16", StringComparison.Ordinal));
    }
}
