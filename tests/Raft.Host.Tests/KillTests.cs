using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Raft.Checker;
using Raft.Core;
using Raft.Core.Tests;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P9-05: a leader killed and restarted in one test process, with the real client's load running
/// through it, and the WGL checker on the client's history (retries merged, P8-00). Each guard is
/// asserted before the verdict: the killed host was the leader; another host led a later term
/// after it; writes completed before the kill and after it; an operation was invoked before the kill
/// and answered, or left indeterminate, after it; the restarted host applied entries it had not
/// held. Vacuity risk: a kill on a follower, or after the load ended, tests nothing about a leader's
/// death; guarded by those guards. Sabotage S-kill-1 (the orchestration kills a follower).
/// </summary>
public sealed class KillTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static (string Node, string Event, IReadOnlyDictionary<string, string> Fields) Parse(string line)
    {
        var e = JsonDocument.Parse(line).RootElement;
        return (e.GetProperty("Node").GetString()!, e.GetProperty("Event").GetString()!, e.GetProperty("Fields").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!));
    }

    private static long Index(IReadOnlyDictionary<string, string> f) => long.Parse(f["index"], CultureInfo.InvariantCulture);

    /// <summary>Picks the host to kill: the leader (S-kill-1 picks a follower).</summary>
    private static NodeId Victim(HostCluster c, NodeId leader) => leader;

    [Fact]
    public async Task ALeaderKilledAndRestartedLeavesAHistoryTheCheckerAccepts()
    {
        await using var c = new HostCluster();
        c.StartAll();
        var leader = (await c.LeaderAsync(Wait, Ct))!.Value;

        var clock = Stopwatch.StartNew();
        var entries = new List<HistoryEntry>();
        var config = new ClientConfig(c.Nodes.ToDictionary(n => n, n => new DnsEndPoint("127.0.0.1", c.ClientEndpoint(n).Port)), Clients: 3, Duration: TimeSpan.FromSeconds(8), Timeout: TimeSpan.FromMilliseconds(500));
        var load = RealClient.RunAsync(config, entries.Add, Ct, clock);

        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        var victim = Victim(c, leader);
        var victimLed = c.Host(victim)!.Role == Role.Leader;
        var victimTerm = c.Host(victim)!.Term;
        var appliedBefore = c.Events.Lines.Select(Parse).Where(e => e.Node == victim.ToString() && e.Event == "apply").Select(e => Index(e.Fields)).DefaultIfEmpty(0).Max();
        var killedAt = RealClient.Micros(clock);
        var eventsAtKill = c.Events.Lines.Count;
        await c.KillAsync(victim);

        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        c.Start(victim);
        await load;
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        // The guards, before the verdict.
        var after = c.Events.Lines.Skip(eventsAtKill).Select(Parse).ToList();
        Assert.True(victimLed, $"the killed host {victim} was not the leader ({leader} was)");
        Assert.True(after.Any(e => e.Event == "leader" && e.Node != victim.ToString() && long.Parse(e.Fields["term"], CultureInfo.InvariantCulture) > victimTerm),
            $"no other host led a term after {victimTerm} once {victim} was killed");
        bool Write(HistoryEntry e) => e.Request.StartsWith("Session|", StringComparison.Ordinal) && e.Response is not null && e.Reply == "ok";
        Assert.True(entries.Any(e => Write(e) && e.Response < killedAt), "no write completed before the kill");
        Assert.True(entries.Any(e => Write(e) && e.Invoke > killedAt), "no write completed after the kill");
        Assert.True(entries.Any(e => e.Invoke < killedAt && (e.Response is null || e.Response > killedAt)), "no operation spanned the kill");
        Assert.True(after.Any(e => e.Node == victim.ToString() && e.Event == "apply" && Index(e.Fields) > appliedBefore), $"the restarted host applied nothing past {appliedBefore}");

        // Attempts per operation (P9-06's observable): a client's consecutive attempts of the same bytes.
        var attempts = 0;
        foreach (var byClient in entries.GroupBy(e => e.Client))
        {
            var run = 0;
            string? previous = null;
            foreach (var e in byClient.OrderBy(e => e.Invoke))
            {
                run = e.Request == previous ? run + 1 : 1;
                previous = e.Request;
                attempts = Math.Max(attempts, run);
            }
        }

        var history = ClientHistory.From(HostHistory.ToClientLog(entries));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "kill-history.txt"), string.Join("\n",
            $"killed {victim} (leader of term {victimTerm}) at {killedAt} us; {entries.Count} attempts; most attempts of one operation {attempts}",
            $"history: {history.History.Count} operations, {history.Completed} completed, {history.Indeterminate} indeterminate, {history.Refused} refused, {history.Retries} retries") + "\n");
        Assert.True(history.Unexplained.Count == 0, "unexplained replies: " + string.Join("; ", history.Unexplained.Take(5)));
        Assert.True(History.Problems(history.History).Count == 0, "history problems: " + string.Join("; ", History.Problems(history.History).Take(5)));
        var verdict = WglChecker.Check(history.History, 32_000_000);
        Assert.True(verdict.IsLinearizable, $"{verdict.Verdict} at key {verdict.Key}");
    }
}
