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

    /// <summary>
    /// The state as canonical bytes (P7-03): the number of keys, then each key and its value, keys in
    /// ordinal order, each as a 4-byte big-endian length and UTF-8. Ordered by key, not by the
    /// dictionary's enumeration, which depends on the order keys were inserted and removed: two equal
    /// states must give equal bytes (the first implementation did not).
    /// </summary>
    public ReadOnlyMemory<byte> Snapshot()
    {
        var b = new List<byte>();
        Put(b, _state.Count);
        var keys = new List<string>(_state.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            PutText(b, key);
            PutText(b, _state[key]);
        }

        return b.ToArray();
    }

    /// <summary>Replaces the state with the one <see cref="Snapshot"/> wrote.</summary>

    public void Restore(ReadOnlyMemory<byte> snapshot)
    {
        _state.Clear();
        var span = snapshot.Span;
        var at = 0;
        var count = Get(span, ref at);
        for (var i = 0; i < count; i++)
        {
            var key = GetText(span, ref at);
            _state[key] = GetText(span, ref at);
        }
    }

    private static void Put(List<byte> b, int v)
    {
        b.Add((byte)(v >> 24));
        b.Add((byte)(v >> 16));
        b.Add((byte)(v >> 8));
        b.Add((byte)v);
    }

    private static void PutText(List<byte> b, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Put(b, bytes.Length);
        b.AddRange(bytes);
    }

    private static int Get(ReadOnlySpan<byte> s, ref int at)
    {
        var v = (s[at] << 24) | (s[at + 1] << 16) | (s[at + 2] << 8) | s[at + 3];
        at += 4;
        return v;
    }

    private static string GetText(ReadOnlySpan<byte> s, ref int at)
    {
        var n = Get(s, ref at);
        var text = Encoding.UTF8.GetString(s.Slice(at, n));
        at += n;
        return text;
    }
}
