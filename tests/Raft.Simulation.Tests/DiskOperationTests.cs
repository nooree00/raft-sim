using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P2-03: write-at, rename and delete, each driven through every crash mode on the simulated disk
/// (phase 1 exercised only append and truncate). Vacuity risk: operations implemented but never
/// issued, which is how rename went untested through an accepted phase; guarded here per mode, and
/// by the census (<see cref="InterfaceCensusTests"/>). Sabotages S-iface-1..3.
/// </summary>
public sealed class DiskOperationTests
{
    private static byte[] B(string s) => Encoding.ASCII.GetBytes(s);

    private static string? Read(SimDisk d, string file) =>
        d.Snapshot().TryGetValue(file, out var m) ? Encoding.ASCII.GetString(m.Span) : null;

    private static SimDisk Disk(params Persist[] completed)
    {
        var d = new SimDisk();
        foreach (var op in completed)
        {
            d.Issue(op, 0);
            d.CompleteNext();
        }

        return d;
    }

    /// <summary>
    /// The positive control loses the last completed write — here a rename, which changes two files.
    /// Undoing it must restore both: the source back, the destination to what it was. Found by
    /// reading at P1 acceptance; this test was run red before the fix (docs/findings.md).
    /// </summary>
    [Fact]
    public void LosingASyncedRenameRestoresBothFiles()
    {
        var d = Disk(new PersistAppend("snap", B("old")), new PersistAppend("snap.tmp", B("new")), new PersistRename("snap.tmp", "snap"));

        d.Crash(DiskLoss.LoseSynced, () => 0);

        Assert.Equal("old", Read(d, "snap"));
        Assert.Equal("new", Read(d, "snap.tmp"));
    }

    [Fact]
    public void LosingASyncedRenameOntoNothingRemovesTheDestination()
    {
        var d = Disk(new PersistAppend("snap.tmp", B("new")), new PersistRename("snap.tmp", "snap"));

        d.Crash(DiskLoss.LoseSynced, () => 0);

        Assert.Null(Read(d, "snap"));
        Assert.Equal("new", Read(d, "snap.tmp"));
    }

    [Theory]
    [InlineData(DiskLoss.Pending)]
    [InlineData(DiskLoss.Torn)]
    [InlineData(DiskLoss.Reordered)]
    public void ARenameInFlightAtACrashIsAtomic(DiskLoss loss)
    {
        foreach (var draw in new ulong[] { 0, 1 })
        {
            var d = Disk(new PersistAppend("snap", B("old")), new PersistAppend("snap.tmp", B("new")));
            d.Issue(new PersistRename("snap.tmp", "snap"), 10);

            d.Crash(loss, () => draw);

            // Either the rename happened or it did not; never half of it.
            var (snap, tmp) = (Read(d, "snap"), Read(d, "snap.tmp"));
            Assert.True((snap, tmp) is ("old", "new") or ("new", null), $"{loss}/{draw}: snap={snap} tmp={tmp}");
        }
    }

    [Theory]
    [InlineData(DiskLoss.Pending)]
    [InlineData(DiskLoss.Torn)]
    [InlineData(DiskLoss.Reordered)]
    public void ADeleteInFlightAtACrashLeavesTheFileWholeOrGone(DiskLoss loss)
    {
        foreach (var draw in new ulong[] { 0, 1 })
        {
            var d = Disk(new PersistAppend("log", B("entries")));
            d.Issue(new PersistDelete("log"), 10);

            d.Crash(loss, () => draw);

            Assert.True(Read(d, "log") is "entries" or null, $"{loss}/{draw}");
        }
    }

    [Fact]
    public void LosingASyncedDeleteRestoresTheFile()
    {
        var d = Disk(new PersistAppend("log", B("entries")), new PersistDelete("log"));

        d.Crash(DiskLoss.LoseSynced, () => 0);

        Assert.Equal("entries", Read(d, "log"));
    }

    [Fact]
    public void ATornWriteAtKeepsAPrefixAtItsOffset()
    {
        var d = Disk(new PersistAppend("f", B("AAAAAAAA")));
        d.Issue(new PersistWriteAt("f", 4, B("bbbbbbbb")), 10);

        var fields = d.Crash(DiskLoss.Torn, () => 2); // keep = 1 + 2 % 7 = 3 bytes

        Assert.Contains(fields, x => x.Key == "kept");
        Assert.Equal("AAAAbbbA", Read(d, "f")); // three bytes at offset 4; byte 7 untouched
    }

    [Fact]
    public void AWriteAtInFlightAtAPendingCrashIsLostAndLosingASyncedOneRestoresTheOldBytes()
    {
        var pending = Disk(new PersistAppend("f", B("AAAA")));
        pending.Issue(new PersistWriteAt("f", 2, B("zz")), 10);
        pending.Crash(DiskLoss.Pending, () => 0);
        Assert.Equal("AAAA", Read(pending, "f"));

        var synced = Disk(new PersistAppend("f", B("AAAA")), new PersistWriteAt("f", 2, B("zzzz")));
        synced.Crash(DiskLoss.LoseSynced, () => 0);
        Assert.Equal("AAAA", Read(synced, "f"));
    }

    [Fact]
    public void ReorderedSurvivorsApplyInIssueOrderEvenWhenARenameDependsOnALostWrite()
    {
        // Pending: write the temp file, rename it over the snapshot. If only the rename survives,
        // it renames whatever temp file is durable — here none — and the snapshot must be untouched.
        var d = Disk(new PersistAppend("snap", B("old")));
        d.Issue(new PersistAppend("snap.tmp", B("new")), 10);
        d.Issue(new PersistRename("snap.tmp", "snap"), 11);
        var draws = new Queue<ulong>([0, 1]); // first write lost, rename survives

        d.Crash(DiskLoss.Reordered, draws.Dequeue);

        Assert.Equal("old", Read(d, "snap"));
        Assert.Null(Read(d, "snap.tmp"));
    }
}
