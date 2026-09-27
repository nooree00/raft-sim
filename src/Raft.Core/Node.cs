using System;
using System.Collections.Generic;

namespace Raft.Core;

/// <summary>
/// A node: a deterministic, closed state machine (spec §4). Same construction and same inputs give
/// the same effects.
/// </summary>
public interface INode
{
    IReadOnlyList<Effect> Handle(Input input);
}

/// <summary>The node's only randomness, injected at construction.</summary>
public interface IRandomSource
{
    ulong NextUInt64();
}

/// <summary>
/// Everything a node is built from. <see cref="Files"/> holds its durable files as the disk kept
/// them after recovery; there is no other way in, so volatile state cannot survive a restart.
/// </summary>
public sealed record NodeContext(
    NodeId Id,
    IReadOnlyList<NodeId> Peers,
    IRandomSource Random,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files);

/// <summary>Builds a node from its context — at first start and after every crash.</summary>
public delegate INode NodeFactory(NodeContext context);

/// <summary>The replicated state machine a Raft node applies committed commands to (phase 3 on).</summary>
public interface IStateMachine
{
    ReadOnlyMemory<byte> Apply(ReadOnlyMemory<byte> command);

    ReadOnlyMemory<byte> Snapshot();

    void Restore(ReadOnlyMemory<byte> snapshot);
}

/// <summary>Helpers over <see cref="IRandomSource"/>.</summary>
public static class RandomSourceExtensions
{
    /// <summary>A uniform integer in [0, <paramref name="maxExclusive"/>), without modulo bias.</summary>
    public static long NextLong(this IRandomSource random, long maxExclusive)
    {
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        var bound = (ulong)maxExclusive;
        var limit = ulong.MaxValue - ulong.MaxValue % bound;
        ulong x;
        do
        {
            x = random.NextUInt64();
        }
        while (x >= limit);

        return (long)(x % bound);
    }
}
