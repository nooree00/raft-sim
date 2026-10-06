using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raft.Checker;
using Raft.Core;
using Raft.Core.Tests;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P9-07, phase 9 decision 8: the positive control in real processes. A leader that answers a write
/// when it appends it (<see cref="RaftOptions.AnswerAtAppend"/>) is cut off from its peers (the
/// test's hold on its outbound frames), answers a write only it holds, and is killed; the next leader
/// never had the write, and a read of its key misses it. The checker must reject that history. The
/// real host, in the same steps, never answers the write, and its history is accepted. Vacuity
/// risk: a construction where the held write was never answered shows nothing; guarded by asserting
/// the answer, and the read that misses it, before the verdict. Sabotage S-ctl-2 (the control on by
/// default).
/// </summary>
public sealed class ControlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<(List<HistoryEntry> Entries, string? Held, string? Read)> RunAsync(RaftOptions options)
    {
        await using var c = new HostCluster(options);
        c.StartAll();
        var leader = (await c.LeaderAsync(Wait, Ct))!.Value;
        var clock = Stopwatch.StartNew();
        var entries = new List<HistoryEntry>();
        long id = 0;
        async Task<string?> RequestAsync(NodeId n, string request, TimeSpan timeout)
        {
            var invoke = RealClient.Micros(clock);
            var reply = await c.RequestAsync(n, request, timeout, Ct);
            entries.Add(new HistoryEntry(0, ++id, n.Value, request, invoke, reply is null ? null : RealClient.Micros(clock), reply ?? ""));
            return reply;
        }

        var session = (await RequestAsync(leader, "Register|", Wait))![3..];
        Assert.Equal("ok", await RequestAsync(leader, $"Session|{session}|1|Append|k|a", Wait));

        // A read by ReadIndex sees `a` only once it has committed: the control answered it at append.
        for (var i = 0; i < 50 && await RequestAsync(leader, "Get|k", Wait) != "ok|a"; i++)
        {
            await Task.Delay(20, Ct);
        }

        c.Host(leader)!.HoldOutbound = true;
        var held = await RequestAsync(leader, $"Session|{session}|2|Append|k|b", TimeSpan.FromSeconds(1));
        await c.KillAsync(leader);
        var next = (await c.LeaderAsync(Wait, Ct))!.Value;
        Assert.NotEqual(leader, next);
        var read = await RequestAsync(next, "Get|k", Wait);
        return (entries, held, read);
    }

    private static bool Linearizable(List<HistoryEntry> entries)
    {
        var history = ClientHistory.From(HostHistory.ToClientLog(entries));
        Assert.Empty(history.Unexplained);
        return WglChecker.Check(history.History, 32_000_000).IsLinearizable;
    }

    [Fact]
    public async Task ALeaderAnsweringAtAppendLosesAnAnsweredWriteAndTheCheckerRejectsTheHistory()
    {
        var (entries, held, read) = await RunAsync(RaftOptions.Default with { AnswerAtAppend = true });

        Assert.Equal("ok", held);
        Assert.Equal("ok|a", read);
        Assert.False(Linearizable(entries), "a write answered and then lost was accepted");
    }

    /// <summary>The real host in the same steps: the held write is never answered, and the history is accepted. Sabotage S-ctl-2.</summary>
    [Fact]
    public async Task TheRealHostNeverAnswersTheHeldWriteAndTheCheckerAcceptsTheHistory()
    {
        var (entries, held, read) = await RunAsync(RaftOptions.Default);

        Assert.True(held is null, $"the real host answered a write only it held: {held}");
        Assert.Equal("ok|a", read);
        Assert.True(Linearizable(entries), "the real host's history was rejected");
    }
}
