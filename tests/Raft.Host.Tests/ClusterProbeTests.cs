using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Raft.Core;
using Raft.Host;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P15-01: a load record's leader fields come from each host's counters read before and after the run,
/// through the same function whether the hosts share the generator's process or not; the counters cross
/// the cluster's control port unchanged; and the generator's process is told from the hosts' by a token,
/// not a process id. Sabotages S-proc-1 (processes compared by id), S-proc-2 (a single reading, not a
/// difference).
/// </summary>
public sealed class ClusterProbeTests
{
    private static HostCounters Counters(long syncs, long messages, long inputs, long batches) =>
        new(syncs, syncs * 250.0, messages, messages * 80, 10, 1_000, 5_000, 2_000, syncs * 300.0, inputs, batches);

    /// <summary>Sabotage S-proc-2: before the run each host has a history (the election, a warm-up); the record is what the leader did after it.</summary>
    [Fact]
    public void TheRecordsLeaderFieldsAreWhatTheLeaderDidBetweenTheTwoReadings()
    {
        IReadOnlyList<HostCounters> before = [Counters(5_000, 20_000, 9_000, 6_000), Counters(4_000, 1_000, 8_000, 8_000), Counters(4_100, 1_000, 8_100, 8_100)];
        IReadOnlyList<HostCounters> after = [Counters(5_300, 20_600, 9_600, 6_400), Counters(4_200, 1_010, 8_300, 8_300), Counters(4_300, 1_010, 8_300, 8_300)];

        var fields = Program.LeaderFields(before, after, new NodeId(1), answered: 1_000, wallMicros: 1_000_000, meanLatency: 2_000);

        Assert.Equal(0.3, fields["leader_syncs_per_write"], 6);
        Assert.Equal(0.2, fields["follower_syncs_per_write"], 6);
        Assert.Equal(0.6, fields["leader_messages_per_write"], 6);
        Assert.Equal(1.5, fields["leader_loop_inputs_per_batch"], 6);
        Assert.Equal(250, fields["leader_sync_us"], 6);
    }

    [Fact]
    public void HostCountersCrossTheControlPortUnchanged()
    {
        var c = new HostCounters(12, 3_456.75, 789, 63_120, 41, 12_345.5, 9_876_543.25, 1_234.5, 6_789.125, 2_222, 1_111);

        Assert.Equal(c, HostCounters.Decode(c.Encode()));
    }

    /// <summary>A cluster seen from another container: its process is numbered as this one is, in a namespace of its own.</summary>
    private sealed class OtherNamespace : IClusterProbe
    {
        public int ProcessId => Environment.ProcessId;

        public string Instance => "a process drawn elsewhere";

        public int TraceSample => 1;

        public IReadOnlyDictionary<NodeId, DnsEndPoint> Clients => new Dictionary<NodeId, DnsEndPoint>();

        public Task<IReadOnlyList<HostCounters>> CountersAsync() => throw new NotSupportedException();

        public Task<NodeId> LeaderAsync(TimeSpan within) => throw new NotSupportedException();

        public Task<List<(int Node, IReadOnlyList<RoleChange> Changes)>> RolesAsync() => throw new NotSupportedException();

        public Task<Dictionary<NodeId, byte[]>> TracesAsync() => throw new NotSupportedException();
    }

    /// <summary>
    /// Sabotage S-proc-1: the first separate runs (two containers, each process 1 of its own namespace) read
    /// as sharing a process when the record compared process ids.
    /// </summary>
    [Fact]
    public void AProcessNumberedLikeThisOneInAnotherNamespaceIsAnotherProcess() =>
        Assert.Equal(1, Program.GeneratorOwnProcess(new OtherNamespace()));
}
