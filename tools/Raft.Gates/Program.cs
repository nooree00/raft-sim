using System;

namespace Raft.Gates;

internal static class Program
{
    private static int Main()
    {
        Console.Error.WriteLine("usage: gates <command> [options]");
        Console.Error.WriteLine("no commands implemented yet (phase 0 in progress)");
        return 2;
    }
}
