using System;
using System.Collections.Generic;
using System.Linq;

namespace Raft.Gates;

internal static class Program
{
    private static readonly Dictionary<string, (string Help, Func<Repo, string[], Findings> Run)> Commands = new(StringComparer.Ordinal)
    {
        ["each-commit"] = ("build and fast-test every non-head commit in the pushed range (spec §12)", EachCommit.Run),
        ["preflight"] = ("environment and pinning checks (spec §3, §10)", Preflight.Run),
        ["breakdown"] = ("every task has vacuity, sabotage, verifiability, prediction, outcome (spec §12)", Breakdown.Run),
        ["trailers"] = ("Task: trailers present, and each task's prediction committed first (spec §12)", Trailers.Run),
        ["register"] = ("deferred-items register vs phase status; every NotImplementedException listed (spec §12)", Register.Run),
        ["sabotage"] = ("run every sabotage/ entry from a committed tree; each must give its expected result", Sabotage.Run),
        ["testcount"] = ("per-project executed-test counts against ci/test-baseline.txt", TestCount.Run),
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
