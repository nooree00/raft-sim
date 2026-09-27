using System;
using System.Collections.Generic;
using System.Linq;

namespace Raft.Gates;

internal static class Program
{
    private static readonly Dictionary<string, (string Help, Func<Repo, string[], Findings> Run)> Commands = new(StringComparer.Ordinal)
    {
        ["preflight"] = ("environment and pinning checks (spec §3, §10)", Preflight.Run),
    };

    private static int Main(string[] args)
    {
        if (args.Length == 0 || !Commands.TryGetValue(args[0], out var command))
        {
            Console.Error.WriteLine("usage: gates <command> [--root DIR] [options]");
            foreach (var (name, (help, _)) in Commands.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                Console.Error.WriteLine($"  {name,-12} {help}");
            }

            return 2;
        }

        var rest = args.Skip(1).ToList();
        var root = Options.Take(rest, "--root");
        var repo = Repo.Locate(root);

        var findings = command.Run(repo, rest.ToArray());
        return findings.Report(args[0], Console.Out, Console.Error);
    }
}
