using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P7-09: a compaction dimension is an effect read from the disk of the node it happened to, not
/// the decision of the node that caused it (phase 6's isolation detector). An install is counted
/// when the follower's disk took the rename of the file its chunks were written to. Sabotage
/// S-cov-13: installs counted when the leader sends the last chunk.
/// </summary>
public sealed class CompactionCoverageTests
{
    /// <summary>
    /// n1 compacts with n3 cut off, then sends n3 its snapshot again and again, and every chunk is
    /// lost: no install is counted, though the leader sent the last chunk. Delivered, it is.
    /// </summary>
    [Fact]
    public void AnInstallIsCountedFromTheFollowersDiskNotFromTheLeadersSends()
    {
        var c = new ManualCluster(RaftOptions.Default with { SnapshotThreshold = 3 });
        c.Stand(N1);
        c.Drop(N1, N3);
        c.Settle(N1, N2);
        for (var i = 0; i < 4; i++)
        {
            c.Client(N1, $"Put|k{i}|v{i}");
            c.Drop(N1, N3);
            c.Tick(N1, 50);
            c.Drop(N1, N3);
            c.Settle(N1, N2);
        }

        for (var i = 0; i < 4; i++)
        {
            c.Tick(N1, 50);
            c.Drop(N1, N3);
            c.Settle(N1, N2);
        }

        var sent = c.Observations.OfType<SentObservation>().Count(s => s.To == N3 && MessageCodec.Decode(s.Payload.ToArray()) is InstallSnapshot { Done: true });
        Assert.True(sent >= 1, "the leader never sent the snapshot's last chunk: the construction tests nothing");
        Assert.DoesNotContain("snapshot-installed-by-a-follower", CompactionCoverage.Of(c.Observations));

        for (var i = 0; i < 4; i++)
        {
            c.Tick(N1, 50);
            c.Settle(All);
        }

        Assert.Contains("snapshot-installed-by-a-follower", CompactionCoverage.Of(c.Observations));
        Assert.Contains("log-compacted", CompactionCoverage.Of(c.Observations));
    }
}
