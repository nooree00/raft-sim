using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Kv;
using Raft.Simulation;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P4-09, spec §10: for every configured value, the largest legitimate thing is done and succeeds,
/// and the value one past it is refused where a refusal is the design. Each test reads its value
/// from the constant it exercises, so moving a limit without moving its test fails the one-past
/// half. Vacuity risk: a limit exercised well inside its bound does not notice the bound is in the
/// wrong place; guarded by using exactly the bound. Sabotages S-limit-1, S-limit-2.
/// </summary>
public sealed class LimitTests
{
    private static readonly Term T1 = new(1);

    private sealed class ZeroRandom : IRandomSource
    {
        public ulong NextUInt64() => 0;
    }

    private static RaftNode Node(NodeId id, RaftOptions options, byte[]? log = null)
    {
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        if (log is not null)
        {
            files[EntryLog.FileName] = log;
        }

        return new RaftNode(new NodeContext(id, All.Where(p => p != id).ToList(), new ZeroRandom(), files), options, new KvStateMachine());
    }

    /// <summary>Ticks n1 to its election and grants n2's vote: leader of term 1.</summary>
    private static void Lead(RaftNode n)
    {
        for (var i = 0; i < 1_000 && n.Role != Role.Leader; i++)
        {
            if (n.Handle(new Tick(1)).OfType<Send>().Any(s => MessageCodec.Decode(s.Payload.ToArray()) is RequestVote))
            {
                n.Handle(new Receive(N2, MessageCodec.Encode(new RequestVoteResponse(T1, true))));
            }
        }

        Assert.Equal(Role.Leader, n.Role);
    }

    private static byte[] Command(int length)
    {
        var prefix = Encoding.ASCII.GetBytes("Put|k|");
        var command = new byte[length];
        prefix.CopyTo(command, 0);
        command.AsSpan(prefix.Length).Fill((byte)'v');
        return command;
    }

    /// <summary>
    /// The largest batch of the largest commands: a leader holding one more than a batch of
    /// maximum-size commands sends exactly <see cref="RaftOptions.MaxEntriesPerAppend"/> of them in
    /// one AppendEntries, which encodes and decodes whole, and a follower with an empty log takes all
    /// of them, persists them as records, and recovers them from its file.
    /// </summary>
    [Fact]
    public void TheLargestBatchOfTheLargestCommandsReplicatesInOneAppendEntries()
    {
        var options = RaftOptions.Default;
        var entries = Enumerable.Range(0, options.MaxEntriesPerAppend + 1).Select(_ => new LogEntry(T1, Command(options.MaxCommandBytes))).ToList();
        var log = new LogStore(EntryLog.Recover(null)).Append(entries).Data.ToArray();
        var leader = Node(N1, options, log);
        Lead(leader);

        // The first AppendEntries to n2 is a heartbeat from the end of the log; its refusal moves
        // nextIndex back, and the retry carries a full batch from the start.
        var first = leader.Handle(new Tick(options.HeartbeatInterval)).OfType<Send>().First(s => s.To == N2);
        var probe = (AppendEntries)MessageCodec.Decode(first.Payload.ToArray())!;
        var retry = leader.Handle(new Receive(N2, MessageCodec.Encode(new AppendEntriesResponse(T1, false, 0))))
            .OfType<Send>().Select(s => (s.To, M: MessageCodec.Decode(s.Payload.ToArray()))).Where(s => s.To == N2).Select(s => s.M).OfType<AppendEntries>().Last();
        Assert.True(retry.PrevLogIndex == 0 && retry.Entries.Count == options.MaxEntriesPerAppend,
            $"the batch from the start held {retry.Entries.Count} entries after {retry.PrevLogIndex}, not {options.MaxEntriesPerAppend} after 0 (the probe was after {probe.PrevLogIndex})");
        Assert.All(retry.Entries, e => Assert.Equal(options.MaxCommandBytes, e.Command.Length));

        var follower = Node(N2, options);
        var effects = follower.Handle(new Receive(N1, MessageCodec.Encode(retry)));
        var reply = effects.OfType<Send>().Select(s => MessageCodec.Decode(s.Payload.ToArray())).OfType<AppendEntriesResponse>().Single();
        Assert.True(reply.Success && reply.MatchIndex == options.MaxEntriesPerAppend, $"the follower answered {reply}");
        var file = effects.OfType<PersistAppend>().Where(p => p.File == EntryLog.FileName).SelectMany(p => p.Data.ToArray()).ToArray();
        var recovered = EntryLog.Recover(file);
        Assert.Equal(RecoveryPath.Clean, recovered.Path);
        Assert.Equal(options.MaxEntriesPerAppend, recovered.Entries.Count);
    }

    /// <summary>A command of exactly <see cref="RaftOptions.MaxCommandBytes"/> is appended; one byte more is refused, and nothing is written.</summary>
    [Fact]
    public void TheLargestCommandIsAppendedAndOneByteMoreIsRefused()
    {
        var options = RaftOptions.Default;
        var leader = Node(N1, options);
        Lead(leader);

        var largest = leader.Handle(new ClientRequest(1, Command(options.MaxCommandBytes)));
        Assert.True(largest.OfType<PersistAppend>().Any(p => p.File == EntryLog.FileName), "a command of exactly the largest size was not appended");

        var tooLarge = leader.Handle(new ClientRequest(2, Command(options.MaxCommandBytes + 1)));
        Assert.False(tooLarge.OfType<PersistAppend>().Any(p => p.File == EntryLog.FileName), "a command one byte over the largest size was appended");
        var refusal = Encoding.ASCII.GetString(tooLarge.OfType<ClientResponse>().Single(r => r.RequestId == 2).Payload.Span);
        Assert.Equal("too-large|" + options.MaxCommandBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), refusal);
    }

    public static TheoryData<string> OptionLimits() => new("heartbeat", "spread", "command", "batch");

    /// <summary>Each option at its bound is accepted by a node, and one past it refused at construction.</summary>
    [Theory]
    [MemberData(nameof(OptionLimits))]
    public void EachOptionAtItsBoundIsAcceptedAndOnePastItRefused(string limit)
    {
        var d = RaftOptions.Default;
        var (atBound, pastBound) = limit switch
        {
            "heartbeat" => (d with { HeartbeatInterval = d.ElectionTimeoutMin / RaftOptions.HeartbeatsPerTimeout }, d with { HeartbeatInterval = (d.ElectionTimeoutMin / RaftOptions.HeartbeatsPerTimeout) + 1 }),
            "spread" => (d with { ElectionTimeoutMax = d.ElectionTimeoutMin + d.HeartbeatInterval }, d with { ElectionTimeoutMax = d.ElectionTimeoutMin + d.HeartbeatInterval - 1 }),
            "command" => (d with { MaxCommandBytes = RaftOptions.LargestCommandFor(d.MaxEntriesPerAppend) }, d with { MaxCommandBytes = RaftOptions.LargestCommandFor(d.MaxEntriesPerAppend) + 1 }),
            _ => (d with { MaxEntriesPerAppend = 1 }, d with { MaxEntriesPerAppend = 0 }),
        };

        var accepted = Node(N1, atBound);
        Assert.Equal(Role.Follower, accepted.Role);
        var refused = Assert.Throws<ArgumentException>(() => Node(N1, pastBound));
        Assert.Contains("options", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Timing at its bounds: the heartbeat at a third of the minimum timeout and the narrowest spread
    /// allowed (one heartbeat), with a heartbeat to each follower lost mid-run. One lost heartbeat must
    /// not start an election: every run elects once and keeps that leader.
    /// </summary>
    [Fact]
    public void AtTheTimingBoundsALostHeartbeatStartsNoElection()
    {
        var d = RaftOptions.Default;
        var heartbeat = d.ElectionTimeoutMin / RaftOptions.HeartbeatsPerTimeout;
        var options = d with { HeartbeatInterval = heartbeat, ElectionTimeoutMax = d.ElectionTimeoutMin + heartbeat };
        var extra = new List<string>();
        for (var seed = 1UL; seed <= 100; seed++)
        {
            Fault[] drops = [.. All.SelectMany(from => All.Where(to => to != from).Select(to => (Fault)new Drop(3_000, from, to)))];
            var sim = new Simulator(new SimulationConfig { Duration = 6_000, Nodes = 3 }, ctx => new RaftNode(ctx, options, new KvStateMachine()), seed, new FaultSchedule(drops)) { Observe = true };
            sim.Run();
            var elections = new ElectionHistory(sim.Observations, 3).Elections().Count;
            if (elections != 1)
            {
                extra.Add($"seed {seed}: {elections} elections");
            }
        }

        Assert.True(extra.Count == 0, "at the timing bounds, a lost heartbeat changed the leader: " + string.Join("; ", extra.Take(5)));
    }

    /// <summary>
    /// K, invariant 11's window, is inclusive: with the suffix starting at 0, a leader that first acts at
    /// time t passes a window of exactly t and fails a window of t − 1. The same comparison bounds
    /// the commit clause.
    /// </summary>
    [Fact]
    public void ALeaderActingExactlyAWindowAfterTheSuffixPassesAndOneUnitLaterFails()
    {
        var c = new ManualCluster(RaftOptions.Default);
        c.Stand(N1);
        c.Settle(All);
        var (h, _) = c.Histories();
        var acted = h.Sends.Where(s => s.Message is AppendEntries).Min(s => s.Time);

        var exactly = ElectionInvariants.Liveness(h, 0, c.Now, acted, 0);
        Assert.True(exactly.Count("checked") == 1 && exactly.Holds, $"a leader acting exactly the window ({acted}) after the suffix began was rejected");
        Assert.False(ElectionInvariants.Liveness(h, 0, c.Now, acted - 1, 0).Holds, "a leader acting one unit past the window was accepted");
        Assert.Equal(10 * RaftOptions.Default.ElectionTimeoutMax, Cluster.Window);
    }
}
