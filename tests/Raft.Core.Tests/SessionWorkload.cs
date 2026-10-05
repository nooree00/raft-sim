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
/// A late duplicate (P8-07): when a client's next sequence number is a multiple of
/// <see cref="LateEvery"/>, and the two numbers before it were both answered `ok`, it first sends
/// again the command two numbers back, as a request the network delivered late would arrive: the
/// server must refuse it as stale, and its operation was already answered, so the copy's reply
/// answers nothing (a copy of an unanswered command would close its operation after the client had
/// moved on, and the history would be malformed). It replaces the inner
/// workload's call and draws nothing, so the random stream is the one the inner workload would see.
/// </summary>
internal sealed class SessionWorkload(IClientWorkload inner, int universe) : IClientWorkload
{
    /// <summary>How many times a timed-out session command is sent again.</summary>
    public const int Retries = 3;

    /// <summary>Every this many sequence numbers, a late duplicate (P8-07).</summary>
    public const int LateEvery = 16;

    private readonly Dictionary<int, long> _session = [];
    private readonly Dictionary<int, long> _sequence = [];
    private readonly Dictionary<int, int> _retried = [];
    private readonly Dictionary<int, Dictionary<long, byte[]>> _sent = [];
    private readonly Dictionary<int, long> _lateAt = [];
    private readonly Dictionary<int, HashSet<long>> _answered = [];

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

        var next = _sequence.GetValueOrDefault(client) + 1;
        if (next % LateEvery == 0 && _lateAt.GetValueOrDefault(client) != next && _answered[client].Contains(next - 1) && _answered[client].Contains(next - 2) && _sent[client].TryGetValue(next - 2, out var late))
        {
            _lateAt[client] = next;
            return call with { Request = late };
        }

        _sequence[client] = next;
        var bytes = Encoding.ASCII.GetBytes(FormattableString.Invariant($"Session|{id}|{next}|{command}"));
        _sent[client].Remove(next - 2);
        _answered[client].Remove(next - 2);
        _sent[client][next] = bytes;
        return call with { Request = bytes };
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
            _sent[client] = [];
            _answered[client] = [];
        }
        else if (ClientHistory.InSession(Encoding.ASCII.GetString(request.Span)) is { } k && _session.GetValueOrDefault(client) == k.Session
            && (text == "ok" || text.StartsWith("ok|", StringComparison.Ordinal)))
        {
            _answered[client].Add(k.Sequence);
        }
    }
}
