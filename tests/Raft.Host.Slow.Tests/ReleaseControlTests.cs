using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Raft.Core;
using Raft.Host;
using Raft.Host.Tests;
using Xunit;

namespace Raft.Host.Slow.Tests;

/// <summary>
/// P16-01: the release's control, on the in-process cluster, traced, as the hand-off control is run
/// (<see cref="BenchControlTests.APlantedHandOffDelayAppearsInItsSegmentOnly"/>): windows alternating off
/// and on, a delay of 2 ms planted on the leader's senders between a frame's release and its send (each
/// sender waiting it off the loop's thread before it takes each frame, P16-11). The sender's part must rise by
/// the delay actually waited, at its 5th percentile, and no other part by more than 0.4 of it, the hand-off
/// control's bounds. The same runs read every joined write's arrival and release within its segment: the
/// kernel's timestamps on the leader's client sockets and the release stamp, through the host. Vacuity
/// risk: a release stamped by the sender, not at the hand-off to its queue, leaves the sender's part empty
/// and the delay in the loop's hold (S-hand-2). In its own class, so the entries on the bench controls'
/// class do not run it, and in their collection, so it runs alone.
/// </summary>
[Collection(nameof(BenchControlTests))]
public sealed class ReleaseControlTests
{
    private const double PlantedMicros = 2_000;
    private const int Windows = 20;
    private const double Rate = 300;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The planted sender delay in the sender's part only, and every write's arrival and release within its segment. Sabotage S-hand-2.</summary>
    [Fact]
    public async Task APlantedSenderDelayAppearsInTheSendersPartOnly()
    {
        await using var c = new HostCluster(RaftOptions.Default with { ElectionTimeoutMin = 1_000, ElectionTimeoutMax = 2_000 }, traced: true);
        c.StartAll();
        var leader = (await c.LeaderAsync(TimeSpan.FromSeconds(15), Ct))!.Value;
        var endpoints = c.Nodes.ToDictionary(n => n, n => new DnsEndPoint("127.0.0.1", c.ClientEndpoint(n).Port));
        var plain = Decomposition.Parts.ToDictionary(p => p, _ => new List<double>(), StringComparer.Ordinal);
        var delayed = Decomposition.Parts.ToDictionary(p => p, _ => new List<double>(), StringComparer.Ordinal);
        int writes = 0, joined = 0, left = 0, arrivals = 0, arrivalsOutside = 0, releases = 0, releasesOutside = 0;
        for (var round = 0; round < Windows; round++)
        {
            var on = round % 2 == 1;
            c.Host(leader)!.SenderDelay = on ? TimeSpan.FromMicroseconds(PlantedMicros) : TimeSpan.Zero;
            foreach (var n in c.Nodes)
            {
                c.Trace(n).Drain();
            }

            var stamps = new System.Collections.Concurrent.ConcurrentBag<WriteStamp>();
            await LoadGenerator.RunAsync(new LoadConfig(endpoints, Rate: Rate, Duration: TimeSpan.FromSeconds(1.25), Warmup: TimeSpan.FromSeconds(0.25), Trace: stamps.Add), Ct);
            var d = Decomposition.Compute(c.Nodes.ToDictionary(n => n, n => c.Trace(n).Drain()), leader, [.. stamps]);
            writes += d.Writes;
            joined += d.Joined;
            left += d.Mismatched + d.Violations;
            (arrivals, arrivalsOutside, releases, releasesOutside) = (arrivals + d.Arrivals.Within, arrivalsOutside + d.Arrivals.Outside, releases + d.Releases.Within, releasesOutside + d.Releases.Outside);
            foreach (var (part, micros) in Decomposition.PartMicros(d))
            {
                (on ? delayed : plain)[part].AddRange(micros);
            }
        }

        double Low(List<double> m) => m.Count == 0 ? 0 : Measurement.Percentile([.. m.Order()], 5);
        var rise = Decomposition.Parts.ToDictionary(p => p, p => Low(delayed[p]) - Low(plain[p]), StringComparer.Ordinal);
        var spun = c.Host(leader)!.SenderSpun;
        var waited = spun.Spins == 0 ? 0 : spun.Micros / spun.Spins;
        // P16-11: every delayed write's frame waits the planted delay in full, so none is short of it.
        var sender = delayed["leader-to-send.sender"];
        var smallest = sender.Count == 0 ? 0 : sender.Min();
        var belowDelay = sender.Count(m => m < 0.95 * PlantedMicros);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "bench-release.txt"), FormattableString.Invariant($"{joined} of {writes} writes joined, {left} left out; arrivals {arrivals} within, {arrivalsOutside} outside; releases {releases} within, {releasesOutside} outside; {PlantedMicros} us set, {waited:F0} us waited on average over {spun.Spins} spins; delayed windows' smallest sender part {smallest:F0} us, {belowDelay} of {sender.Count} below 95% of the delay; 5th percentile rise: ") + string.Join(", ", rise.Select(r => FormattableString.Invariant($"{r.Key} {r.Value:F0}"))) + "\n");
        Assert.True(joined >= 0.95 * writes && left == 0, $"{joined} of {writes} writes joined, {left} left out: the control did not measure the writes it ran");
        Assert.True(arrivals >= 0.95 * joined && arrivalsOutside == 0, $"{arrivals} of {joined} joined writes have their line's kernel arrival within client-to-leader, {arrivalsOutside} outside it");
        Assert.True(releases >= 0.95 * joined && releasesOutside == 0, $"{releases} of {joined} joined writes have their append's release within leader-to-send's hold, {releasesOutside} outside it");
        Assert.True(spun.Spins > 0, "the planted sender delay never ran");
        Assert.InRange(rise["leader-to-send.sender"] / waited, 0.5, 3.0);
        // Not the generator's own segment, as in the hand-off control: no host's delay can be attributed to it.
        Assert.All(rise.Where(r => r.Key is not "leader-to-send.sender" and not "generator-late"), r => Assert.True(r.Value < 0.4 * waited, FormattableString.Invariant($"{r.Key} rose {r.Value:F0} us at its 5th percentile with {waited:F0} us planted in leader-to-send.sender")));
    }
}
