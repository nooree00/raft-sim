using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Kv;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P6-03: a configuration is a log entry, durable and recovered. Its encoding is canonical, it is in
/// effect from the moment it is appended (P6 decision 1), and a node's configuration in effect is
/// the latest configuration entry in its log, else its initial one (decision 2). Vacuity risk: a node
/// that never truncates a configuration entry never shows whether "in effect" means the log or a
/// cached value; so a configuration entry in a conflicting suffix, truncated by a new leader, must
/// revert the node's configuration. Sabotages S-cfg-1, S-cfg-2.
/// </summary>
public sealed class ConfigurationTests
{
    private static readonly NodeId N1 = new(1), N2 = new(2), N3 = new(3), N4 = new(4);
    private static readonly Term T1 = new(1), T2 = new(2);
    private static readonly Configuration Initial = new([N1, N2, N3]);
    private static readonly Configuration Joint = new([N1, N2, N3], [N1, N2, N4]);
    private static readonly Configuration Replaced = new([N1, N2, N4]);

    private sealed class ZeroRandom : IRandomSource
    {
        public ulong NextUInt64() => 0;
    }

    private sealed class RecordingStateMachine : IStateMachine
    {
        public List<byte[]> Applied { get; } = [];

        public ReadOnlyMemory<byte> Apply(long index, ReadOnlyMemory<byte> command)
        {
            Applied.Add(command.ToArray());
            return ReadOnlyMemory<byte>.Empty;
        }

        public ReadOnlyMemory<byte> Snapshot() => ReadOnlyMemory<byte>.Empty;

        public void Restore(ReadOnlyMemory<byte> snapshot)
        {
        }
    }

    private static LogEntry E(Term t, string command) => new(t, Encoding.ASCII.GetBytes(command));

    private static LogEntry C(Term t, Configuration c) => new(t, c.Encode());

    private static RaftNode Node(NodeId id, byte[]? log = null, IStateMachine? sm = null)
    {
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        if (log is not null)
        {
            files[EntryLog.FileName] = log;
        }

        return new RaftNode(new NodeContext(id, new[] { N1, N2, N3 }.Where(p => p != id).ToList(), new ZeroRandom(), files), RaftOptions.Default, sm ?? new KvStateMachine());
    }

    private static IReadOnlyList<Effect> Receive(RaftNode n, NodeId from, Message m) => n.Handle(new Receive(from, MessageCodec.Encode(m)));

    private static byte[] LogFile(params LogEntry[] entries) => new LogStore(EntryLog.Recover(null)).Append(entries).Data.ToArray();

    [Fact]
    public void AConfigurationRoundTripsAndOnlyItsCanonicalBytesDecode()
    {
        foreach (var c in new[] { Initial, Joint, Replaced, new Configuration([N3, N1, N1, N2]) })
        {
            Assert.Equal(c, Configuration.Decode(c.Encode()));
        }

        Assert.Equal(Initial, new Configuration([N3, N1, N1, N2]));
        var bytes = Joint.Encode();
        Assert.Null(Configuration.Decode([.. bytes, 0]));
        Assert.Null(Configuration.Decode(bytes[..^1]));
        var unsorted = (byte[])Initial.Encode().Clone();
        for (var i = 0; i < 4; i++)
        {
            (unsorted[4 + i], unsorted[8 + i]) = (unsorted[8 + i], unsorted[4 + i]);
        }

        Assert.Null(Configuration.Decode(unsorted));
        Assert.Null(Configuration.Decode(Encoding.ASCII.GetBytes("Put|k|v")));
        Assert.Null(Configuration.Decode([]));
        Assert.False(Configuration.IsInternal(Encoding.ASCII.GetBytes("Put|k|v")));
        Assert.True(Configuration.IsInternal(Joint.Encode()));
    }

    [Fact]
    public void AJointQuorumNeedsAMajorityOfBoth()
    {
        Assert.True(Initial.IsQuorum([N1, N2]));
        Assert.False(Joint.IsQuorum([N3, N4]));
        Assert.False(Joint.IsQuorum([N2, N3]));
        Assert.True(Joint.IsQuorum([N1, N2]));
        Assert.True(Joint.IsQuorum([N2, N3, N4]));
        Assert.Equal([N1, N2, N3, N4], Joint.Members);
    }

    [Fact]
    public void TheConfigurationInEffectIsTheLatestInTheLogElseTheInitialOne()
    {
        Assert.Equal(Initial, Configuration.InEffect(Initial, []));
        Assert.Equal(Initial, Configuration.InEffect(Initial, [Encoding.ASCII.GetBytes("Put|k|v"), []]));
        Assert.Equal(Replaced, Configuration.InEffect(Initial, [Joint.Encode(), Encoding.ASCII.GetBytes("Put|k|v"), Replaced.Encode(), []]));
    }

    /// <summary>Decision 1: appended is enough. A follower's configuration changes with an entry nothing has committed.</summary>
    [Fact]
    public void AnAppendedConfigurationIsInEffectBeforeItCommits()
    {
        var n = Node(N2);
        Receive(n, N1, new AppendEntries(T1, N1, 0, Term.Zero, [E(T1, ""), C(T1, Joint)], 0));

        Assert.Equal(Joint, n.Configuration);
    }

    /// <summary>
    /// The case that decides between the log and a cached value: a configuration entry in a suffix a
    /// new leader truncates takes its effect with it.
    /// </summary>
    [Fact]
    public void ATruncatedConfigurationEntryRevertsTheConfiguration()
    {
        var n = Node(N2);
        Receive(n, N1, new AppendEntries(T1, N1, 0, Term.Zero, [E(T1, ""), C(T1, Joint)], 0));
        Assert.Equal(Joint, n.Configuration);

        var effects = Receive(n, N3, new AppendEntries(T2, N3, 1, T1, [E(T2, "")], 0));

        Assert.Contains(effects, e => e is PersistTruncate);
        Assert.Equal(Initial, n.Configuration);
    }

    /// <summary>
    /// Recovery from every crash point of the log file: the configuration is that of the latest
    /// configuration record wholly on disk, and a torn configuration record takes its configuration
    /// with it. The expectation is computed from the record boundaries, not from recovery.
    /// </summary>
    [Fact]
    public void EveryCrashPointOfTheLogRecoversTheLatestDurableConfiguration()
    {
        var entries = new[] { E(T1, ""), C(T1, Joint), E(T1, "Put|k|v"), C(T1, Replaced) };
        var file = LogFile(entries);
        var ends = new List<int>();
        var store = new LogStore(EntryLog.Recover(null));
        foreach (var e in entries)
        {
            store.Append([e]);
            ends.Add((int)store.At(store.LastIndex).EndOffset);
        }

        for (var cut = 0; cut <= file.Length; cut++)
        {
            var expected = cut >= ends[3] ? Replaced : cut >= ends[1] ? Joint : Initial;
            Assert.True(expected == Node(N2, file[..cut]).Configuration, $"cut at {cut} of {file.Length}: expected {expected}");
        }
    }

    [Fact]
    public void AClientCommandStartingWithTheInternalMarkIsRefusedAndConfigurationsNeverReachTheStateMachine()
    {
        var sm = new RecordingStateMachine();
        var n = Node(N2, LogFile(E(T1, ""), C(T1, Joint), E(T1, "Put|k|v")), sm);
        Receive(n, N1, new AppendEntries(T1, N1, 3, T1, [], 3));

        Assert.Equal(["Put|k|v"], sm.Applied.Select(b => Encoding.ASCII.GetString(b)));

        var leader = Node(N1);
        for (var i = 0; i < 400 && leader.Role != Role.Leader; i++)
        {
            if (leader.Handle(new Tick(1)).OfType<Send>().Any(m => MessageCodec.Decode(m.Payload.ToArray()) is RequestVote))
            {
                Receive(leader, N2, new RequestVoteResponse(T1, true));
            }
        }

        Assert.Equal(Role.Leader, leader.Role);
        var reply = leader.Handle(new ClientRequest(7, Joint.Encode())).OfType<ClientResponse>().Single();
        Assert.Equal("reserved|", Encoding.ASCII.GetString(reply.Payload.Span));
    }
}
