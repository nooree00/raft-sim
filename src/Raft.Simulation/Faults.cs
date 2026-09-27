using System.Collections.Generic;
using Raft.Core;

namespace Raft.Simulation;

/// <summary>
/// A fault in a schedule (spec §7): data, not behaviour. A schedule is an explicit list of faults;
/// the runner executes it, the generator produces it from a seed, the shrinker removes from it.
/// </summary>
public abstract record Fault(long At);

/// <summary>A fault on the directed link From→To.</summary>
public abstract record LinkFault(long At, NodeId From, NodeId To) : Fault(At);

/// <summary>The first message released on the link at or after <c>At</c> is lost.</summary>
public sealed record Drop(long At, NodeId From, NodeId To) : LinkFault(At, From, To);

/// <summary>The first message released on the link at or after <c>At</c> is delivered twice.</summary>
public sealed record Duplicate(long At, NodeId From, NodeId To) : LinkFault(At, From, To);

/// <summary>The first message released on the link at or after <c>At</c> takes <c>Extra</c> longer.</summary>
public sealed record Delay(long At, NodeId From, NodeId To, long Extra) : LinkFault(At, From, To);

/// <summary>
/// The first message released on the link at or after <c>At</c> is held until the next message on
/// the link has been delivered, then delivered: the two arrive in the opposite order to sending.
/// </summary>
public sealed record Reorder(long At, NodeId From, NodeId To) : LinkFault(At, From, To);

/// <summary>From <c>At</c>, messages released on From→To are blocked (one direction only).</summary>
public sealed record Partition(long At, NodeId From, NodeId To) : LinkFault(At, From, To);

/// <summary>From <c>At</c>, From→To carries messages again.</summary>
public sealed record Heal(long At, NodeId From, NodeId To) : LinkFault(At, From, To);

/// <summary>What a crash does to a node's disk (docs/design/node-interface.md §4).</summary>
public enum DiskLoss
{
    /// <summary>Writes in flight are lost; completed writes are durable.</summary>
    Pending,

    /// <summary>The oldest write in flight survives as a prefix (a torn final record); the rest are lost.</summary>
    Torn,

    /// <summary>An arbitrary subset of the writes in flight survives (reordered completion).</summary>
    Reordered,

    /// <summary>Positive control only: the disk also loses its last completed write. Raft cannot survive it.</summary>
    LoseSynced,
}

/// <summary>A fault on one node.</summary>
public abstract record NodeFault(long At, NodeId Node) : Fault(At);

/// <summary>The node crashes: its object is discarded, held messages vanish, its disk meets <c>Loss</c>.</summary>
public sealed record Crash(long At, NodeId Node, DiskLoss Loss) : NodeFault(At, Node);

/// <summary>The node is rebuilt from what its disk kept.</summary>
public sealed record Restart(long At, NodeId Node) : NodeFault(At, Node);

/// <summary>Until <c>Until</c>, the node's writes take at least <c>Latency</c> to complete.</summary>
public sealed record SlowDisk(long At, NodeId Node, long Latency, long Until) : NodeFault(At, Node);

/// <summary>Positive control only: until <c>Until</c>, the node's sends ignore the persist barrier.</summary>
public sealed record BarrierViolation(long At, NodeId Node, long Until) : NodeFault(At, Node);

/// <summary>The node stops receiving ticks and deliveries; its object and state are kept.</summary>
public sealed record Pause(long At, NodeId Node) : NodeFault(At, Node);

/// <summary>The node receives one tick covering the pause, then the messages held for it, in arrival order.</summary>
public sealed record Unpause(long At, NodeId Node) : NodeFault(At, Node);

/// <summary>From <c>At</c>, the node perceives <c>Numerator</c>/<c>Denominator</c> units per real unit.</summary>
public sealed record Skew(long At, NodeId Node, long Numerator, long Denominator) : NodeFault(At, Node);

/// <summary>An explicit list of faults.</summary>
public sealed record FaultSchedule(IReadOnlyList<Fault> Faults)
{
    public static FaultSchedule Empty { get; } = new([]);
}
