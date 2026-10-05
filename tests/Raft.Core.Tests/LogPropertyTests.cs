using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Kv;
using Raft.Simulation;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P7-02: the log's properties (docs/design/log-properties.md), one test each, every test taking
/// logs as input, generated and compacted (P7-06), so that compaction's effect on each is
/// visible. Vacuity risks: an enumeration from spec §8's list alone (guarded: the table derives
/// from every reader of the log); a property test compaction can never reach (guarded: each test
/// runs over <see cref="Logs"/>, half of them compacted). Sabotages S-logprop-1, S-logprop-2.
/// </summary>
public sealed class LogPropertyTests
{
    /// <summary>A log as the node holds it, the file its writes left, and the commands it applied in order.</summary>
    public sealed record Sample(string Name, LogStore Store, byte[] File, IReadOnlyList<byte[]> Commands);

    private static readonly Configuration Joint = new([new NodeId(1), new NodeId(2), new NodeId(3)], [new NodeId(1), new NodeId(2), new NodeId(4)]);

    /// <summary>
    /// Generated logs: appends of key-value commands and configuration entries in several terms, with
    /// truncations, every write applied to a simulated disk in order; then the same logs compacted at
    /// a random index (P7-06), the state the commands up to it built and the configuration in effect
    /// there in the snapshot, the compaction's file renamed over the log.
    /// </summary>
    public static IEnumerable<Sample> Logs()
    {
        for (var seed = 1; seed <= 80; seed++)
        {
            var rng = new Random(seed);
            var store = new LogStore(EntryLog.Recover(null));
            var disk = new SimDisk();
            var term = 1L;
            for (var step = 0; step < 12; step++)
            {
                if (store.LastIndex > 2 && rng.Next(4) == 0 && store.TruncateFrom(rng.Next(2, (int)store.LastIndex + 1)) is { } cut)
                {
                    disk.Issue(cut, 0);
                    disk.CompleteNext();
                    term++;
                }

                var batch = Enumerable.Range(0, rng.Next(1, 4)).Select(k => rng.Next(6) == 0
                    ? new LogEntry(new Term(term), Joint.Encode())
                    : new LogEntry(new Term(term), Encoding.ASCII.GetBytes($"Put|k{rng.Next(4)}|s{seed}x{step}y{k}"))).ToList();
                disk.Issue(store.Append(batch), 0);
                disk.CompleteNext();
            }

            var commands = Enumerable.Range(1, (int)store.LastIndex).Select(i => store.At(i).Command).ToList();
            if (seed > 40)
            {
                var index = rng.Next(1, (int)store.LastIndex + 1);
                var kv = new KvStateMachine();
                foreach (var c in commands.Take(index).Where(c => !Configuration.IsInternal(c)))
                {
                    kv.Apply(c);
                }

                var at = store.ConfigurationIndexAtOrBelow(index);
                foreach (var op in store.Compact(index, at == 0 ? null : Configuration.Decode(store.At(at).Command), kv.Snapshot().ToArray()))
                {
                    disk.Issue(op, 0);
                    disk.CompleteNext();
                }
            }

            var file = disk.Snapshot()[EntryLog.FileName].ToArray();
            yield return new Sample((seed > 40 ? "compacted " : "generated ") + seed, store, file, commands);
        }
    }

    public static TheoryData<int> Indexes() => new(Enumerable.Range(0, 80));

    private static Sample At(int i) => Logs().ElementAt(i);

    [Theory]
    [MemberData(nameof(Indexes))]
    public void EveryIndexResolves(int i)
    {
        var s = At(i);
        Assert.Equal(s.Store.Snapshot?.Term ?? Term.Zero, s.Store.TermAt(s.Store.BaseIndex));
        for (var index = s.Store.BaseIndex + 1; index <= s.Store.LastIndex; index++)
        {
            Assert.Equal(s.Store.At(index).Term, s.Store.TermAt(index));
        }

        // Below the snapshot nothing resolves: those entries are its state now.
        for (var index = 1L; index < s.Store.BaseIndex; index++)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => s.Store.TermAt(index));
        }
    }

    [Theory]
    [MemberData(nameof(Indexes))]
    public void IndicesAreDense(int i)
    {
        var s = At(i);
        for (var index = s.Store.BaseIndex + 1; index <= s.Store.LastIndex; index++)
        {
            Assert.Equal(index, s.Store.At(index).Index);
        }
    }

    /// <summary>Replaying the log from its snapshot's state (the empty state without one) gives the state the commands built; configuration entries never reach the state machine.</summary>
    [Theory]
    [MemberData(nameof(Indexes))]
    public void ReplayingTheLogGivesTheState(int i)
    {
        var s = At(i);
        var replayed = new KvStateMachine();
        var direct = new KvStateMachine();
        if (s.Store.Snapshot is { } snapshot)
        {
            replayed.Restore(snapshot.State);
        }

        for (var index = s.Store.BaseIndex + 1; index <= s.Store.LastIndex; index++)
        {
            var command = s.Store.At(index).Command;
            if (!Configuration.IsInternal(command))
            {
                replayed.Apply(command);
            }
        }

        foreach (var command in s.Commands.Where(c => !Configuration.IsInternal(c)))
        {
            direct.Apply(command);
        }

        foreach (var key in Enumerable.Range(0, 4).Select(k => "k" + k))
        {
            var get = Encoding.ASCII.GetBytes("Get|" + key);
            Assert.Equal(Encoding.ASCII.GetString(direct.Apply(get).Span), Encoding.ASCII.GetString(replayed.Apply(get).Span));
        }
    }

    /// <summary>The log recovered from its file answers every consistency-check question as the in-memory log does.</summary>
    [Theory]
    [MemberData(nameof(Indexes))]
    public void TheRecoveredLogAnswersAsTheLogDid(int i)
    {
        var s = At(i);
        var recovered = new LogStore(EntryLog.Recover(s.File));
        Assert.Equal(s.Store.LastIndex, recovered.LastIndex);
        Assert.Equal(s.Store.BaseIndex, recovered.BaseIndex);
        for (var prev = s.Store.BaseIndex; prev <= s.Store.LastIndex; prev++)
        {
            Assert.Equal(s.Store.TermAt(prev), recovered.TermAt(prev));
        }
    }

    /// <summary>A record whose previous-entry term does not match the entry before it is dropped: an append that survived a lost write it depended on.</summary>
    [Fact]
    public void AnUnchainedRecordIsDropped()
    {
        var file = EntryLog.Record(1, new Term(1), Term.Zero, Encoding.ASCII.GetBytes("a"))
            .Concat(EntryLog.Record(2, new Term(1), new Term(1), Encoding.ASCII.GetBytes("b")))
            .Concat(EntryLog.Record(3, new Term(3), new Term(2), Encoding.ASCII.GetBytes("stray")))
            .ToArray();

        var r = EntryLog.Recover(file);

        Assert.Equal(["a", "b"], r.Entries.Select(e => Encoding.ASCII.GetString(e.Command)));
    }

    /// <summary>A later record for an index discards every entry at that index or above.</summary>
    [Fact]
    public void ALaterRecordOverridesTheSuffix()
    {
        var file = EntryLog.Record(1, new Term(1), Term.Zero, Encoding.ASCII.GetBytes("a"))
            .Concat(EntryLog.Record(2, new Term(1), new Term(1), Encoding.ASCII.GetBytes("b")))
            .Concat(EntryLog.Record(3, new Term(1), new Term(1), Encoding.ASCII.GetBytes("c")))
            .Concat(EntryLog.Record(2, new Term(2), new Term(1), Encoding.ASCII.GetBytes("B")))
            .ToArray();

        var r = EntryLog.Recover(file);

        Assert.Equal(["a", "B"], r.Entries.Select(e => Encoding.ASCII.GetString(e.Command)));
    }

    /// <summary>A truncation cuts the file where the entry before it ended: the cut file recovers to the truncated log.</summary>
    [Theory]
    [MemberData(nameof(Indexes))]
    public void ATruncationCutsTheFileWhereTheEntryEnded(int i)
    {
        var s = At(i);
        for (var from = Math.Max(2L, s.Store.BaseIndex + 1); from <= s.Store.LastIndex; from++)
        {
            var store = new LogStore(EntryLog.Recover(s.File));
            var cut = store.TruncateFrom(from)!;
            var recovered = new LogStore(EntryLog.Recover(s.File[..(int)cut.Length]));
            Assert.Equal(from - 1, recovered.LastIndex);
            Assert.Equal(store.LastTerm, recovered.LastTerm);
        }
    }

    /// <summary>
    /// The configuration at every index is the latest configuration entry at or below it: found in the
    /// log after the snapshot, and in the snapshot when that entry is at or below its index.
    /// </summary>
    [Theory]
    [MemberData(nameof(Indexes))]
    public void TheConfigurationAtAnIndexIsTheLatestAtOrBelowIt(int i)
    {
        var s = At(i);
        for (var index = s.Store.BaseIndex; index <= s.Store.LastIndex; index++)
        {
            var expected = Enumerable.Range(1, (int)index).Where(k => Configuration.IsInternal(s.Commands[k - 1])).Select(k => (long)k).DefaultIfEmpty(0).Max();
            var found = s.Store.ConfigurationIndexAtOrBelow(index);
            if (expected > s.Store.BaseIndex)
            {
                Assert.Equal(expected, found);
            }
            else
            {
                Assert.Equal(0, found);
                Assert.Equal(expected == 0 ? null : Configuration.Decode(s.Commands[(int)expected - 1]), s.Store.Snapshot?.Configuration);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Indexes))]
    public void TheLastTermIsTheLastEntrysTerm(int i)
    {
        var s = At(i);
        Assert.Equal(s.Store.LastIndex == s.Store.BaseIndex ? s.Store.Snapshot?.Term ?? Term.Zero : s.Store.At(s.Store.LastIndex).Term, s.Store.LastTerm);
    }

    /// <summary>The checkers' incremental view of the file, fed the node's writes one by one, equals a full recovery of the file.</summary>
    [Theory]
    [MemberData(nameof(Indexes))]
    public void AnIncrementalViewOfTheFileEqualsItsRecovery(int i)
    {
        var s = At(i);
        var view = new LogHistory.FileView();
        var rng = new Random(i);
        var at = 0;
        while (at < s.File.Length)
        {
            var take = Math.Min(s.File.Length - at, rng.Next(1, 200));
            view.Apply(new PersistAppend(EntryLog.FileName, s.File.AsMemory(at, take)));
            at += take;
        }

        Assert.Equal(EntryLog.Recover(s.File).Entries.Count, view.Count);
        Assert.Equal(EntryLog.Recover(s.File).Snapshot?.Index, view.Snapshot?.Index);
    }
}
