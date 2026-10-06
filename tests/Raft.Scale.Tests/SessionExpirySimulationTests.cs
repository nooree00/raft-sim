using System;
using System.IO;
using System.Linq;
using System.Text;
using Raft.Checker;
using Raft.Core;
using Raft.Core.Tests;
using Raft.Simulation;
using Xunit;

namespace Raft.Scale.Tests;

/// <summary>
/// P9-01: session expiry over the soak's generated executions, at a bound of 2 sessions for 3
/// clients, so that registrations evict constantly. Every invariant, the agreement check (its replay
/// at the same bound) and linearizability, per execution; and the dimension "a session evicted and
/// its client registered again", read from the server's answer (`unknown-session|` to a client that
/// held a session) and the client's next registration answered, never from the client's own decision
/// to register. Vacuity risk: a bound the clients never reach evicts nothing; guarded by the
/// dimension's floor. Thirty executions (4 s): every execution evicts, so the floor needs few, and every
/// Scale harness entry runs this project's tests as its neighbours (phase 7's shard 10). The
/// measurement over 100 (every execution evicted) is in the breakdown.
/// </summary>
public sealed class SessionExpirySimulationTests
{
    private const int Executions = 30, Bound = 2;

    [Fact]
    public void ASmallSessionTableEvictsAndEveryCheckHolds()
    {
        var evicted = 0;
        long refusedUnknown = 0;
        for (var seed = 1; seed <= Executions; seed++)
        {
            var probe = new AgreementProbe(maxSessions: Bound);
            var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = SoakConfig.FaultsUntil });
            var (sim, h) = Cluster.Run((ulong)seed, SoakConfig.Duration, schedule, SoakConfig.Clients, new SessionWorkload(new RaftWorkload(int.MaxValue, retry: true, think: SoakConfig.Think), Cluster.Nodes),
                node: ctx => new RaftNode(ctx, SoakConfig.Options, probe.For(ctx.Id)));
            var observations = sim.Observations.ToList();
            var stable = Stability.StableFrom(schedule, observations);
            var log = new LogAnalysis(LogHistory.FromObservations(observations, h, Cluster.Nodes));
            foreach (var r in Cluster.Check(h, stable, SoakConfig.Duration).Concat(LogAnalysis.Names.Select(log.Result)))
            {
                Assert.True(r.Holds, $"seed {seed}, {r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
            }

            var (disagreements, _) = probe.Check(observations, log);
            Assert.True(disagreements.Count == 0, $"seed {seed}: {string.Join("; ", disagreements.Take(3))}");
            var client = ClientHistory.From(sim.ClientLog);
            Assert.True(client.Unexplained.Count == 0, $"seed {seed}: {string.Join("; ", client.Unexplained.Take(3))}");
            var lin = WglChecker.Check(client.History, SoakConfig.CheckerBudget);
            Assert.True(lin.Verdict == Verdict.Linearizable, $"seed {seed}: {lin.Verdict} at key {lin.Key}");

            var ops = sim.ClientLog.Where(o => o.Response is not null).OrderBy(o => o.Response).ToList();
            var unknown = ops.Where(o => Encoding.ASCII.GetString(o.Reply.Span) == "unknown-session|").ToList();
            refusedUnknown += unknown.Count;
            evicted += unknown.Any(u => ops.Any(o => o.Client == u.Client && o.Response > u.Response && Encoding.ASCII.GetString(o.Request.Span) == "Register|" && Encoding.ASCII.GetString(o.Reply.Span).StartsWith("ok|", StringComparison.Ordinal))) ? 1 : 0;
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "session-expiry.txt"),
            $"{Executions} executions, {SoakConfig.Clients} clients, a table of {Bound}: a session evicted and its client registered again in {evicted}; unknown-session refusals {refusedUnknown}\n");
        Assert.True(evicted >= Coverage.FloorFor(Executions), $"a session evicted and its client registered again in only {evicted} of {Executions} executions");
    }
}
