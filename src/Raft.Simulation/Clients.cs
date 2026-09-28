using System;
using Raft.Core;

namespace Raft.Simulation;

/// <summary>
/// What a simulated client does next (P2-08): the node it sends to and the request's bytes, or null
/// when it is done. Called once per operation, in order, with the client's own random stream.
/// The simulator never interprets the bytes: the protocol under test and its checker do.
/// </summary>
public interface IClientWorkload
{
    ClientCall? NextCall(int client, int sequence, IRandomSource random);
}

public sealed record ClientCall(NodeId Node, ReadOnlyMemory<byte> Request);

/// <summary>
/// One client operation as the client saw it: invoked when sent, responded when a reply reached the
/// client. <see cref="Response"/> is null for an operation still unanswered at its timeout
/// (indeterminate): it may or may not have taken effect.
/// </summary>
public sealed record ClientOp(int Client, long RequestId, NodeId Node, ReadOnlyMemory<byte> Request, long Invoke, long? Response, ReadOnlyMemory<byte> Reply);
