using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P3-08: invariants 1, 8, 9 and 11 over generated executions with partitions (asymmetric
/// included), crashes and every other generated fault, and the distribution of what happened,
/// measured as effects (P2-02's rules: a floor, a near-always declaration, identical sets). The
/// suite runs a fixed sample of 300; the `soak` CI job runs the same test over 10,000
/// (RAFT_SOAK_COUNT), and a harness entry's verdict is a claim about the sample only (spec §12).
/// Vacuity risk: executions green because nothing happened. Guarded: every run must elect a leader
/// that acts; liveness must be checked in most runs, not skipped for want of a stable suffix; and
/// every effect below must clear the floor. Sabotages S-soak-1, S-soak-2, S-cov-7.
/// </summary>
public sealed class SoakTests
{
    public const long FaultsUntil = 12_000, Duration = 20_000;

    public static readonly string[] ElectionEffects =
    [
        "split-vote", "leader-replaced-after-crash", "vote-denied-already-voted", "requestvote-ignored",
        "leader-stepped-down", "candidate-stepped-down", "elected-after-restart", "elected-during-one-way-partition",
    ];

    /// <summary>
    /// When the last fault's effect ends: heals, restarts, unpauses and "until"s from the schedule, and
    /// the last observed restart, which covers the state-placed crashes (their firing time is not in
    /// the schedule). Clock skew and FIFO links are conditions, not faults that heal.
    /// </summary>
    internal static long StableFrom(FaultSchedule schedule, IEnumerable<Observation> observations)
    {
        var f = schedule.Faults;
        long After<T>(Fault x, Func<T, bool> match)
            where T : Fault => f.OfType<T>().Where(y => y.At >= x.At && match(y)).Select(y => y.At).DefaultIfEmpty(long.MaxValue).Min();
        var end = 0L;
        foreach (var x in f)
        {
            end = Math.Max(end, x switch
            {
                Partition p => After<Heal>(p, h => h.From == p.From && h.To == p.To),
                Crash c => After<Restart>(c, r => r.Node == c.Node),
                Pause p => After<Unpause>(p, u => u.Node == p.Node),
                Isolate i => i.Until,
                CrashAll a => a.Until,
                CrashMajority m => m.Until,
                SlowDisk s => s.Until,
                BarrierViolation b => b.Until,
                Delay d => d.At + d.Extra,
                Skew or Fifo => 0,
                _ => x.At,
            });
        }

        var restarts = observations.OfType<StartObservation>().Where(s => s.Incarnation > 1).Select(s => s.Time);
        return Math.Max(end, restarts.DefaultIfEmpty(0).Max());
    }

    /// <summary>The election effects one execution produced, computed from observations and the trace.</summary>
    internal static HashSet<string> Effects(ElectionHistory h, IReadOnlyList<string> trace, FaultSchedule schedule)
    {
        var hit = new HashSet<string>(StringComparer.Ordinal);
        var elected = h.Elections();
        var terms = h.Sends.Where(s => s.Message is RequestVote).Select(s => s.Message.Term).Distinct();
        if (terms.Any(t => !elected.Keys.Any(k => k.Term == t)))
        {
            hit.Add("split-vote");
        }

        foreach (var ((term, leader), at) in elected)
        {
            var crash = h.Liveness.Where(l => l.Node == leader && !l.Up && l.Time > at).Select(l => l.Time).DefaultIfEmpty(long.MaxValue).Min();
            if (crash < long.MaxValue && elected.Any(e => e.Key.Term > term && e.Value > crash))
            {
                hit.Add("leader-replaced-after-crash");
            }

            if (h.Liveness.Any(l => l.Node == leader && l.Up && l.Time > 0 && l.Time < at))
            {
                hit.Add("elected-after-restart");
            }

            var partitions = schedule.Faults.OfType<Partition>().Where(p => p.At <= at
                && !schedule.Faults.OfType<Heal>().Any(x => x.From == p.From && x.To == p.To && x.At >= p.At && x.At <= at)).ToList();
            if (partitions.Any(p => !partitions.Any(q => q.From == p.To && q.To == p.From)))
            {
                hit.Add("elected-during-one-way-partition");
            }
        }

        var latest = new Dictionary<NodeId, TermVoteState>();
        foreach (var e in h.States.Select(s => (s.Seq, State: (TermVoteState?)s.State, s.Node, Denial: (ElectionHistory.Sent?)null))
            .Concat(h.Sends.Where(s => s.Message is RequestVoteResponse { VoteGranted: false }).Select(s => (s.Seq, State: (TermVoteState?)null, Node: s.From, Denial: (ElectionHistory.Sent?)s)))
            .OrderBy(e => e.Seq))
        {
            if (e.State is not null)
            {
                latest[e.Node] = e.State;
            }
            else if (latest.TryGetValue(e.Node, out var now) && now.Term == e.Denial!.Message.Term && now.VotedFor is { } v && v != e.Denial.To)
            {
                hit.Add("vote-denied-already-voted");
            }
        }

        foreach (var (evt, effect) in new[] { ("requestvote-ignored", "requestvote-ignored"), ("leader-steps-down", "leader-stepped-down"), ("candidate-steps-down", "candidate-stepped-down") })
        {
            if (trace.Any(l => l.Contains(" EVENT name=" + evt, StringComparison.Ordinal)))
            {
                hit.Add(effect);
            }
        }

        return hit;
    }

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    [Fact]
    public void TheGeneratedSampleHoldsEveryInvariant()
    {
        var first = Env("RAFT_SOAK_FIRST", 1);
        var count = Env("RAFT_SOAK_COUNT", 300);
        var effects = ElectionEffects.Concat(Coverage.Dimensions).ToDictionary(d => d, _ => new HashSet<int>(), StringComparer.Ordinal);
        var timeToLeader = new List<long>();
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        int liveness = 0, noSuffix = 0;
        long elections = 0;
        for (var seed = first; seed < first + count; seed++)
        {
            var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = FaultsUntil });
            var (sim, h) = Cluster.Run((ulong)seed, Duration, schedule);
            var stable = StableFrom(schedule, sim.Observations);
            var results = Cluster.Check(h, stable, Duration);
            foreach (var r in results)
            {
                Assert.True(r.Holds, $"seed {seed}, {r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
            }

            Assert.True(results[0].Count("acting-leaders") >= 1, $"seed {seed}: no leader was elected and acted");
            elections += results[0].Count("elections");
            liveness += (int)results[3].Count("checked");
            noSuffix += (int)results[3].Count("no-stable-suffix");
            if (results[3].Counts.TryGetValue("time-to-leader", out var t))
            {
                timeToLeader.Add(t);
            }

            foreach (var e in Effects(h, sim.Trace.Lines, schedule).Concat(Coverage.Of(sim.Trace.Lines)))
            {
                if (effects.TryGetValue(e, out var set))
                {
                    set.Add(seed);
                }
                else
                {
                    unknown.Add(e);
                }
            }
        }

        var failures = unknown.Where(e => !e.StartsWith("control:", StringComparison.Ordinal)).Select(e => $"{e}: produced but not a declared effect").ToList();
        foreach (var (d, set) in effects)
        {
            if (set.Count < Coverage.Floor && !(count < SoakExecutions && RareInSample.ContainsKey(d)))
            {
                failures.Add($"{d}: {set.Count} of {count}, below the floor of {Coverage.Floor}");
            }

            if (set.Count >= Coverage.NearAlways * count && !AlwaysOn.ContainsKey(d))
            {
                failures.Add($"{d}: {set.Count} of {count} (95% or more), not declared always-on");
            }
        }

        var names = effects.Keys.ToList();
        for (var i = 0; i < names.Count; i++)
        {
            for (var j = i + 1; j < names.Count; j++)
            {
                if (effects[names[i]].Count > 0 && effects[names[i]].SetEquals(effects[names[j]]))
                {
                    failures.Add($"{names[i]} and {names[j]}: exactly the same executions, one measurement wearing two names");
                }
            }
        }

        var report = new List<string>
        {
            $"{count} executions (seeds {first}..{first + count - 1}), faults generated until {FaultsUntil}, run to {Duration}",
            $"invariants 1, 8, 9, 11: no violation; elections {elections}",
            $"liveness checked in {liveness}, no stable suffix in {noSuffix}",
            timeToLeader.Count == 0 ? "time to leader: none measured" : $"time to leader after the stable suffix: max {timeToLeader.Max()}, mean {timeToLeader.Average().ToString("F0", CultureInfo.InvariantCulture)} (window {Cluster.Window})",
            "effects:",
        };
        report.AddRange(effects.Select(kv => $"  {kv.Key,-42} {kv.Value.Count,6} / {count}  ({(100.0 * kv.Value.Count / count).ToString("F0", CultureInfo.InvariantCulture)}%)"));
        report.AddRange(failures.Select(f => "  FAIL " + f));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "soak-report.txt"), string.Join("\n", report) + "\n");

        Assert.True(liveness >= count * 3 / 4, $"liveness checked in only {liveness} of {count} runs: most had no stable suffix, so invariant 11 was barely tested");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    public const int SoakExecutions = 10_000;

    /// <summary>
    /// Effects allowed below the floor in the 300-execution sample only, each with its reason; the soak
    /// (10,000) must still clear the floor for them.
    /// </summary>
    private static readonly Dictionary<string, string> RareInSample = new(StringComparer.Ordinal)
    {
        ["writes-completed-out-of-order-at-crash"] = "8 of 10,000 in the soak, 0 of 300 here: a Raft node writes only when its term or vote changes, so two writes in flight at a crash need two changes within one disk latency (1-3 ticks)",
    };

    /// <summary>Effects at or above 95%, each with the reason the other case is rare.</summary>
    private static readonly Dictionary<string, string> AlwaysOn = new(StringComparer.Ordinal);
}
