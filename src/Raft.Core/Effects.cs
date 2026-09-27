using System;
using System.Collections.Generic;

namespace Raft.Core;

/// <summary>
/// Everything a node does leaves as an effect, in an ordered list (docs/design/node-interface.md §3).
/// A Send or ClientResponse is released only after every persist the same node emitted before it is
/// durable (§4, the persist barrier).
/// </summary>
public abstract record Effect;

/// <summary>Send bytes to a peer.</summary>
public sealed record Send(NodeId To, ReadOnlyMemory<byte> Payload) : Effect;

/// <summary>Answer a client.</summary>
public sealed record ClientResponse(long RequestId, ReadOnlyMemory<byte> Payload) : Effect;

/// <summary>A structured event for the trace; no effect on the world.</summary>
public sealed record Emit(string Name, IReadOnlyList<Field> Fields) : Effect;

/// <summary>One field of an <see cref="Emit"/>.</summary>
public readonly record struct Field(string Key, string Value);

/// <summary>A write to the node's durable files.</summary>
public abstract record Persist(string File) : Effect;

/// <summary>Append bytes to a file, creating it if needed.</summary>
public sealed record PersistAppend(string File, ReadOnlyMemory<byte> Data) : Persist(File);

/// <summary>Write bytes at an offset, creating the file if needed. Not atomic.</summary>
public sealed record PersistWriteAt(string File, long Offset, ReadOnlyMemory<byte> Data) : Persist(File);

/// <summary>Atomically replace <paramref name="To"/> with <paramref name="File"/>.</summary>
public sealed record PersistRename(string File, string To) : Persist(File);

/// <summary>Shorten a file to <paramref name="Length"/> bytes (truncating a log, dropping a torn tail).</summary>
public sealed record PersistTruncate(string File, long Length) : Persist(File);

/// <summary>Remove a file.</summary>
public sealed record PersistDelete(string File) : Persist(File);
