using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Checker;
using Raft.Core;
using Raft.Core.Tests;
using Raft.Scale.Tests;
using Raft.Simulation;
using Xunit;

namespace Raft.Membership.Tests;

/// <summary>
/// P6-08: generated executions with membership changes. Five nodes, three of them the initial
/// configuration and two spares; the soak's faults over all five, its clients, and membership
/// requests among their operations (<see cref="MembershipWorkload"/>). Every invariant (the checkers
/// count quorums of the configuration in effect, P6-04), linearizability over the client histories,
/// and the distribution of what the changes did, as functions of the observations: a change completed
/// while a partition was in force, a change that removed the leader, a crash while a node was in a
/// joint configuration, and a change still joint at the end. Vacuity risk: a generator whose changes
/// happen only in fault-free stretches never tests the phase's done criterion (joint consensus under
/// partition); every dimension has a floor. Sabotage S-cov-11.
/// </summary>
public sealed class MembershipSimulationTests
{
    public const int Spares = 2, Universe = Cluster.Nodes + Spares;

    public static readonly string[] Dimensions =
    [
        "membership-change-completed", "change-completed-during-a-partition", "change-removed-the-leader",
        "crash-in-a-joint-configuration", "change-still-joint-at-the-end",
    ];

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    /// <summary>One generated execution with membership changes.</summary>
    internal static (Simulator Sim, ElectionHistory History, FaultSchedule Schedule) Run(int seed)
    {
        var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = SoakConfig.FaultsUntil, Nodes = Universe });
        var (sim, h) = Cluster.Run((ulong)seed, SoakConfig.Duration, schedule, SoakConfig.Clients, new MembershipWorkload(SoakConfig.Think), Spares);
        return (sim, h, schedule);
    }

    /// <summary>Whether a partition or an isolation was in force at <paramref name="time"/>.</summary>
    internal static bool Partitioned(FaultSchedule schedule, long time) =>
        schedule.Faults.OfType<Partition>().Any(p => p.At <= time && time < schedule.Faults.OfType<Heal>().Where(h => h.From == p.From && h.To == p.To && h.At >= p.At).Select(h => h.At).DefaultIfEmpty(long.MaxValue).Min())
        || schedule.Faults.OfType<Isolate>().Any(i => i.At <= time && time < i.Until);

    /// <summary>The <see cref="Dimensions"/> one execution hit.</summary>
    internal static HashSet<string> Effects(IReadOnlyList<Observation> observations, ElectionHistory h, LogAnalysis log, FaultSchedule schedule)
    {
        var hit = new HashSet<string>(StringComparer.Ordinal);
        var elected = h.Elections();
        foreach (var c in log.Commits)
        {
            if (log.CommandOf(c.Ghost) is not { } command || !Configuration.IsInternal(command) || Configuration.Decode(command) is not { IsJoint: false } config || config == h.Initial)
            {
                continue;
            }

            hit.Add("membership-change-completed");
            var time = observations[(int)c.Seq - 1].Time;
            if (Partitioned(schedule, time))
            {
                hit.Add("change-completed-during-a-partition");
            }

            var leader = elected.Where(e => e.Value <= time).OrderBy(e => e.Key.Term.Value).Select(e => (NodeId?)e.Key.Candidate).LastOrDefault();
            if (leader is { } l && !config.Members.Contains(l))
            {
                hit.Add("change-removed-the-leader");
            }
        }

        foreach (var (seq, _, node) in h.CrashSeqs)
        {
            var before = h.Configurations.Where(x => x.Node == node && x.Seq < seq).Select(x => x.Config).LastOrDefault();
            if (before is { IsJoint: true })
            {
                hit.Add("crash-in-a-joint-configuration");
            }
        }

        if (h.Configurations.GroupBy(x => x.Node).Any(g => g.Last().Config.IsJoint))
        {
            hit.Add("change-still-joint-at-the-end");
        }

        return hit;
    }

    [Fact]
    public void TheMembershipSampleHoldsEveryInvariant()
    {
        var count = Env("RAFT_MEMBERSHIP_COUNT", 300);
        var effects = Dimensions.ToDictionary(d => d, _ => 0, StringComparer.Ordinal);
        var undecided = new List<string>();
        int liveness = 0, commitChecked = 0, membership = 0;
        for (var seed = 1; seed <= count; seed++)
        {
            var (sim, h, schedule) = Run(seed);
            var observations = sim.Observations.ToList();
            var stable = Stability.StableFrom(schedule, observations);
            var results = Cluster.Check(h, stable, SoakConfig.Duration);
            var log = new LogAnalysis(LogHistory.FromObservations(observations, h, Cluster.Nodes));
            var commit = CommitLiveness.Clause(h, observations, log, stable, SoakConfig.Duration, Cluster.Window, 2 * Cluster.Window);
            foreach (var r in results.Concat(LogAnalysis.Names.Select(log.Result)).Append(commit))
            {
                Assert.True(r.Holds, $"seed {seed}, {r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
            }

            var client = ClientHistory.From(sim.ClientLog);
            Assert.True(client.Unexplained.Count == 0, $"seed {seed}: {string.Join("; ", client.Unexplained.Take(3))}");
            Assert.True(client.Accounted == sim.ClientLog.Count, $"seed {seed}: the adapter lost operations");
            Assert.Empty(History.Problems(client.History));
            var lin = WglChecker.Check(client.History, SoakConfig.CheckerBudget);
            Assert.True(lin.Verdict != Verdict.NotLinearizable, $"seed {seed}, linearizability at key {lin.Key}: no linearization past\n  {string.Join("\n  ", lin.LongestPrefix.TakeLast(5))}");
            if (lin.Verdict == Verdict.Undecided)
            {
                undecided.Add($"seed {seed} (key {lin.Key}, {lin.SubHistory.Count} operations)");
            }

            membership += client.Membership;
            liveness += (int)results[3].Count("checked");
            commitChecked += (int)commit.Count("checked");
            foreach (var e in Effects(observations, h, log, schedule))
            {
                effects[e]++;
            }
        }

        var floor = Coverage.FloorFor(count);
        var report = new List<string>
        {
            $"{count} executions of {Universe} nodes ({Cluster.Nodes} configured, {Spares} spares), faults until {SoakConfig.FaultsUntil}, run to {SoakConfig.Duration}",
            $"membership requests {membership}; liveness checked in {liveness}, the commit clause in {commitChecked}; undecided searches {undecided.Count}",
            $"dimensions (floor {floor}):",
        };
        report.AddRange(effects.Select(kv => $"  {kv.Key,-42} {kv.Value,6} / {count}  ({Coverage.Rate(kv.Value, count)})"));
        report.AddRange(undecided.Select(u => "  undecided: " + u));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "membership-report.txt"), string.Join("\n", report) + "\n");

        Assert.True(undecided.Count == 0, "undecided linearizability searches (P5 decision 3: never acceptance): " + string.Join("; ", undecided));
        Assert.True(liveness >= count * 3 / 4, $"liveness checked in only {liveness} of {count} runs");
        var below = effects.Where(kv => kv.Value < floor).Select(kv => $"{kv.Key}: {kv.Value} of {count}, below the floor of {floor}").ToList();
        Assert.True(below.Count == 0, string.Join("\n", below));
    }
}
