using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P3-03: the term-and-vote file (spec §8). Vacuity risk: recovery tested only on files the writer
/// produced never meets a torn or corrupt record, so "takes the last valid record" and "refuses on
/// corruption" go untested. Guarded: hand-built files for every path, and crash-mode runs on the
/// simulated disk that count which path recovery took. Sabotages S-pstate-1..3.
/// </summary>
public sealed class TermVoteLogTests
{
    private static readonly NodeId N2 = new(2), N3 = new(3);

    private static byte[] File(params byte[][] records) => records.SelectMany(r => r).ToArray();

    private static byte[] R(long term, NodeId? vote) => TermVoteLog.Record(new Term(term), vote);

    [Fact]
    public void NoFileIsTermZeroWithNoVote()
    {
        Assert.Equal((RecoveryPath.Empty, TermVoteState.Initial), (TermVoteLog.Recover(null).Path, TermVoteLog.Recover(null).State));
        Assert.Equal(RecoveryPath.Empty, TermVoteLog.Recover([]).Path);
    }

    [Fact]
    public void TheLastValidRecordWins()
    {
        var r = TermVoteLog.Recover(File(R(1, null), R(2, N2), R(2, N3), R(5, null)));

        Assert.Equal(RecoveryPath.Clean, r.Path);
        Assert.Equal(new TermVoteState(new Term(5), null), r.State);
        Assert.Equal(4 * 20, r.ValidLength);
    }

    /// <summary>A torn final record, cut at every length: the state is the record before it.</summary>
    [Fact]
    public void ATornFinalRecordIsCutAtEveryLength()
    {
        var whole = R(3, N2);
        for (var keep = 1; keep < whole.Length; keep++)
        {
            var r = TermVoteLog.Recover(File(R(2, N3), whole[..keep]));

            Assert.True(r.Path == RecoveryPath.TruncatedTornTail, $"cut at {keep}: {r.Path} ({r.Detail})");
            Assert.Equal(new TermVoteState(new Term(2), N3), r.State);
            Assert.Equal(20, r.ValidLength);
        }
    }

    [Fact]
    public void AFinalRecordFailingItsChecksumIsTornAndCut()
    {
        var bad = R(3, N2);
        bad[10] ^= 1;
        var r = TermVoteLog.Recover(File(R(2, N3), bad));

        Assert.Equal(RecoveryPath.TruncatedTornTail, r.Path);
        Assert.Equal(new TermVoteState(new Term(2), N3), r.State);
    }

    [Fact]
    public void ACorruptRecordBeforeTheLastRefusesToStart()
    {
        var bad = R(2, N3);
        bad[10] ^= 1;
        var r = TermVoteLog.Recover(File(R(1, null), bad, R(3, N2)));

        Assert.True(r.Path == RecoveryPath.Refused, $"corruption before the last record gave {r.Path}: {r.Detail}");
    }

    /// <summary>A record whose checksum is valid over the wrong length: whole, well-formed as a record, and not ours. Last or not, corruption.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AValidChecksumOverTheWrongLengthRefuses(bool last)
    {
        var body = new List<byte> { 0, 0, 0, 13 };
        body.AddRange(new byte[13]);
        var h = 2166136261u;
        foreach (var b in body)
        {
            h = (h ^ b) * 16777619u;
        }

        body.AddRange([(byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h]);
        var file = last ? File(R(1, null), [.. body]) : File(R(1, null), [.. body], R(2, N2));

        Assert.Equal(RecoveryPath.Refused, TermVoteLog.Recover(file).Path);
    }

    /// <summary>
    /// Writes in flight at a crash, under every crash mode of the simulated disk. Recovery must never
    /// refuse (a crash is the normal case, spec §8), and must restore the last record that survived.
    /// The count of each path per mode is written beside the assembly (P3-03's prediction).
    /// </summary>
    [Fact]
    public void EveryCrashModeRecoversToTheLastRecordThatSurvived()
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var mode in new[] { DiskLoss.Pending, DiskLoss.Torn, DiskLoss.Reordered })
        {
            for (var seed = 1; seed <= 200; seed++)
            {
                var rng = new Random(seed);
                var disk = new SimDisk();
                var durable = rng.Next(0, 3);
                var inFlight = rng.Next(1, 4);
                var records = Enumerable.Range(1, durable + inFlight).Select(i => R(i, i % 2 == 0 ? N2 : null)).ToList();
                for (var i = 0; i < records.Count; i++)
                {
                    disk.Issue(new PersistAppend(TermVoteLog.FileName, records[i]), 0);
                    if (i < durable)
                    {
                        disk.CompleteNext();
                    }
                }

                var draws = new Random(seed * 31);
                disk.Crash(mode, () => (ulong)draws.NextInt64());
                var file = disk.Snapshot().TryGetValue(TermVoteLog.FileName, out var m) ? m.ToArray() : null;
                var r = TermVoteLog.Recover(file);
                var key = $"{mode}/{r.Path}";
                counts[key] = counts.GetValueOrDefault(key) + 1;

                Assert.True(r.Path != RecoveryPath.Refused, $"{mode}, seed {seed}: a crash made recovery refuse: {r.Detail}");
                var whole = (file?.Length ?? 0) / 20;
                var expected = whole == 0 ? TermVoteState.Initial : TermVoteLog.Recover(file![..(whole * 20)]).State;
                Assert.Equal(expected, r.State);
            }
        }

        System.IO.File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "term-vote-recovery.txt"), string.Join("\n", counts.Select(kv => $"{kv.Key} {kv.Value}")) + "\n");
        Assert.True(counts.Keys.Any(k => k.EndsWith("/TruncatedTornTail", StringComparison.Ordinal)), "no crash tore a record: the truncation path was never exercised");
    }

    /// <summary>
    /// P6-15, found by the membership soak (seed 1462): a node recovers a torn final record, issues the
    /// cut and then a record, and a crash with a reordered loss keeps the record and loses the cut (the
    /// disk model lets any subset of writes in flight survive, and the node never learns what is
    /// durable). The record now sits after the torn bytes. A torn write keeps less than a whole
    /// record, so a valid record starts within one record's length of it, which corruption of a whole
    /// record never gives; recovery skips the torn bytes and takes the record, at every torn length.
    /// Below four bytes the torn record's length field is garbage, and reading it as a torn tail would
    /// cut the later record away, losing a vote that may have been durable. Sabotage S-pstate-4.
    /// </summary>
    [Fact]
    public void ATornRecordFollowedByARecordWhoseCutWasLostRecoversTheRecord()
    {
        var torn = R(2, N2);
        for (var keep = 1; keep < torn.Length; keep++)
        {
            var disk = new SimDisk();
            disk.Issue(new PersistAppend(TermVoteLog.FileName, R(1, null)), 0);
            disk.CompleteNext();
            disk.Issue(new PersistAppend(TermVoteLog.FileName, torn), 0);
            disk.Crash(DiskLoss.Torn, () => (ulong)(keep - 1));
            var first = TermVoteLog.Recover(disk.Snapshot()[TermVoteLog.FileName].ToArray());
            Assert.Equal((RecoveryPath.TruncatedTornTail, 20), (first.Path, first.ValidLength));

            // The restarted node's cut and its next record, both in flight; the crash keeps only the record.
            disk.Issue(new PersistTruncate(TermVoteLog.FileName, first.ValidLength), 0);
            disk.Issue(new PersistAppend(TermVoteLog.FileName, R(3, N3)), 0);
            var draws = new Queue<ulong>([0, 1]);
            disk.Crash(DiskLoss.Reordered, draws.Dequeue);
            var file = disk.Snapshot()[TermVoteLog.FileName].ToArray();
            Assert.Equal(File(R(1, null), torn[..keep], R(3, N3)), file);

            var r = TermVoteLog.Recover(file);
            Assert.True(r.Path != RecoveryPath.Refused, $"torn at {keep}: refused: {r.Detail}");
            Assert.Equal(new TermVoteState(new Term(3), N3), r.State);
            Assert.Equal(file.Length, r.ValidLength);
        }
    }
}
