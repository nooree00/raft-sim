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

/// <summary>An explicit list of faults.</summary>
public sealed record FaultSchedule(IReadOnlyList<Fault> Faults)
{
    public static FaultSchedule Empty { get; } = new([]);
}
