using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Checker;
using Raft.Simulation;

namespace Raft.Core.Tests;

/// <summary>
/// P5-01: the simulator's client log as a checker history (P5 decisions 1, 2 and 4). A reply
/// "ok" or "ok|output" is a completed operation, its output read from the reply (Get: the value,
/// "-" for absent; Cas: "true" or "false"). A refusal ("redirect|…", "too-large|…") comes from a
/// node that did not append the command, so the operation did not take effect: it is left out and
/// counted. An operation unanswered at its timeout is indeterminate, and the client's later
/// operations belong to a fresh logical client, because a simulated client moves on while its
/// operation may still take effect, which a sequential client cannot express (Knossos, Jepsen). A
/// retry is a new operation with the same bytes. Any other reply is unexplained and reported.
/// P6-08: a membership request (`Member|…`) is no key-value operation; it is counted, never put in
/// the history. Its `busy|` answer is a definite failure (P6 decision 4, with the reviewer's
/// condition): counted, never indeterminate, and the client carries on as the same logical client.
/// Sabotage S-adapt-4.
/// P8-00 (phase 8 decision 1): a command in a session (`Session|id|seq|command`) is one operation per
/// (session, sequence number), however many times it was sent: invoked at its first attempt and
/// answered by the first attempt that was answered, indeterminate if none was. A retry is the same
/// operation, so a command applied twice is two effects of one operation, which no ordering
/// explains. A refused attempt is left out as before. `Register|` is no key-value operation: counted,
/// never put in the history. Sabotage S-adapt-5.
/// Vacuity risk: an adapter that drops operations makes any history pass (a lost write whose
/// operation is dropped leaves nothing to contradict). Guarded: every operation is accounted for,
/// and the counts must sum to the log. Sabotages S-adapt-1..3.
/// </summary>
public static class ClientHistory
{
    /// <summary>A logical client's id: the simulated client, plus 1,000 for each timeout it has had.</summary>
    public const int Generation = 1_000;

    public sealed record Result(IReadOnlyList<Operation> History, int Completed, int Indeterminate, int Refused, IReadOnlyList<string> Unexplained, int Membership = 0, int Busy = 0, int Registrations = 0, int Retries = 0)
    {
        /// <summary>Every client request is one of these: a session command's later attempts are counted as retries of its operation.</summary>
        public int Accounted => Completed + Indeterminate + Refused + Unexplained.Count + Membership + Registrations + Retries;
    }

    /// <summary>A command's session and sequence number, and the command inside, if it is in a session.</summary>
    public static (long Session, long Sequence, string Command)? InSession(string request)
    {
        var p = request.Split('|', 4);
        return p.Length == 4 && p[0] == "Session" && long.TryParse(p[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            && long.TryParse(p[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seq)
            ? (id, seq, p[3])
            : null;
    }

    /// <summary>
    /// <paramref name="freshClientAfterTimeout"/> false is the variant P5-01's prediction measures:
    /// a timed-out client's later operations stay on the same logical client.
    /// </summary>
    public static Result From(IReadOnlyList<ClientOp> log, bool freshClientAfterTimeout = true)
    {
        var generation = new Dictionary<int, int>();
        var history = new List<Operation>();
        var unexplained = new List<string>();
        int completed = 0, indeterminate = 0, refused = 0, membership = 0, busy = 0, registrations = 0, retries = 0;

        // P8-00: the attempts of each session command, in log order, so that each becomes one operation.
        var attempts = new Dictionary<(long, long), List<ClientOp>>();
        foreach (var o in log)
        {
            if (InSession(Encoding.ASCII.GetString(o.Request.Span)) is { } k)
            {
                (attempts.TryGetValue((k.Session, k.Sequence), out var l) ? l : attempts[(k.Session, k.Sequence)] = []).Add(o);
            }
        }

        foreach (var o in log)
        {
            var client = (generation.GetValueOrDefault(o.Client) * Generation) + o.Client;
            var request = Encoding.ASCII.GetString(o.Request.Span);
            if (request.StartsWith("Register|", StringComparison.Ordinal))
            {
                registrations++;
                if (o.Response is null && freshClientAfterTimeout)
                {
                    generation[o.Client] = generation.GetValueOrDefault(o.Client) + 1;
                }

                continue;
            }

            if (InSession(request) is { } session)
            {
                var all = attempts[(session.Session, session.Sequence)];
                if (!ReferenceEquals(all[0], o))
                {
                    retries++;
                    continue;
                }

                var answered = all.FirstOrDefault(a => a.Response is not null && Answer(Encoding.ASCII.GetString(a.Reply.Span)));
                request = session.Command;
                if (answered is null)
                {
                    if (all.Any(a => a.Response is null))
                    {
                        history.Add(Decode(client, request, o.Invoke, null, null));
                        indeterminate++;
                        if (freshClientAfterTimeout)
                        {
                            generation[o.Client] = generation.GetValueOrDefault(o.Client) + 1;
                        }
                    }
                    else
                    {
                        refused++;
                    }

                    continue;
                }

                var text = Encoding.ASCII.GetString(answered.Reply.Span);
                history.Add(Decode(client, request, o.Invoke, answered.Response, text.Length > 3 ? text[3..] : null));
                completed++;
                continue;
            }

            if (request.StartsWith("Member|", StringComparison.Ordinal))
            {
                membership++;
                if (o.Response is null && freshClientAfterTimeout)
                {
                    generation[o.Client] = generation.GetValueOrDefault(o.Client) + 1;
                }
                else if (o.Response is not null && Encoding.ASCII.GetString(o.Reply.Span).StartsWith("busy|", StringComparison.Ordinal))
                {
                    busy++;
                }

                continue;
            }

            if (o.Response is null)
            {
                history.Add(Decode(client, request, o.Invoke, null, null));
                indeterminate++;
                if (freshClientAfterTimeout)
                {
                    generation[o.Client] = generation.GetValueOrDefault(o.Client) + 1;
                }

                continue;
            }

            var reply = Encoding.ASCII.GetString(o.Reply.Span);
            if (reply.StartsWith("redirect|", StringComparison.Ordinal) || reply.StartsWith("too-large|", StringComparison.Ordinal))
            {
                refused++;
            }
            else if (reply == "ok" || reply.StartsWith("ok|", StringComparison.Ordinal))
            {
                history.Add(Decode(client, request, o.Invoke, o.Response, reply.Length > 3 ? reply[3..] : null));
                completed++;
            }
            else
            {
                unexplained.Add($"client {o.Client} request {o.RequestId} '{request}': reply '{reply}'");
            }
        }

        return new Result(history, completed, indeterminate, refused, unexplained, membership, busy, registrations, retries);
    }

    private static bool Answer(string reply) => reply == "ok" || reply.StartsWith("ok|", StringComparison.Ordinal);

    private static Operation Decode(int client, string request, long invoke, long? response, string? output)
    {
        var p = request.Split('|');
        var op = p[0] switch
        {
            "Put" => new Operation(client, OpKind.Put, p[1], invoke, response, Value: p[2]),
            "Append" => new Operation(client, OpKind.Append, p[1], invoke, response, Value: p[2]),
            "Get" => new Operation(client, OpKind.Get, p[1], invoke, response),
            "Cas" => new Operation(client, OpKind.CompareAndSwap, p[1], invoke, response, Value: p[3], Expected: p[2] == "-" ? null : p[2]),
            "Delete" => new Operation(client, OpKind.Delete, p[1], invoke, response),
            _ => throw new FormatException($"not a KV request: '{request}'"),
        };
        return response is not null && op.Kind is OpKind.Get or OpKind.CompareAndSwap
            ? op with { Output = output == "-" ? null : output }
            : op;
    }

    /// <summary>The operation kinds a history contains, for decision 5's floor on the workload.</summary>
    public static IReadOnlySet<OpKind> Kinds(IReadOnlyList<Operation> history) => history.Select(o => o.Kind).ToHashSet();
}
