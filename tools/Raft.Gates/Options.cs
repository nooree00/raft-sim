using System;
using System.Collections.Generic;

namespace Raft.Gates;

internal static class Options
{
    /// <summary>Removes "--name value" from args and returns value, or null if absent.</summary>
    public static string? Take(List<string> args, string name)
    {
        var i = args.IndexOf(name);
        if (i < 0)
        {
            return null;
        }

        if (i + 1 >= args.Count)
        {
            throw new ArgumentException($"{name} needs a value");
        }

        var value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }

    /// <summary>Removes "--name" from args and returns whether it was present.</summary>
    public static bool Flag(List<string> args, string name) => args.Remove(name);
}
