using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Raft.Core;

namespace Raft.Host;

/// <summary>A write as the load generator saw it (P12-03): its number, when it was due, dispatched (P12-04: when its time came on the generator's schedule), sent and answered, and the latency the generator measured.</summary>
public sealed record WriteStamp(int N, long Due, long Dispatched, long Sent, long Answered, double LatencyMicros);

/// <summary>
/// P12-03, phase 12 decision 2: where a commit's latency goes. After a run, the hosts' hand-off
/// stamps (<see cref="HandOffTrace"/>) and the generator's are joined write by write: a write's
/// command (`Put|k..|v{n}`) names it in the leader's client read and in the leader's entry log, which
/// gives its index; its index names it in every append, answer and persist after that. Each write's
/// latency is then fifteen consecutive stamps, so its fourteen segments sum to it by construction.
/// That identity checks nothing about the join, so the guards are others: the share of writes
/// joined; content (every stamp joined by index is of an entry carrying the write's own command, so
/// a write joined to another's index is counted and left out, S-lat-2); causality (no segment from
/// the dispatch on negative: the generator's own lateness is signed, since the generator P12-03
/// measured sent early when less than a millisecond remained); and the stamps' span against the
/// generator's own latency for the same writes. The generator's part is two segments (P12-04): its
/// own lateness, and the wait for a free connection after it, which is the system's backlog. With two followers, the one whose answer the leader took first is the one that committed
/// the write.
/// </summary>
public static class Decomposition
{
    /// <summary>The segments, in order along a write's path.</summary>
    public static readonly string[] Segments =
    [
        "generator-late",
        "connection-wait",
        "client-to-leader",
        "leader-queue",
        "leader-persist",
        "leader-to-send",
        "network-out",
        "follower-queue",
        "follower-persist",
        "follower-to-send",
        "network-back",
        "leader-queue-answer",
        "commit-respond",
        "response-to-client",
    ];

    /// <summary>
    /// The outcome: how many writes there were; how many were joined; how many were left out because a
    /// segment after the send was negative (Violations); the gap between the stamps' span and the
    /// generator's latency; and each segment's microseconds over the joined writes.
    /// </summary>
    public sealed record Result(int Writes, int Joined, int Violations, double SumGap, IReadOnlyDictionary<string, IReadOnlyList<double>> Micros)
    {
        /// <summary>For each segment, how many writes it was negative in: a stamp out of order, or, for the generator's, an early send.</summary>
        public IReadOnlyDictionary<string, int> NegativeBy { get; init; } = new Dictionary<string, int>();

        /// <summary>Writes left out because their index led to a stamp of an entry carrying another command: a wrong join.</summary>
        public int Mismatched { get; init; }

        /// <summary>
        /// P15-03: for each joined write, in <see cref="Micros"/>' order, leader-to-send and commit-respond each split
        /// at the output's emission (<see cref="SplitNames"/>): the time before it (for leader-to-send counted from
        /// the entry's durability, so the replication pipeline's wait; for commit-respond the answer's handling) and
        /// the hold after it, with the hold's class (0, 1, 2 for A, B, C; -1 when the trace has no emission).
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<double>> Splits { get; init; } = new Dictionary<string, IReadOnlyList<double>>();

        /// <summary>P16-01: the joined writes whose line's kernel arrival lies within client-to-leader, and those whose arrival lies outside it (a clock gone wrong; their split left whole to the reader).</summary>
        public (int Within, int Outside) Arrivals { get; init; }

        /// <summary>P16-01: the joined writes whose append's release lies within its hold, and those whose release lies outside it (their split left whole to the loop's hold).</summary>
        public (int Within, int Outside) Releases { get; init; }
    }

    /// <summary>The split parts (P15-03), each a list per joined write.</summary>
    public static readonly string[] SplitNames =
    [
        "leader-to-send.before", "leader-to-send.hold", "leader-to-send.class",
        "commit-respond.before", "commit-respond.hold", "commit-respond.class",
        "client-to-leader.delivery", "client-to-leader.reader", "leader-to-send.loop-hold", "leader-to-send.sender",
    ];

    /// <summary>
    /// P16-01: the parts of a write's path, the segments with client-to-leader split at its line's kernel arrival
    /// (delivery, then the reader) and leader-to-send at its append's emission and release (before the emission,
    /// the loop's hold until the release, the sender after it; P15-03's hold is the last two). A write with no
    /// arrival or release in the trace has the whole segment in its reader or its loop's hold.
    /// </summary>
    public static readonly string[] Parts =
    [
        .. Segments.SelectMany<string, string>(s => s switch
        {
            "client-to-leader" => ["client-to-leader.delivery", "client-to-leader.reader"],
            "leader-to-send" => ["leader-to-send.before", "leader-to-send.loop-hold", "leader-to-send.sender"],
            _ => [s],
        }),
    ];

    /// <summary>Each part's microseconds per joined write (<see cref="Parts"/>), from a result's segments and splits.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<double>> PartMicros(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Parts.ToDictionary(p => p, p => result.Micros.TryGetValue(p, out var m) ? m : result.Splits[p], StringComparer.Ordinal);
    }

    /// <summary>Whether a stamp lies within the segment it splits (P16-01: an arrival between the generator's send and the leader's read; a release between its append's hold's start and its send). Sabotage S-hand-3's guard.</summary>
    public static bool Within(long from, long at, long to) => at >= from && at <= to;

    /// <summary>The class an emission stamp's kind names: 0, 1, 2 for A, B, C (P15-03), else null.</summary>
    private static int? ClassOf(HandOffTrace.Kind kind) => kind switch
    {
        HandOffTrace.Kind.EmittedOwn => 0,
        HandOffTrace.Kind.EmittedEarlier => 1,
        HandOffTrace.Kind.EmittedNone => 2,
        _ => null,
    };

    /// <summary>A stamp joined by index: when, the array it read (for a peer message's take), and the write number its entry carries (null for an answer, which carries none).</summary>
    private readonly record struct AtIndex(long At, byte[]? Array, int? Number);

    public static Result Compute(IReadOnlyDictionary<NodeId, IReadOnlyList<HandOffTrace.Stamp>> traces, NodeId leader, IReadOnlyList<WriteStamp> writes)
    {
        ArgumentNullException.ThrowIfNull(traces);
        ArgumentNullException.ThrowIfNull(writes);
        var lead = traces[leader];
        var followers = traces.Keys.Where(k => k != leader).ToList();

        // The leader's side by request: its client reads (named by the write's number), takes and responses.
        var reads = new Dictionary<int, (long Id, long At)>();
        var taken = new Dictionary<long, long>();
        var responded = new Dictionary<long, long>();
        foreach (var s in lead)
        {
            switch (s.Kind)
            {
                case HandOffTrace.Kind.ClientRead when s.Payload is byte[] line && WriteNumber(line) is { } n:
                    reads.TryAdd(n, (s.Id, s.At));
                    break;
                case HandOffTrace.Kind.ClientTaken:
                    taken.TryAdd(s.Id, s.At);
                    break;
                case HandOffTrace.Kind.Responded:
                    responded.TryAdd(s.Id, s.At);
                    break;
            }
        }

        // Each node's persists by index, with the write each record's command carries; the leader's give
        // each write its index.
        var durable = traces.ToDictionary(t => t.Key, t => Durable(t.Value));
        var byNumber = new Dictionary<int, long>();
        foreach (var (index, d) in durable[leader].OrderBy(d => d.Key))
        {
            if (d.Number is { } n)
            {
                byNumber.TryAdd(n, index);
            }
        }

        // Appends, answers and their reads by index, per direction; and each node's takes of peer messages by array.
        var appendsSent = followers.ToDictionary(f => f, f => Cover(lead.Where(s => s.Kind == HandOffTrace.Kind.Sent && s.Id == f.Value).Select(s => (s, Message(Frame(s.Payload)))), appends: true));
        var appendsRead = followers.ToDictionary(f => f, f => Cover(traces[f].Where(s => s.Kind == HandOffTrace.Kind.PeerRead && s.Id == leader.Value).Select(s => (s, Message(s.Payload as byte[]))), appends: true));
        var answersSent = followers.ToDictionary(f => f, f => Cover(traces[f].Where(s => s.Kind == HandOffTrace.Kind.Sent && s.Id == leader.Value).Select(s => (s, Message(Frame(s.Payload)))), appends: false));
        var answersRead = followers.ToDictionary(f => f, f => Cover(lead.Where(s => s.Kind == HandOffTrace.Kind.PeerRead && s.Id == f.Value).Select(s => (s, Message(s.Payload as byte[]))), appends: false));
        var peerTaken = traces.ToDictionary(t => t.Key, t => PeerTaken(t.Value));

        // P15-03: the leader's emissions: appends by the index they first carry per follower, responses by request.
        var emittedAppends = followers.ToDictionary(f => f, f =>
        {
            var first = new Dictionary<long, (long At, int Class)>();
            foreach (var s in lead.Where(s => ClassOf(s.Kind) is not null && s.Id == f.Value && s.Payload is ReadOnlyMemory<byte>).OrderBy(s => s.At))
            {
                if (MessageCodec.Decode(((ReadOnlyMemory<byte>)s.Payload!).ToArray()) is AppendEntries ae)
                {
                    for (var i = 0; i < ae.Entries.Count; i++)
                    {
                        first.TryAdd(ae.PrevLogIndex + 1 + i, (s.At, ClassOf(s.Kind)!.Value));
                    }
                }
            }

            return first;
        });
        var emittedResponses = new Dictionary<long, (long At, int Class)>();
        foreach (var s in lead.Where(s => ClassOf(s.Kind) is not null && s.Payload is null))
        {
            emittedResponses.TryAdd(s.Id, (s.At, ClassOf(s.Kind)!.Value));
        }

        var splits = SplitNames.ToDictionary(n => n, _ => (IReadOnlyList<double>)new List<double>(), StringComparer.Ordinal);

        // P16-01: the kernel's arrival of each request's line; each append's release, by the frame its send wrote.
        var arrived = new Dictionary<long, long>();
        foreach (var s in lead.Where(s => s.Kind == HandOffTrace.Kind.ClientArrived))
        {
            arrived.TryAdd(s.Id, s.At);
        }

        var released = new Dictionary<byte[], long>(ReferenceEqualityComparer.Instance);
        foreach (var s in lead.Where(s => s.Kind == HandOffTrace.Kind.Released && s.Payload is byte[]))
        {
            released.TryAdd((byte[])s.Payload!, s.At);
        }

        int arrivalsWithin = 0, arrivalsOutside = 0, releasesWithin = 0, releasesOutside = 0;

        var micros = Segments.ToDictionary(s => s, _ => (IReadOnlyList<double>)new List<double>(), StringComparer.Ordinal);
        int joined = 0, violations = 0, mismatched = 0;
        var negative = Segments.ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
        double stampLatency = 0, generatorLatency = 0;
        foreach (var w in writes)
        {
            if (!reads.TryGetValue(w.N, out var read) || !taken.TryGetValue(read.Id, out var leaderTaken) || !responded.TryGetValue(read.Id, out var leaderResponded)
                || !byNumber.TryGetValue(w.N, out var index) || !durable[leader].TryGetValue(index, out var leaderDurable))
            {
                continue;
            }

            long[]? best = null;
            NodeId? bestFollower = null;
            var wrong = leaderDurable.Number != w.N;
            foreach (var f in followers)
            {
                if (appendsSent[f].TryGetValue(index, out var sent) && appendsRead[f].TryGetValue(index, out var fRead) && peerTaken[f].TryGetValue(fRead.Array!, out var fTaken)
                    && durable[f].TryGetValue(index, out var fDurable) && answersSent[f].TryGetValue(index, out var answered)
                    && answersRead[f].TryGetValue(index, out var lRead) && peerTaken[leader].TryGetValue(lRead.Array!, out var lTaken))
                {
                    wrong |= sent.Number != w.N || fRead.Number != w.N || fDurable.Number != w.N;
                    if (best is null || lTaken < best[12])
                    {
                        best = [w.Due, w.Dispatched, w.Sent, read.At, leaderTaken, leaderDurable.At, sent.At, fRead.At, fTaken, fDurable.At, answered.At, lRead.At, lTaken, leaderResponded, w.Answered];
                        bestFollower = f;
                    }
                }
            }

            if (best is null)
            {
                continue;
            }

            if (wrong)
            {
                mismatched++;
                continue;
            }

            var segments = new double[Segments.Length];
            var causal = true;
            for (var k = 0; k < segments.Length; k++)
            {
                segments[k] = Bench.Micros(best[k + 1] - best[k]);
                if (segments[k] < 0)
                {
                    // The generator's scheduling error is signed: a generator that dispatches early shows it
                    // here rather than as a violation. Causality is the system's, from the dispatch on.
                    causal &= k == 0;
                    negative[Segments[k]]++;
                }
            }

            if (!causal)
            {
                violations++;
                continue;
            }

            joined++;
            for (var k = 0; k < segments.Length; k++)
            {
                ((List<double>)micros[Segments[k]]).Add(segments[k]);
            }

            // P15-03: leader-to-send split at the append's emission (before it, only the time after the entry is
            // durable counts); commit-respond at the response's emission.
            void Split(string name, double before, double hold, int cls)
            {
                ((List<double>)splits[name + ".before"]).Add(before);
                ((List<double>)splits[name + ".hold"]).Add(hold);
                ((List<double>)splits[name + ".class"]).Add(cls);
            }

            var (durableAt, sentAt, takenAt, respondedAt) = (best[5], best[6], best[12], best[13]);
            var holdFrom = durableAt;
            if (emittedAppends[bestFollower!.Value].TryGetValue(index, out var ea))
            {
                Split("leader-to-send", Bench.Micros(Math.Max(0, ea.At - durableAt)), Bench.Micros(sentAt - Math.Max(ea.At, durableAt)), ea.Class);
                holdFrom = Math.Max(ea.At, durableAt);
            }
            else
            {
                Split("leader-to-send", 0, Bench.Micros(sentAt - durableAt), -1);
            }

            // P16-01: client-to-leader at the line's kernel arrival; the hold at the append's release.
            var (generatorSent, leaderRead) = (best[2], best[3]);
            var arrival = arrived.TryGetValue(read.Id, out var a) ? a : (long?)null;
            var arrivalInside = arrival is { } at && Within(generatorSent, at, leaderRead);
            arrivalsWithin += arrivalInside ? 1 : 0;
            arrivalsOutside += arrival is not null && !arrivalInside ? 1 : 0;
            ((List<double>)splits["client-to-leader.delivery"]).Add(arrivalInside ? Bench.Micros(arrival!.Value - generatorSent) : 0);
            ((List<double>)splits["client-to-leader.reader"]).Add(Bench.Micros(leaderRead - (arrivalInside ? arrival!.Value : generatorSent)));
            var release = released.TryGetValue(appendsSent[bestFollower.Value][index].Array!, out var r) ? r : (long?)null;
            var releaseInside = release is { } rel && Within(holdFrom, rel, sentAt);
            releasesWithin += releaseInside ? 1 : 0;
            releasesOutside += release is not null && !releaseInside ? 1 : 0;
            ((List<double>)splits["leader-to-send.loop-hold"]).Add(Bench.Micros((releaseInside ? release!.Value : sentAt) - holdFrom));
            ((List<double>)splits["leader-to-send.sender"]).Add(releaseInside ? Bench.Micros(sentAt - release!.Value) : 0);

            if (emittedResponses.TryGetValue(read.Id, out var er))
            {
                Split("commit-respond", Bench.Micros(er.At - takenAt), Bench.Micros(respondedAt - er.At), er.Class);
            }
            else
            {
                Split("commit-respond", 0, Bench.Micros(respondedAt - takenAt), -1);
            }

            stampLatency += Bench.Micros(w.Answered - w.Due);
            generatorLatency += w.LatencyMicros;
        }

        var gap = joined == 0 || generatorLatency == 0 ? 1 : Math.Abs(stampLatency - generatorLatency) / generatorLatency;
        return new Result(writes.Count, joined, violations, gap, micros)
        {
            NegativeBy = negative, Mismatched = mismatched, Splits = splits,
            Arrivals = (arrivalsWithin, arrivalsOutside), Releases = (releasesWithin, releasesOutside),
        };
    }

    /// <summary>The segments P14-04's rule reads as the persists' (the disk's own time).</summary>
    public static readonly string[] PersistSegments = ["leader-persist", "follower-persist"];

    /// <summary>The segments P14-04's rule reads as the loop's queues.</summary>
    public static readonly string[] QueueSegments = ["leader-queue", "leader-queue-answer", "commit-respond"];

    /// <summary>
    /// P14-04's rule for a missed tail, stated before the measurement: the writes whose latency (the sum of
    /// their segments) is over <paramref name="over"/> (the run's own 99th percentile when null), and their
    /// excess over the median write, summed segment by segment, as shares of the whole excess: by group
    /// (persist, queue, other) and by segment. A segment below its median gives a negative excess, kept.
    /// </summary>
    public static (double Over, int Writes, IReadOnlyDictionary<string, double> Groups, IReadOnlyDictionary<string, double> Segments) TailShares(IReadOnlyDictionary<string, IReadOnlyList<double>> micros, double? over) =>
        TailShares(micros, Segments, over);

    /// <summary>The same over other names that sum to a write's latency: P16-01's <see cref="Parts"/>, whose persists and queues are the segments'.</summary>
    public static (double Over, int Writes, IReadOnlyDictionary<string, double> Groups, IReadOnlyDictionary<string, double> Segments) TailShares(IReadOnlyDictionary<string, IReadOnlyList<double>> micros, IReadOnlyList<string> names, double? over)
    {
        ArgumentNullException.ThrowIfNull(micros);
        ArgumentNullException.ThrowIfNull(names);
        var count = micros[names[0]].Count;
        var totals = Enumerable.Range(0, count).Select(i => names.Sum(s => micros[s][i])).ToList();
        var threshold = over ?? (count == 0 ? 0 : Stats(totals).P99);
        var medians = names.ToDictionary(s => s, s => micros[s].Count == 0 ? 0 : Stats(micros[s]).P50, StringComparer.Ordinal);
        var excess = names.ToDictionary(s => s, _ => 0.0, StringComparer.Ordinal);
        var writes = 0;
        for (var i = 0; i < count; i++)
        {
            if (totals[i] <= threshold)
            {
                continue;
            }

            writes++;
            foreach (var s in names)
            {
                excess[s] += micros[s][i] - medians[s];
            }
        }

        var all = excess.Values.Sum();
        double Share(IEnumerable<string> group) => all <= 0 ? 0 : group.Sum(n => excess[n]) / all;
        var groups = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["persist"] = Share(PersistSegments),
            ["queue"] = Share(QueueSegments),
            ["other"] = Share(names.Except(PersistSegments).Except(QueueSegments)),
        };
        return (threshold, writes, groups, names.ToDictionary(s => s, s => Share([s]), StringComparer.Ordinal));
    }

    /// <summary>The number in a write's command, `Put|k..|v{n}`, or null for anything else; read in place (a sampled trace reads it per response, P15-01).</summary>
    internal static int? WriteNumber(ReadOnlySpan<byte> command)
    {
        var text = command.TrimEnd("\n\r"u8);
        var at = text.LastIndexOf("|v"u8);
        return text.StartsWith("Put|"u8) && at > 0 && int.TryParse(text[(at + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>A node's first durable time per index and the write its record carries, read from the entry-log records each persist appended (EntryLog's framing: length, index, term, previous term, command, checksum).</summary>
    private static Dictionary<long, AtIndex> Durable(IReadOnlyList<HandOffTrace.Stamp> stamps)
    {
        var first = new Dictionary<long, AtIndex>();
        foreach (var s in stamps.Where(s => s.Kind == HandOffTrace.Kind.Durable && s.Payload is ReadOnlyMemory<byte>))
        {
            var data = ((ReadOnlyMemory<byte>)s.Payload!).Span;
            for (var off = 0; off + 4 <= data.Length;)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(data[off..]);
                if (length < 24 || off + 4 + length + 4 > data.Length)
                {
                    break;
                }

                var payload = data.Slice(off + 4, length);
                var index = BinaryPrimitives.ReadInt64BigEndian(payload);
                if (index > 0)
                {
                    first.TryAdd(index, new AtIndex(s.At, null, WriteNumber(payload[24..])));
                }

                off += 4 + length + 4;
            }
        }

        return first;
    }

    /// <summary>The message inside a frame (length, message, checksum).</summary>
    private static byte[]? Frame(object? payload) => payload is byte[] { Length: >= 8 } f ? f[4..^4] : null;

    private static Message? Message(byte[]? bytes) => bytes is null ? null : MessageCodec.Decode(bytes);

    /// <summary>
    /// The first stamp, per index, of a message that covers it: an append carrying entries covers
    /// their indices (and names each entry's write); a successful answer covers every index up to its
    /// match index.
    /// </summary>
    private static Dictionary<long, AtIndex> Cover(IEnumerable<(HandOffTrace.Stamp Stamp, Message? Message)> messages, bool appends)
    {
        var first = new Dictionary<long, AtIndex>();
        var high = -1L;
        foreach (var (s, m) in messages.OrderBy(x => x.Stamp.At))
        {
            var array = s.Payload as byte[];
            if (appends && m is AppendEntries ae)
            {
                for (var i = 0; i < ae.Entries.Count; i++)
                {
                    first.TryAdd(ae.PrevLogIndex + 1 + i, new AtIndex(s.At, array, WriteNumber(ae.Entries[i].Command)));
                }
            }
            else if (!appends && m is AppendEntriesResponse { Success: true } r && r.MatchIndex > high)
            {
                for (var i = Math.Max(high + 1, 1); i <= r.MatchIndex; i++)
                {
                    first.TryAdd(i, new AtIndex(s.At, array, null));
                }

                high = r.MatchIndex;
            }
        }

        return first;
    }

    /// <summary>When the loop took each peer message, by the array its read produced.</summary>
    private static Dictionary<byte[], long> PeerTaken(IReadOnlyList<HandOffTrace.Stamp> stamps)
    {
        var taken = new Dictionary<byte[], long>(ReferenceEqualityComparer.Instance);
        foreach (var s in stamps.Where(s => s.Kind == HandOffTrace.Kind.PeerTaken && s.Payload is byte[]))
        {
            taken.TryAdd((byte[])s.Payload!, s.At);
        }

        return taken;
    }

    /// <summary>A segment's mean, median and 99th percentile, rounded to a tenth of a microsecond.</summary>
    public static (double Mean, double P50, double P99) Stats(IReadOnlyList<double> micros)
    {
        ArgumentNullException.ThrowIfNull(micros);
        if (micros.Count == 0)
        {
            return (0, 0, 0);
        }

        var sorted = micros.Order().ToList();
        return (Math.Round(micros.Average(), 1), Math.Round(Measurement.Percentile(sorted, 50), 1), Math.Round(Measurement.Percentile(sorted, 99), 1));
    }

    /// <summary>The array behind a payload, for joining a take to its read.</summary>
    internal static byte[]? ArrayOf(ReadOnlyMemory<byte> payload) => MemoryMarshal.TryGetArray(payload, out var segment) ? segment.Array : null;
}
