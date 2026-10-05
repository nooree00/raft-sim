using System.Linq;
using System.Text;
using Raft.Core;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P7-04: the snapshot record the checkers read (phase 7 decision 2), at the head of the log file:
/// its fields round-trip, the entries after it chain onto its index and term, and it is corruption
/// anywhere but the head. Its durable replacement and the node's use of it are P7-05 and P7-06.
/// </summary>
public sealed class SnapshotRecordTests
{
    private static readonly Configuration Three = new([new NodeId(1), new NodeId(2), new NodeId(3)]);

    private static byte[] E(long index, long term, long previous, string command) => EntryLog.Record(index, new Term(term), new Term(previous), Encoding.ASCII.GetBytes(command));

    [Fact]
    public void ASnapshotRecordRoundTrips()
    {
        var r = EntryLog.Recover(EntryLog.SnapshotRecord(5, new Term(2), Three, [1, 2, 3]));

        Assert.Equal(RecoveryPath.Clean, r.Path);
        Assert.NotNull(r.Snapshot);
        Assert.Equal((5L, new Term(2), Three), (r.Snapshot!.Index, r.Snapshot.Term, r.Snapshot.Configuration));
        Assert.Equal(new byte[] { 1, 2, 3 }, r.Snapshot.State);
        Assert.Empty(r.Entries);
        Assert.Null(EntryLog.Recover(EntryLog.SnapshotRecord(1, new Term(1), null, [])).Snapshot!.Configuration);
    }

    /// <summary>The first retained entry's predecessor is the snapshot's last entry: it chains onto the snapshot's index and term.</summary>
    [Fact]
    public void TheEntriesAfterASnapshotChainOntoIt()
    {
        var file = EntryLog.SnapshotRecord(5, new Term(2), Three, [9]).Concat(E(6, 3, 2, "x")).Concat(E(7, 3, 3, "y")).ToArray();

        var r = EntryLog.Recover(file);

        Assert.Equal([6L, 7L], r.Entries.Select(e => e.Index));
        Assert.Equal(file.Length, r.ValidLength);
    }

    /// <summary>A retained entry that does not chain onto the snapshot (wrong previous term) is dropped as unchained, as any other.</summary>
    [Fact]
    public void AnEntryThatDoesNotChainOntoTheSnapshotIsDropped()
    {
        var file = EntryLog.SnapshotRecord(5, new Term(2), Three, [9]).Concat(E(6, 3, 1, "x")).ToArray();

        Assert.Empty(EntryLog.Recover(file).Entries);
    }

    [Fact]
    public void ASnapshotRecordAnywhereButTheHeadIsCorruption()
    {
        var file = E(1, 1, 0, "a").Concat(EntryLog.SnapshotRecord(1, new Term(1), null, [])).ToArray();

        Assert.Equal(RecoveryPath.Refused, EntryLog.Recover(file).Path);
    }
}
