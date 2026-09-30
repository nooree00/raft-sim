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

/// <summary>A message handed to the receiving node's Handle, as step <paramref name="Step"/> of the run (P4-01: provenance of what the node writes in that step).</summary>
public sealed record DeliveredObservation(long Time, NodeId Node, NodeId From, long Id, long Step = 0) : Observation(Time, Node);

/// <summary>
/// A durable file changed. Either a write completed (<paramref name="Completed"/>, and
/// <paramref name="Content"/> is null: apply it to the file as it was), or a crash left the file as
/// <paramref name="Content"/> (a full snapshot; null when the file no longer exists). Recording the
/// write rather than the file's whole content keeps an observed run's cost proportional to what it
/// wrote (P4-03: full contents per write exhausted memory once nodes wrote logs).
/// </summary>
public sealed record DurableObservation(long Time, NodeId Node, string File, ReadOnlyMemory<byte>? Content, Persist? Completed = null) : Observation(Time, Node);

/// <summary>A write the node issued in step <paramref name="Step"/>, with its bytes: what the node decided, before the disk made it durable (it may be lost in a crash).</summary>
public sealed record IssuedObservation(long Time, NodeId Node, Persist Op, long Step = 0) : Observation(Time, Node);

/// <summary>An event the node emitted: the node's own report, recorded as such (P4 decision 3: "apply" has no state to derive it from).</summary>
public sealed record EmittedObservation(long Time, NodeId Node, Emit Event, long Step) : Observation(Time, Node);

/// <summary>A node crashed (its durable changes, if any, follow as <see cref="DurableObservation"/>s).</summary>
public sealed record CrashObservation(long Time, NodeId Node) : Observation(Time, Node);

/// <summary>A node started, or restarted from its disk.</summary>
public sealed record StartObservation(long Time, NodeId Node, int Incarnation) : Observation(Time, Node);
