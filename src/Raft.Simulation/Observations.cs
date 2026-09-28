using System;
using Raft.Core;

namespace Raft.Simulation;

/// <summary>
/// What the simulator saw happen, with the bytes (P3-04): the invariant checkers read these, never
/// a node's report about itself. Recorded only when <see cref="Simulator.Observe"/> is set, so the
/// canonical trace and the cost of an unobserved run are unchanged. The simulator stays ignorant of
/// Raft: payloads and file contents are bytes.
/// </summary>
public abstract record Observation(long Time, NodeId Node);

/// <summary>A message released by the barrier and handed to the network (it may still be dropped or blocked).</summary>
public sealed record SentObservation(long Time, NodeId Node, NodeId To, long Id, ReadOnlyMemory<byte> Payload) : Observation(Time, Node);

/// <summary>A message handed to the receiving node's Handle.</summary>
public sealed record DeliveredObservation(long Time, NodeId Node, NodeId From, long Id) : Observation(Time, Node);

/// <summary>A durable file's content after a write completed or a crash changed it; null when the file no longer exists.</summary>
public sealed record DurableObservation(long Time, NodeId Node, string File, ReadOnlyMemory<byte>? Content) : Observation(Time, Node);

/// <summary>A node crashed (its durable changes, if any, follow as <see cref="DurableObservation"/>s).</summary>
public sealed record CrashObservation(long Time, NodeId Node) : Observation(Time, Node);

/// <summary>A node started, or restarted from its disk.</summary>
public sealed record StartObservation(long Time, NodeId Node, int Incarnation) : Observation(Time, Node);
