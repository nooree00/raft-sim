using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Raft.Host;
using Xunit;

namespace Raft.Host.Slow.Tests;

/// <summary>
/// P15-01: the in-process cluster run in a process of its own (`bench cluster`) and driven from this one
/// through its control port: the hosts' counters, role changes and traces reach this process, and the
/// record says the generator had a process of its own; the same cluster in this process says it shared
/// the hosts'. Sabotage S-proc-1 (a separate mode claimed without comparing the processes).
/// </summary>
public sealed class SeparateGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The host's build in this tree: the test runs from the tree that `ci-build.sh` built.</summary>
    private static string HostDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Raft.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("no Raft.slnx above " + AppContext.BaseDirectory), "src", "Raft.Host", "bin", "Debug", "net10.0", "Raft.Host.dll");
    }

    /// <summary>Sabotage S-proc-1.</summary>
    [Fact]
    public async Task AClusterInAProcessOfItsOwnIsDrivenFromAnother()
    {
        var data = Path.Combine(Path.GetTempPath(), "raft-separate-" + Guid.NewGuid().ToString("N"));
        using var cluster = Process.Start(new ProcessStartInfo("dotnet", [HostDll(), "bench", "cluster", "--local", data, "--control-port", "28499", "--base-port", "28400", "--decompose", "1"]) { UseShellExecute = false })!;
        try
        {
            var probe = await ControlProbe.ConnectAsync(new DnsEndPoint("127.0.0.1", 28499), TimeSpan.FromSeconds(60));
            var leader = await probe.LeaderAsync(TimeSpan.FromSeconds(15));
            var before = await probe.CountersAsync();
            var endpoints = probe.Clients.ToDictionary(c => c.Key, c => c.Value);
            var r = await LoadGenerator.RunAsync(new LoadConfig(endpoints, Rate: 0, Duration: TimeSpan.FromSeconds(1), Warmup: TimeSpan.FromSeconds(0.3), ClosedClients: 8), Ct);
            var after = await probe.CountersAsync();
            var fields = Program.LeaderFields(before, after, leader, r.Answered, 1_000_000, 1_000);
            var traces = await probe.TracesAsync();
            var roles = await probe.RolesAsync();

            Assert.Equal(cluster.Id, probe.ProcessId);
            Assert.NotEqual(ProcessToken.Value, probe.Instance);
            Assert.Equal(1, Program.GeneratorOwnProcess(probe));
            Assert.True(r.Answered > 50, $"{r.Answered} writes answered");
            Assert.InRange(fields["leader_syncs_per_write"], 0.01, 3);
            Assert.Equal(3, traces.Count);
            Assert.All(traces.Values, t => Assert.NotEmpty(HandOffTrace.Read(t)));
            Assert.Contains(roles, n => n.Changes.Any(c => c.To == Raft.Core.Role.Leader));
            await probe.StopAsync();
            Assert.True(cluster.WaitForExit(30_000), "the cluster's process did not stop when asked");
        }
        finally
        {
            if (!cluster.HasExited)
            {
                cluster.Kill(entireProcessTree: true);
            }

            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The same cluster in this process: the generator shares the hosts' process, and the record says so.</summary>
    [Fact]
    public async Task AClusterInThisProcessSharesTheGeneratorsProcess()
    {
        var data = Path.Combine(Path.GetTempPath(), "raft-shared-" + Guid.NewGuid().ToString("N"));
        var local = new LocalCluster(data, basePort: 28600);
        await using (local)
        {
            local.Start();
            var probe = new LocalProbe(local);
            await probe.LeaderAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(ProcessToken.Value, probe.Instance);
            Assert.Equal(0, Program.GeneratorOwnProcess(probe));
        }
    }
}
