using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raft.Core;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P13-02: a load run's record carries every host's role changes, each election with the electing
/// follower's silence from the leader, so a leader change in an unfaulted run is explained (a
/// silence past the election timeout) or counted. Vacuity risk: a record that carries the field and
/// never a change says nothing; guarded by a control that stalls the leader past its timeout and
/// needs the change and its silence recorded. Sabotage S-lead-1 (no change recorded).
/// </summary>
public sealed class RoleChangeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>The control: the leader's loop stops for a second, its followers elect, and the election is recorded with the silence that explains it. Sabotage S-lead-1.</summary>
    [Fact]
    public async Task ALeaderStalledPastItsTimeoutIsRecordedAsAnElectionItsSilenceExplains()
    {
        await using var c = new HostCluster(answerRoles: true);
        c.StartAll();
        _ = await c.LeaderAsync(Wait, Ct) ?? throw new InvalidOperationException("no leader");
        await Task.Delay(500, Ct);

        // The node that leads now: on a slow machine the first election can be followed by another
        // before the stall (run 38482181300, at 4ca269b), and stalling a node that no longer leads
        // plants nothing. The new leader is the one that takes office after the stall.
        var leader = await c.LeaderAsync(Wait, Ct) ?? throw new InvalidOperationException("no leader");
        var stalled = Stopwatch.GetTimestamp();
        c.Host(leader)!.StallOnce(TimeSpan.FromSeconds(1));

        NodeId? next = null;
        for (var end = DateTime.UtcNow + Wait; next is null && DateTime.UtcNow < end;)
        {
            await Task.Delay(50, Ct);
            foreach (var n in c.Nodes.Where(n => n != leader && c.Host(n)!.RoleChanges.Any(x => x.At >= stalled && x.To == Role.Leader)))
            {
                next = n;
            }
        }

        Assert.True(next is not null, "no follower took office after the leader was stalled");
        var elections = c.Nodes.Where(n => n != leader).SelectMany(n => c.Host(n)!.RoleChanges).Where(x => x.At >= stalled && x.To == Role.Candidate && x.From == Role.Follower).ToList();
        Assert.NotEmpty(elections);
        var first = elections.OrderBy(x => x.At).First();
        Assert.True(first.Explained(RaftOptions.Default), $"the first election's silence from the leader was {first.SilenceMs:F1} ms, more than a tick under the {RaftOptions.Default.ElectionTimeoutMin}-ms timeout");
        Assert.Contains(c.Host(next.Value)!.RoleChanges, x => x.At >= stalled && x.To == Role.Leader);

        // The same changes through `Roles|`, as a Compose load run reads them.
        var reply = await c.RequestAsync(next.Value, "Roles|", TimeSpan.FromSeconds(5), Ct);
        Assert.StartsWith("ok|", reply, StringComparison.Ordinal);
        var decoded = NodeHost.DecodeRoles(reply![3..], Stopwatch.GetTimestamp());
        Assert.Equal(c.Host(next.Value)!.RoleChanges.Select(x => (x.From, x.To, x.Term)), decoded.Select(x => (x.From, x.To, x.Term)));
    }

    [Fact]
    public void AnElectionWithARecentLeaderFrameIsUnexplained()
    {
        Assert.False(new RoleChange(0, Role.Follower, Role.Candidate, 3, SilenceMs: 40, TickMs: 160).Explained(RaftOptions.Default));
        Assert.True(new RoleChange(0, Role.Follower, Role.Candidate, 3, SilenceMs: 151, TickMs: 10).Explained(RaftOptions.Default));
        Assert.True(new RoleChange(0, Role.Follower, Role.Candidate, 3, SilenceMs: 142, TickMs: 10).Explained(RaftOptions.Default));
        Assert.False(new RoleChange(0, Role.Follower, Role.Candidate, 3, SilenceMs: 138, TickMs: 10).Explained(RaftOptions.Default));
        Assert.True(new RoleChange(0, Role.Leader, Role.Follower, 4, SilenceMs: 0, TickMs: 0).Explained(RaftOptions.Default));
    }

    /// <summary>The host tells a leader's frame by the codec's type byte; held to the codec, so a renumbering is caught here.</summary>
    [Fact]
    public void OnlyTheFramesALeaderSendsAreTakenAsTheLeaders()
    {
        Assert.True(NodeHost.FromLeader(MessageCodec.Encode(new AppendEntries(new Term(2), new NodeId(1), 0, new Term(0), [], 0, 1))));
        Assert.True(NodeHost.FromLeader(MessageCodec.Encode(new InstallSnapshot(new Term(2), new NodeId(1), 5, new Term(1), 0, [], true))));
        Assert.False(NodeHost.FromLeader(MessageCodec.Encode(new RequestVote(new Term(2), new NodeId(2), 0, new Term(0)))));
        Assert.False(NodeHost.FromLeader(MessageCodec.Encode(new AppendEntriesResponse(new Term(2), true, 0, 1))));
        Assert.False(NodeHost.FromLeader([]));
    }

    [Fact]
    public void RolesSurviveTheirEncoding()
    {
        var now = Stopwatch.GetTimestamp();
        var changes = new[] { new RoleChange(now - Stopwatch.Frequency, Role.Follower, Role.Candidate, 7, 312.5, 10), new RoleChange(now, Role.Candidate, Role.Leader, 7, 0, 0) };

        var back = NodeHost.DecodeRoles(NodeHost.EncodeRoles(changes, now), now);

        Assert.Equal(changes.Select(c => (c.From, c.To, c.Term, c.SilenceMs, c.TickMs)), back.Select(c => (c.From, c.To, c.Term, c.SilenceMs, c.TickMs)));
        Assert.InRange(Math.Abs(back[0].At - changes[0].At), 0, Stopwatch.Frequency / 1000);
    }
}
