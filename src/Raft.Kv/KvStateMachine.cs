using System;
using System.Collections.Generic;
using System.Text;
using Raft.Core;

namespace Raft.Kv;

/// <summary>
/// The key-value state machine (spec §6): `Get`, `Put`, `Append`, `CompareAndSwap`, `Delete`, each
/// on one key. Requests and replies are the text of P2-09's stores: "Put|k|v", "Append|k|v",
/// "Get|k", "Cas|k|expected|v" (expected "-" for absent), "Delete|k"; replies "ok" or "ok|output",
/// "-" for an absent value. An empty command is a leader's no-op (P4 decision 6) and changes
/// nothing. A malformed command is applied as nothing and answered "error": the log may hold any
/// bytes a client sent, and every node must treat them alike.
/// </summary>
public sealed class KvStateMachine : IStateMachine
{
    private readonly Dictionary<string, string> _state = new(StringComparer.Ordinal);

    public int Count => _state.Count;

    public ReadOnlyMemory<byte> Apply(ReadOnlyMemory<byte> command)
    {
        if (command.Length == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var p = Encoding.ASCII.GetString(command.Span).Split('|');
        if (p.Length < 2)
        {
            return Encoding.ASCII.GetBytes("error");
        }

        var present = _state.TryGetValue(p[1], out var current);
        string reply;
        switch (p[0])
        {
            case "Put" when p.Length == 3:
                _state[p[1]] = p[2];
                reply = "ok";
                break;
            case "Append" when p.Length == 3:
                _state[p[1]] = (present ? current : "") + p[2];
                reply = "ok";
                break;
            case "Get" when p.Length == 2:
                reply = "ok|" + (present ? current : "-");
                break;
            case "Delete" when p.Length == 2:
                _state.Remove(p[1]);
                reply = "ok";
                break;
            case "Cas" when p.Length == 4:
                var ok = p[2] == "-" ? !present : present && current == p[2];
                if (ok)
                {
                    _state[p[1]] = p[3];
                }

                reply = "ok|" + (ok ? "true" : "false");
                break;
            default:
                reply = "error";
                break;
        }

        return Encoding.ASCII.GetBytes(reply);
    }

    /// <summary>Snapshots arrive with log compaction (phase 7, register).</summary>
    public ReadOnlyMemory<byte> Snapshot() => throw new NotImplementedException("snapshots are phase 7 (log compaction)");

    /// <summary>Snapshots arrive with log compaction (phase 7, register).</summary>
    public void Restore(ReadOnlyMemory<byte> snapshot) => throw new NotImplementedException("snapshots are phase 7 (log compaction)");
}
