using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Raft.Core;
using Raft.Simulation;

namespace Raft.Core.Tests;

/// <summary>
/// P8-02: a workload's clients with sessions (phase 8 decisions 1 to 3). A client without a session
/// registers first (`Register|`, to the node its next operation would have gone to; that operation
/// is not sent) and keeps the id the reply names. Its writes then carry the session and the next
/// sequence number (`Session|id|seq|command`); reads (`Get`) and membership requests carry none
/// (decision 4: a read changes nothing). A timed-out session command is sent again, the same bytes,
/// to the node the inner workload's retry draws, up to <see cref="Retries"/> times, and then given up
/// (it stays indeterminate) and the client continues with its next sequence number. A timed-out or
/// refused registration is not retried: the client registers again on its next operation.
/// </summary>
internal sealed class SessionWorkload(IClientWorkload inner, int universe) : IClientWorkload
{
    /// <summary>How many times a timed-out session command is sent again.</summary>
    public const int Retries = 3;

    private readonly Dictionary<int, long> _session = [];
    private readonly Dictionary<int, long> _sequence = [];
    private readonly Dictionary<int, int> _retried = [];

    public ClientCall? NextCall(int client, int sequence, IRandomSource random)
    {
        var call = inner.NextCall(client, sequence, random);
        if (call is null)
        {
            return null;
        }

        _retried[client] = 0;
        if (!_session.TryGetValue(client, out var id))
        {
            return call with { Request = Encoding.ASCII.GetBytes("Register|") };
        }

        var command = Encoding.ASCII.GetString(call.Request.Span);
        if (command.StartsWith("Get|", StringComparison.Ordinal) || command.StartsWith("Member|", StringComparison.Ordinal))
        {
            return call;
        }

        var next = _sequence[client] = _sequence.GetValueOrDefault(client) + 1;
        return call with { Request = Encoding.ASCII.GetBytes(FormattableString.Invariant($"Session|{id}|{next}|{command}")) };
    }

    public ClientCall? Retry(int client, ClientCall timedOut, IRandomSource random)
    {
        if (!Encoding.ASCII.GetString(timedOut.Request.Span).StartsWith("Session|", StringComparison.Ordinal) || _retried.GetValueOrDefault(client) >= Retries)
        {
            return null;
        }

        _retried[client] = _retried.GetValueOrDefault(client) + 1;
        var node = inner.Retry(client, timedOut, random)?.Node ?? new NodeId(1 + (int)random.NextLong(universe));
        return new ClientCall(node, timedOut.Request);
    }

    public void Replied(int client, ReadOnlyMemory<byte> request, ReadOnlyMemory<byte> reply)
    {
        var text = Encoding.ASCII.GetString(reply.Span);
        if (Encoding.ASCII.GetString(request.Span) == "Register|" && text.StartsWith("ok|", StringComparison.Ordinal)
            && long.TryParse(text.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            _session[client] = id;
            _sequence[client] = 0;
        }
    }
}
