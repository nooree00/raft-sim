using System;

namespace Raft.Core;

/// <summary>Everything a node sees arrives as an input (docs/design/node-interface.md §2).</summary>
public abstract record Input;

/// <summary>Logical time passed for this node since its last tick, in the node's own units.</summary>
public sealed record Tick(long Elapsed) : Input;

/// <summary>A message arrived from a peer.</summary>
public sealed record Receive(NodeId From, ReadOnlyMemory<byte> Payload) : Input;

/// <summary>A client command or query arrived.</summary>
public sealed record ClientRequest(long RequestId, ReadOnlyMemory<byte> Payload) : Input;
