using System;
using System.Collections.Generic;

namespace Raft.Core;

/// <summary>
/// The servers whose votes count (paper §6, phase 6). A simple configuration has <see cref="Old"/>
/// only; a joint one, `C_old,new`, also <see cref="New"/>, and then every decision needs a majority
/// of both. A configuration is a log entry and takes effect when it is appended, committed or not
/// (P6 decision 1); a node's configuration in effect is the latest one in its log, else the initial
/// configuration its context gives (decision 2), so a configuration truncated from the log takes its
/// effect with it. Entries whose command starts with <see cref="InternalMark"/> are the log's own,
/// never a client's: a leader refuses client commands that start with it, and the state machine
/// never sees them.
/// </summary>
public sealed record Configuration
{
    /// <summary>The first byte of an internal entry's command; client commands are text and never start with it.</summary>
    public const byte InternalMark = 0x00;

    private const byte Tag = (byte)'C', Simple = 1, Joint = 2;

    public Configuration(IEnumerable<NodeId> old, IEnumerable<NodeId>? @new = null)
    {
        ArgumentNullException.ThrowIfNull(old);
        Old = Canonical(old);
        New = @new is null ? null : Canonical(@new);
        if (Old.Count == 0 || New is { Count: 0 })
        {
            throw new ArgumentException("a configuration needs at least one server");
        }
    }

    public IReadOnlyList<NodeId> Old { get; }

    public IReadOnlyList<NodeId>? New { get; }

    public bool IsJoint => New is not null;

    /// <summary>Every server either configuration names, in order.</summary>
    public IReadOnlyList<NodeId> Members
    {
        get
        {
            if (New is null)
            {
                return Old;
            }

            var all = new List<NodeId>(Old);
            all.AddRange(New);
            return Canonical(all);
        }
    }

    /// <summary>Whether <paramref name="holders"/> are a majority of every configuration in this one.</summary>
    public bool IsQuorum(IReadOnlyList<NodeId> holders)
    {
        ArgumentNullException.ThrowIfNull(holders);
        return Majority(Old, holders) && (New is null || Majority(New, holders));
    }

    public bool Equals(Configuration? other) =>
        other is not null && Same(Old, other.Old) && (New is null ? other.New is null : other.New is not null && Same(New, other.New));

    private static bool Same(IReadOnlyList<NodeId> a, IReadOnlyList<NodeId> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode() => Old.Count ^ (New?.Count ?? -1);

    public override string ToString() =>
        New is null ? "{" + string.Join(",", Old) + "}" : "{" + string.Join(",", Old) + "}->{" + string.Join(",", New) + "}";

    /// <summary>The command of the entry that carries this configuration.</summary>
    public byte[] Encode()
    {
        var b = new List<byte> { InternalMark, Tag, New is null ? Simple : Joint };
        Put(b, Old);
        if (New is not null)
        {
            Put(b, New);
        }

        return b.ToArray();
    }

    /// <summary>The configuration an entry's command carries, or null when it carries none or is not canonical.</summary>
    public static Configuration? Decode(byte[] command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length < 3 || command[0] != InternalMark || command[1] != Tag || command[2] is not (Simple or Joint))
        {
            return null;
        }

        var at = 3;
        var old = Read(command, ref at);
        var @new = command[2] == Joint ? Read(command, ref at) : null;
        if (old is null || (command[2] == Joint && @new is null) || at != command.Length)
        {
            return null;
        }

        try
        {
            var c = new Configuration(old, @new);
            var canonical = c.Encode();
            if (canonical.Length != command.Length)
            {
                return null;
            }

            for (var i = 0; i < canonical.Length; i++)
            {
                if (canonical[i] != command[i])
                {
                    return null;
                }
            }

            return c;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whether an entry's command is the log's own (a configuration), not a client's.</summary>
    public static bool IsInternal(byte[] command) => command is { Length: > 0 } && command[0] == InternalMark;

    /// <summary>The configuration in effect after these commands, in log order: the latest configuration among them, else <paramref name="initial"/>.</summary>
    public static Configuration InEffect(Configuration initial, IEnumerable<byte[]> commandsInLogOrder)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(commandsInLogOrder);
        var current = initial;
        foreach (var command in commandsInLogOrder)
        {
            if (IsInternal(command) && Decode(command) is { } c)
            {
                current = c;
            }
        }

        return current;
    }

    private static bool Majority(IReadOnlyList<NodeId> members, IReadOnlyList<NodeId> holders)
    {
        var count = 0;
        foreach (var m in members)
        {
            foreach (var h in holders)
            {
                if (h == m)
                {
                    count++;
                    break;
                }
            }
        }

        return count * 2 > members.Count;
    }

    /// <summary>Sorted by value, without duplicates (an insertion sort: configurations are a handful of servers).</summary>
    private static List<NodeId> Canonical(IEnumerable<NodeId> ids)
    {
        var sorted = new List<NodeId>();
        foreach (var id in ids)
        {
            var at = 0;
            while (at < sorted.Count && sorted[at].Value < id.Value)
            {
                at++;
            }

            if (at == sorted.Count || sorted[at].Value != id.Value)
            {
                sorted.Insert(at, id);
            }
        }

        return sorted;
    }

    private static void Put(List<byte> b, IReadOnlyList<NodeId> ids)
    {
        b.Add((byte)ids.Count);
        foreach (var id in ids)
        {
            for (var shift = 24; shift >= 0; shift -= 8)
            {
                b.Add((byte)(id.Value >> shift));
            }
        }
    }

    private static List<NodeId>? Read(byte[] b, ref int at)
    {
        if (at >= b.Length)
        {
            return null;
        }

        var count = b[at++];
        if (count == 0 || at + (count * 4) > b.Length)
        {
            return null;
        }

        var ids = new List<NodeId>(count);
        for (var i = 0; i < count; i++)
        {
            ids.Add(new NodeId((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]));
            at += 4;
        }

        return ids;
    }
}
