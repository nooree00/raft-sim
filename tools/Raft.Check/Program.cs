using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Raft.Checker;
using Raft.Core;
using Raft.Core.Tests;
using Raft.Host;
using Raft.Simulation;

namespace Raft.Check;

/// <summary>
/// `Raft.Check DIR`: the check of a Compose run (P9-08), reading `DIR/history.jsonl` (the real
/// client's), `DIR/events.jsonl` (every node's events, as `docker compose logs` gives them) and
/// `DIR/kill.txt` (the victim's id, the term it led when it was killed, and the kill's wall-clock
/// time in Unix milliseconds). Each guard is checked before the verdict, as in P9-05: the victim
/// led; another node led a later term; writes completed before and after the kill; an operation
/// spanned it; the restarted victim applied entries it had not held. Then WGL on the history,
/// retries merged. Exit 0 only if every guard holds and the history is linearizable.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: Raft.Check DIR (history.jsonl, events.jsonl, kill.txt)");
            return 2;
        }

        var dir = args[0];
        List<HistoryEntry> entries;
        using (var reader = new StreamReader(Path.Combine(dir, "history.jsonl")))
        {
            entries = RealClient.Read(reader);
        }

        var events = File.ReadAllLines(Path.Combine(dir, "events.jsonl")).Where(l => l.StartsWith('{')).Select(Parse).ToList();
        var kill = File.ReadAllText(Path.Combine(dir, "kill.txt")).Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var victim = kill[0];
        var status = kill[1];
        var killedAt = long.Parse(kill[2], CultureInfo.InvariantCulture);

        var failures = new List<string>();
        void Guard(bool holds, string what)
        {
            Console.WriteLine($"{(holds ? "ok  " : "FAIL")} {what}");
            if (!holds)
            {
                failures.Add(what);
            }
        }

        var parts = status.Split('|');
        var victimTerm = parts.Length == 3 && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : -1;
        Guard(parts.Length == 3 && parts[1] == "Leader", $"the killed node {victim} was the leader (its status before the kill: {status})");
        Guard(events.Any(e => e.Event == "leader" && e.Node != victim && Term(e) > victimTerm), $"another node led a term after {victimTerm}");
        long End(HistoryEntry e) => e.Wall + ((e.Response!.Value - e.Invoke) / 1000);
        bool Write(HistoryEntry e) => e.Request.StartsWith("Session|", StringComparison.Ordinal) && e.Response is not null && e.Reply == "ok";
        Guard(entries.Any(e => Write(e) && End(e) < killedAt), "a write completed before the kill");
        Guard(entries.Any(e => Write(e) && e.Wall > killedAt), "a write completed after the kill");
        Guard(entries.Any(e => e.Wall < killedAt && (e.Response is null || End(e) > killedAt)), "an operation spanned the kill");
        var mine = events.Where(e => e.Node == victim).ToList();
        var restart = mine.FindLastIndex(e => e.Event == "host-start");
        var before = mine.Take(Math.Max(0, restart)).Where(e => e.Event == "apply").Select(Index).DefaultIfEmpty(0).Max();
        Guard(restart > 0 && mine.Skip(restart).Any(e => e.Event == "apply" && Index(e) > before), $"the restarted {victim} applied entries past {before}");

        var log = entries.OrderBy(e => e.Invoke).ThenBy(e => e.RequestId)
            .Select(e => new ClientOp(e.Client, e.RequestId, new NodeId(e.Node), Encoding.ASCII.GetBytes(e.Request), e.Invoke, e.Response, Encoding.ASCII.GetBytes(e.Reply)))
            .ToList();
        var history = ClientHistory.From(log);
        Console.WriteLine($"history: {entries.Count} attempts; {history.History.Count} operations, {history.Completed} completed, {history.Indeterminate} indeterminate, {history.Refused} refused, {history.Retries} retries, {history.Registrations} registrations");
        Guard(history.Unexplained.Count == 0, "every reply explained: " + string.Join("; ", history.Unexplained.Take(3)));
        Guard(History.Problems(history.History).Count == 0, "the history is well formed");
        var verdict = WglChecker.Check(history.History, 32_000_000);
        Console.WriteLine($"linearizability: {verdict.Verdict}{(verdict.IsLinearizable ? "" : " at key " + verdict.Key)}, most states {verdict.StatesExplored}");
        return failures.Count == 0 && verdict.IsLinearizable ? 0 : 1;
    }

    private sealed record NodeEvent(string Node, string Event, IReadOnlyDictionary<string, string> Fields);

    private static NodeEvent Parse(string line)
    {
        var e = JsonDocument.Parse(line).RootElement;
        return new NodeEvent(e.GetProperty("Node").GetString()!, e.GetProperty("Event").GetString()!, e.GetProperty("Fields").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!));
    }

    private static long Term(NodeEvent e) => long.Parse(e.Fields["term"], CultureInfo.InvariantCulture);

    private static long Index(NodeEvent e) => long.Parse(e.Fields["index"], CultureInfo.InvariantCulture);
}
