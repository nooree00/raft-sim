using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Kv;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P7-03: the key-value state machine's snapshot (register row). Restoring a snapshot and applying
/// the rest equals applying everything, and equal states give equal bytes, so a snapshot is a
/// function of the state alone. Vacuity risk: a round trip over a small or one-kind state passes a
/// serialisation that drops what it never saw; guarded by sequences using every command kind on
/// every key, compared reply by reply, key by key and byte for byte. Sabotages S-kvsnap-1, S-kvsnap-2.
/// </summary>
public sealed class KvSnapshotTests
{
    private static readonly string[] Keys = ["k0", "k1", "k2", "k3", "k4", "k5"];

    private static List<string> Commands(int seed, int count)
    {
        var rng = new Random(seed);
        var commands = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var key = Keys[rng.Next(Keys.Length)];
            commands.Add(rng.Next(6) switch
            {
                0 => $"Put|{key}|v{i}",
                1 => $"Append|{key}|a{i}",
                2 => $"Get|{key}",
                3 => $"Delete|{key}",
                4 => $"Cas|{key}|-|c{i}",
                _ => $"Cas|{key}|v{i - 1}|d{i}",
            });
        }

        return commands;
    }

    private static string Apply(KvStateMachine sm, string command) => Encoding.ASCII.GetString(sm.Apply(Encoding.ASCII.GetBytes(command)).Span);

    /// <summary>Applying a prefix, snapshotting, restoring into a fresh machine and applying the rest gives every reply and every value applying everything gives.</summary>
    [Fact]
    public void RestoringASnapshotAndApplyingTheRestEqualsApplyingEverything()
    {
        for (var seed = 1; seed <= 200; seed++)
        {
            var commands = Commands(seed, 60);
            var split = new Random(seed * 7).Next(commands.Count + 1);
            var whole = new KvStateMachine();
            var expected = commands.Select(c => Apply(whole, c)).ToList();

            var before = new KvStateMachine();
            foreach (var c in commands.Take(split))
            {
                Apply(before, c);
            }

            var after = new KvStateMachine();
            after.Restore(before.Snapshot());
            var replies = commands.Skip(split).Select(c => Apply(after, c)).ToList();

            Assert.Equal(expected.Skip(split), replies);
            Assert.Equal(Keys.Select(k => Apply(whole, "Get|" + k)), Keys.Select(k => Apply(after, "Get|" + k)));
            Assert.Equal(whole.Snapshot().ToArray(), after.Snapshot().ToArray());
        }
    }

    /// <summary>Two machines with equal contents reached by different histories give the same snapshot bytes.</summary>
    [Fact]
    public void EqualStatesGiveEqualSnapshots()
    {
        var a = new KvStateMachine();
        var b = new KvStateMachine();
        foreach (var c in new[] { "Put|k1|x", "Put|k2|y", "Put|k3|z" })
        {
            Apply(a, c);
        }

        foreach (var c in new[] { "Put|k3|z", "Put|k0|w", "Put|k2|y", "Delete|k0", "Put|k1|x" })
        {
            Apply(b, c);
        }

        Assert.Equal(a.Snapshot().ToArray(), b.Snapshot().ToArray());
    }

    /// <summary>The empty state round-trips, and a restore replaces whatever the machine held.</summary>
    [Fact]
    public void ARestoreReplacesTheState()
    {
        var empty = new KvStateMachine().Snapshot();
        var sm = new KvStateMachine();
        Apply(sm, "Put|k1|x");
        sm.Restore(empty);
        Assert.Equal("ok|-", Apply(sm, "Get|k1"));
        Assert.Equal(0, sm.Count);
    }
}
