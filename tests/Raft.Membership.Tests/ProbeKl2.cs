using System;
using System.Globalization;
using System.Linq;
using Raft.Checker;
using Raft.Core.Tests;
using Raft.Scale.Tests;
using Xunit;

namespace Raft.Membership.Tests;

/// <summary>P7-01 probe, never merged: KL-2's search on the runner at one budget, after asserting the history is KL-2's.</summary>
public sealed class ProbeKl2
{
    [Fact]
    public void Measure()
    {
        var budget = long.Parse(Environment.GetEnvironmentVariable("PROBE_BUDGET")!, CultureInfo.InvariantCulture);
        var expected = Environment.GetEnvironmentVariable("PROBE_DIGEST") ?? "081204a0f61123069be79e2b92c98900a03d92f0b8183d60404b81cc5d04087f";
        var (sim, _, _) = MembershipSimulationTests.Run(8741);
        var history = ClientHistory.From(sim.ClientLog).History;
        var digest = KnownLimits.Digest(history.Where(o => o.Key == "k5"));
        Assert.True(digest == expected, $"not KL-2's history: digest {digest}, expected {expected}");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var lin = WglChecker.Check(history, budget);
        watch.Stop();
        var peak = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1_000_000;
        System.IO.File.AppendAllText(Environment.GetEnvironmentVariable("PROBE_OUT") ?? "/tmp/probe.txt", FormattableString.Invariant($"PROBE budget {budget}: {lin.Verdict} key {lin.Key} states {lin.StatesExplored} peak {peak} MB {watch.Elapsed.TotalSeconds:F0} s\n"));
        Assert.Equal(Verdict.Undecided, lin.Verdict);
    }
}
