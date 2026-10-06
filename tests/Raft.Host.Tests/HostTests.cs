using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Raft.Core;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P9-04: three hosts in one test process, on loopback, with real clocks, real sockets and real
/// files. Spec §10: one test where nobody calls anything, for the behaviour the clock drives; a
/// leader is read from the hosts' own events. Vacuity risk: a test that drives a node by calling it
/// proves the node, not the host. Sabotage S-host-1 (the clock's ticks never delivered).
/// </summary>
public sealed class HostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Nobody calls anything: three hosts start, and one of them announces it leads. Sabotage S-host-1.</summary>
    [Fact]
    public async Task ThreeHostsElectALeaderWithNobodyCallingAnything()
    {
        await using var c = new HostCluster();
        c.StartAll();
        var leader = await c.LeaderAsync(Wait, Ct);

        Assert.True(leader is not null, "no host became leader within 10 s: " + string.Join(" / ", c.Events.Lines.TakeLast(5)));
        Assert.Contains(c.Events.Lines, l => JsonDocument.Parse(l).RootElement.GetProperty("Event").GetString() == "leader"
            && JsonDocument.Parse(l).RootElement.GetProperty("Node").GetString() == leader!.Value.ToString());
    }

    /// <summary>A client registers, writes in its session, reads by ReadIndex, and is redirected by a follower.</summary>
    [Fact]
    public async Task ClientWritesCommitAndAReadSeesThem()
    {
        await using var c = new HostCluster();
        c.StartAll();
        var leader = (await c.LeaderAsync(Wait, Ct))!.Value;
        var register = await c.RequestAsync(leader, "Register|", Wait, Ct);
        Assert.StartsWith("ok|", register, StringComparison.Ordinal);
        var session = register![3..];
        Assert.Equal("ok", await c.RequestAsync(leader, $"Session|{session}|1|Append|k|a", Wait, Ct));
        Assert.Equal("ok", await c.RequestAsync(leader, $"Session|{session}|2|Append|k|b", Wait, Ct));
        Assert.Equal("ok|ab", await c.RequestAsync(leader, "Get|k", Wait, Ct));

        var follower = c.Nodes.First(n => n != leader);
        Assert.Equal("redirect|" + leader, await c.RequestAsync(follower, "Get|k", Wait, Ct));
        Assert.StartsWith("ok|Leader|", await c.RequestAsync(leader, "Status|", Wait, Ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// The prediction's measurement: a steady run of three hosts with no faults, for
    /// RAFT_HOST_STEADY_SECONDS (5 by default), and the highest term any host reached. Written beside
    /// the assembly; asserted only that a leader exists at the end.
    /// </summary>
    [Fact]
    public async Task ASteadyRunKeepsALeader()
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("RAFT_HOST_STEADY_SECONDS"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 5;
        await using var c = new HostCluster();
        c.StartAll();
        Assert.NotNull(await c.LeaderAsync(Wait, Ct));
        await Task.Delay(TimeSpan.FromSeconds(seconds), Ct);
        var term = c.Nodes.Max(n => c.Host(n)!.Term);
        var elections = c.Events.Lines.Count(l => JsonDocument.Parse(l).RootElement.GetProperty("Event").GetString() == "leader");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "host-steady.txt"), $"{seconds} s steady, three hosts: highest term {term}, leader elections {elections}\n");
        Assert.NotNull(await c.LeaderAsync(Wait, Ct));
    }
}
