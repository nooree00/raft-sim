using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Checker;
using Raft.Core;
using Raft.Core.Tests;
using Raft.Simulation;
using Xunit;

namespace Raft.Scale.Tests;

/// <summary>
/// P3-08: invariants 1, 8, 9 and 11 over generated executions with partitions (asymmetric
/// included), crashes and every other generated fault, and the distribution of what happened,
/// measured as effects (P2-02's rules: a floor, a near-always declaration, identical sets). The
/// suite runs a fixed sample of 300; the `soak` CI job runs the same test over 10,000
/// (RAFT_SOAK_COUNT), and a harness entry's verdict is a claim about the sample only (spec §12).
/// Vacuity risk: executions green because nothing happened. Guarded: every run must elect a leader
/// that acts; liveness must be checked in most runs, not skipped for want of a stable suffix; and
/// every effect below must clear the floor. Sabotages S-soak-1, S-soak-2, S-cov-7.
/// P4-07: with three clients retrying on timeout, the log invariants (2-7, 10) and invariant 11's
/// commit clause run too, and replication's effects join the distribution. Every run must commit
/// entries in fact, and the commit clause must be checked in most runs. Sabotages S-soak-4, S-soak-5,
/// S-cov-10.
/// P5-03: every execution's client history (P5-01's adapter) must be well formed and accepted by the
/// WGL checker; a search that exhausts its budget fails the run (P5 decision 3, without its declaration path). What
/// the histories contain is measured like effects: each mechanism a weak checker would also accept
/// without (indeterminate operations, concurrency, reads, each operation kind) must clear the floor
/// (P5 decision 5). Sabotages S-lin-1, S-lin-2.
/// </summary>
public sealed class SoakTests
{
    /// <summary>The soak's settings, shared with the budget tests (<see cref="SoakConfig"/>).</summary>
    public const long FaultsUntil = SoakConfig.FaultsUntil, Duration = SoakConfig.Duration, Think = SoakConfig.Think, CheckerBudget = SoakConfig.CheckerBudget;

    /// <summary>Clients per execution (<see cref="SoakConfig"/>).</summary>
    public const int Clients = SoakConfig.Clients;

    /// <summary>What replication did in a run (P4-07), measured from the log analysis and the observations.</summary>
    public static readonly string[] ReplicationEffects =
    [
        "follower-caught-up-by-backtracking", "conflicting-suffix-truncated", "leader-crashed-with-uncommitted-entries",
        "entry-committed-in-a-later-term", "command-retried-and-duplicated", "crash-with-a-log-write-in-flight",
    ];

    /// <summary>
    /// What a run's client history contains (P5-03). Not effects of the cluster but of the workload
    /// and the faults on it; a checker accepting histories that never contain one of these says
    /// nothing about it, so each must clear the floor. Kept apart from the effects because several
    /// are in nearly every run, which the effects' identical-set rule would misread.
    /// </summary>
    public static readonly string[] HistoryContents =
    [
        "history-indeterminate", "history-concurrent-on-a-key", "history-read-completed", "history-put-completed",
        "history-append-completed", "history-delete-completed", "history-cas-true", "history-cas-false", "history-write-retried",
    ];

    /// <summary>The <see cref="HistoryContents"/> one history holds.</summary>
    internal static HashSet<string> Contents(IReadOnlyList<Operation> history)
    {
        var hit = new HashSet<string>(StringComparer.Ordinal);
        void If(bool condition, string name)
        {
            if (condition)
            {
                hit.Add(name);
            }
        }

        var done = history.Where(o => !o.IsIndeterminate).ToList();
        If(history.Any(o => o.IsIndeterminate), "history-indeterminate");
        If(history.GroupBy(o => o.Key).Any(g => g.Any(a => g.Any(b => a != b && !a.Precedes(b) && !b.Precedes(a)))), "history-concurrent-on-a-key");
        If(done.Any(o => o.Kind == OpKind.Get), "history-read-completed");
        If(done.Any(o => o.Kind == OpKind.Put), "history-put-completed");
        If(done.Any(o => o.Kind == OpKind.Append), "history-append-completed");
        If(done.Any(o => o.Kind == OpKind.Delete), "history-delete-completed");
        If(done.Any(o => o.Kind == OpKind.CompareAndSwap && o.Output == "true"), "history-cas-true");
        If(done.Any(o => o.Kind == OpKind.CompareAndSwap && o.Output == "false"), "history-cas-false");
        If(history.Where(o => o.Value is not null).GroupBy(o => (o.Kind, o.Key, o.Value)).Any(g => g.Count() > 1), "history-write-retried");
        return hit;
    }

    public static readonly string[] ElectionEffects =
    [
        "split-vote", "leader-replaced-after-crash", "vote-denied-already-voted", "requestvote-ignored",
        "leader-stepped-down", "candidate-stepped-down", "elected-after-restart", "elected-during-one-way-partition",
    ];

    /// <summary>When the last fault's effect ends (<see cref="Stability.StableFrom"/>, shared with the membership sample, P6-08).</summary>
    internal static long StableFrom(FaultSchedule schedule, IEnumerable<Observation> observations) => Stability.StableFrom(schedule, observations);

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

    /// <summary>The replication effects one execution produced.</summary>
    internal static HashSet<string> Replication(IReadOnlyList<Observation> observations, ElectionHistory h, LogAnalysis log)
    {
        var hit = new HashSet<string>(StringComparer.Ordinal);

        // A follower refused an AppendEntries of its term, and later accepted one from the same leader in that term.
        var refused = new HashSet<(NodeId, NodeId, Term)>();
        foreach (var s in h.Sends)
        {
            if (s.Message is AppendEntriesResponse r)
            {
                if (!r.Success)
                {
                    refused.Add((s.From, s.To, r.Term));
                }
                else if (refused.Contains((s.From, s.To, r.Term)))
                {
                    hit.Add("follower-caught-up-by-backtracking");
                    break;
                }
            }
        }

        if (log.Counts["suffixes-truncated"] > 0)
        {
            hit.Add("conflicting-suffix-truncated");
        }

        if (log.Counts["leaders-crashed-with-uncommitted-entries"] > 0)
        {
            hit.Add("leader-crashed-with-uncommitted-entries");
        }

        if (log.Counts["committed-in-a-later-term"] > 0)
        {
            hit.Add("entry-committed-in-a-later-term");
        }

        // Two entries committed with the same bytes: every write a client composes carries a value unique
        // to the client and its sequence, so the second is a retry of the first (P4 decision 5: expected
        // in phase 4). A Get or a Delete carries no value, and two of them on one key are two operations.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in log.Commits.Where(c => c.Command))
        {
            if (log.CommandOf(c.Ghost) is { } bytes && bytes is not ([(byte)'G', (byte)'e', (byte)'t', ..] or [(byte)'D', (byte)'e', (byte)'l', ..]) && !seen.Add(Convert.ToHexString(bytes)))
            {
                hit.Add("command-retried-and-duplicated");
                break;
            }
        }

        // A node crashed with an entry-log write issued and not yet durable.
        var pending = new Dictionary<NodeId, int>();
        foreach (var o in observations)
        {
            switch (o)
            {
                case IssuedObservation { Op.File: EntryLog.FileName }:
                    pending[o.Node] = pending.GetValueOrDefault(o.Node) + 1;
                    break;
                case DurableObservation { File: EntryLog.FileName, Completed: not null }:
                    pending[o.Node] = pending.GetValueOrDefault(o.Node) - 1;
                    break;
                case StartObservation:
                    pending[o.Node] = 0;
                    break;
                case CrashObservation when pending.GetValueOrDefault(o.Node) > 0:
                    hit.Add("crash-with-a-log-write-in-flight");
                    break;
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
        var effects = ElectionEffects.Concat(ReplicationEffects).Concat(Coverage.Dimensions).ToDictionary(d => d, _ => new HashSet<int>(), StringComparer.Ordinal);
        var timeToLeader = new List<long>();
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        int liveness = 0, noSuffix = 0, commitChecked = 0;
        long elections = 0, committed = 0, minCommitted = long.MaxValue;
        var timeToCommit = new List<long>();
        var contents = HistoryContents.ToDictionary(d => d, _ => 0, StringComparer.Ordinal);
        var undecided = new List<string>();
        var unverified = new List<string>();
        var limits = KnownLimits.Recorded;
        var limitFailures = new List<string>();
        long operations = 0, indeterminate = 0, refused = 0, maxStates = 0;
        var hardest = new List<(long States, int Seed)>();
        var checking = new System.Diagnostics.Stopwatch();
        var total = System.Diagnostics.Stopwatch.StartNew();
        var maxPerKey = 0;
        for (var seed = first; seed < first + count; seed++)
        {
            var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = FaultsUntil });
            var (sim, h) = Cluster.Run((ulong)seed, Duration, schedule, Clients, new RaftWorkload(int.MaxValue, retry: true, think: Think));
            var observations = sim.Observations.ToList();
            var stable = StableFrom(schedule, observations);
            var results = Cluster.Check(h, stable, Duration);
            var log = new LogAnalysis(LogHistory.FromObservations(observations, h, Cluster.Nodes));
            var commit = CommitLiveness.Clause(h, observations, log, stable, Duration, Cluster.Window, 2 * Cluster.Window);
            foreach (var r in results.Concat(LogAnalysis.Names.Select(log.Result)).Append(commit))
            {
                Assert.True(r.Holds, $"seed {seed}, {r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
            }

            var client = ClientHistory.From(sim.ClientLog);
            Assert.True(client.Unexplained.Count == 0, $"seed {seed}: {string.Join("; ", client.Unexplained.Take(3))}");
            Assert.True(client.Accounted == sim.ClientLog.Count && client.History.Count == client.Completed + client.Indeterminate, $"seed {seed}: the adapter lost operations");
            var problems = History.Problems(client.History);
            Assert.True(problems.Count == 0, $"seed {seed}, the client history is malformed: {string.Join("; ", problems.Take(3))}");
            checking.Start();
            var lin = WglChecker.Check(client.History, CheckerBudget);
            checking.Stop();
            hardest.Add((lin.StatesExplored, seed));
            Assert.True(lin.Verdict != Verdict.NotLinearizable, $"seed {seed}, linearizability at key {lin.Key}: no linearization past\n  {string.Join("\n  ", lin.LongestPrefix.TakeLast(5))}\nof\n  {string.Join("\n  ", lin.SubHistory.Take(40))}");
            var (recorded, limitFailure) = KnownLimits.Judge(limits, seed, client.History, lin);
            if (limitFailure is not null)
            {
                limitFailures.Add(limitFailure);
            }

            if (lin.Verdict == Verdict.Undecided && recorded)
            {
                var entry = limits.Single(e => e.Seed == seed);
                unverified.Add($"{entry.Id} (seed {seed}, key {entry.Key}). Its linearizability is unknown: the search exhausts {CheckerBudget} states without a verdict. Recorded as a known limit against the register row \"{entry.RegisterRow}\" ({entry.ApprovedIn})");
            }
            else if (lin.Verdict == Verdict.Undecided)
            {
                undecided.Add($"seed {seed} (key {lin.Key}, {lin.SubHistory.Count} operations)");
            }

            operations += client.History.Count;
            indeterminate += client.Indeterminate;
            refused += client.Refused;
            maxStates = Math.Max(maxStates, lin.StatesExplored);
            maxPerKey = Math.Max(maxPerKey, client.History.GroupBy(o => o.Key).Select(g => g.Count()).DefaultIfEmpty(0).Max());
            foreach (var c in Contents(client.History))
            {
                contents[c]++;
            }

            var entries = log.Counts["entries-committed"];
            Assert.True(entries > 0, $"seed {seed}: nothing was committed in fact");
            committed += entries;
            minCommitted = Math.Min(minCommitted, entries);
            commitChecked += (int)commit.Count("checked");
            if (commit.Counts.TryGetValue("time-to-commit", out var tc))
            {
                timeToCommit.Add(tc);
            }

            Assert.True(results[0].Count("acting-leaders") >= 1, $"seed {seed}: no leader was elected and acted");
            elections += results[0].Count("elections");
            liveness += (int)results[3].Count("checked");
            noSuffix += (int)results[3].Count("no-stable-suffix");
            if (results[3].Counts.TryGetValue("time-to-leader", out var t))
            {
                timeToLeader.Add(t);
            }

            foreach (var e in Effects(h, sim.Trace.Lines, schedule).Concat(Replication(observations, h, log)).Concat(Coverage.Of(sim.Trace.Lines)))
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
        failures.AddRange(RateFailures(effects.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal), count));
        failures.AddRange(ContentFailures(contents, count));
        failures.AddRange(BudgetFailures(undecided, count));
        failures.AddRange(limitFailures);

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
            $"invariants 1-11 (membership aside): no violation; elections {elections}; {Clients} clients, retrying on timeout",
            $"liveness checked in {liveness}, no stable suffix in {noSuffix}; the commit clause checked in {commitChecked}",
            $"entries committed in fact: {committed} ({committed / count} per execution, fewest {minCommitted})",
            timeToCommit.Count == 0 ? "time to commit: none measured" : $"time to a command committed after the stable suffix: max {timeToCommit.Max()}, mean {timeToCommit.Average().ToString("F0", CultureInfo.InvariantCulture)} (window {Cluster.Window})",
            timeToLeader.Count == 0 ? "time to leader: none measured" : $"time to leader after the stable suffix: max {timeToLeader.Max()}, mean {timeToLeader.Average().ToString("F0", CultureInfo.InvariantCulture)} (window {Cluster.Window})",
            $"linearizability (WGL, budget {CheckerBudget} states per key): {count - undecided.Count - unverified.Count} checked and accepted, 0 rejected; {unverified.Count} unverified{(unverified.Count == 0 ? "" : ": " + string.Join("; ", unverified))}; any other undecided search fails the run ({undecided.Count} here); most states explored {maxStates}",
            $"hardest histories (states explored, seed): {string.Join(", ", hardest.OrderByDescending(x => x.States).Take(5).Select(x => $"{x.States} seed {x.Seed}"))}",
            $"time: checking {checking.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s of {total.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s",
            $"client histories: {operations} operations ({operations / count} per execution), {indeterminate} indeterminate, {refused} refusals left out; largest per-key sub-history {maxPerKey}",
            $"history contents (floor {Coverage.FloorFor(count, FloorRate)}):",
        };
        report.AddRange(contents.Select(kv => $"  {kv.Key,-42} {kv.Value,6} / {count}  ({Coverage.Rate(kv.Value, count)})"));
        report.AddRange(undecided.Take(20).Select(u => "  undecided: " + u));
        report.Add($"effects (floor {Coverage.FloorFor(count, FloorRate)}: 1% of {count}, at least {Coverage.Floor}):");
        report.AddRange(effects.Select(kv => $"  {kv.Key,-42} {kv.Value.Count,6} / {count}  ({Coverage.Rate(kv.Value.Count, count)})"
            + (kv.Value.Count < Coverage.FloorFor(count, FloorRate) && BelowTheSoakFloor.ContainsKey(kv.Key) ? "  declared below the floor" : "")));
        report.AddRange(failures.Select(f => "  FAIL " + f));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "soak-report.txt"), string.Join("\n", report) + "\n");

        Assert.True(commitChecked >= count * 3 / 4, $"the commit clause was checked in only {commitChecked} of {count} runs: invariant 11's second clause was barely tested");
        Assert.True(liveness >= count * 3 / 4, $"liveness checked in only {liveness} of {count} runs: most had no stable suffix, so invariant 11 was barely tested");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    public const int SoakExecutions = 10_000;

    /// <summary>Each of <see cref="HistoryContents"/> must clear the floor at the run's own scale (P5 decision 5).</summary>
    internal static List<string> ContentFailures(IReadOnlyDictionary<string, int> contents, int count) =>
        contents.Where(kv => kv.Value < Coverage.FloorFor(count, FloorRate))
            .Select(kv => $"{kv.Key}: in {kv.Value} of {count} histories, below the floor of {Coverage.FloorFor(count, FloorRate)}: a checker accepting these histories says nothing about it")
            .ToList();

    /// <summary>
    /// P5 decision 3: a budget-exhausted search is never acceptance, and it is never declared. An
    /// undecided search is a checker that could not answer; a declaration below the floor says an
    /// effect is rare and why, a different claim, and the floor was built for effects (reviewer, P5-05:
    /// the declaration path decision 3 first allowed is closed). Sabotages S-lin-1, S-lin-6.
    /// </summary>
    internal static List<string> BudgetFailures(IReadOnlyList<string> undecided, int count) =>
        undecided.Count == 0
            ? []
            : [$"linearizability undecided in {undecided.Count} of {count} runs (budget exhausted): {string.Join(", ", undecided.Take(5))}"];

    /// <summary>
    /// The soak's own floor rate (P3 acceptance): held at the suite's, so that 10,000 executions must
    /// show each effect at the rate 300 do (3 in 300), not merely the same count. The absolute minimum
    /// of <see cref="Coverage.Floor"/> stays under it.
    /// </summary>
    public const double FloorRate = Coverage.FloorRate;

    /// <summary>
    /// The floor and near-always rules over one run's effect counts, at its own scale: the floor is
    /// <see cref="FloorRate"/> of the executions, at least <see cref="Coverage.Floor"/>. A declared
    /// effect may sit below the rate, never below the absolute minimum.
    /// </summary>
    internal static List<string> RateFailures(IReadOnlyDictionary<string, int> counts, int count, IReadOnlyDictionary<string, string>? belowTheFloor = null)
    {
        belowTheFloor ??= BelowTheSoakFloor;
        var failures = new List<string>();
        var floor = Coverage.FloorFor(count, FloorRate);
        foreach (var (d, n) in counts)
        {
            var sampleRare = count < SoakExecutions && RareInSample.ContainsKey(d);
            var declared = n >= Coverage.Floor && belowTheFloor.ContainsKey(d);
            if (n < floor && !sampleRare && !declared)
            {
                failures.Add($"{d}: {n} of {count} ({Coverage.Rate(n, count)}), below the floor of {floor} (1% of {count}, at least {Coverage.Floor})");
            }

            if (n >= Coverage.NearAlways * count && !AlwaysOn.ContainsKey(d))
            {
                failures.Add($"{d}: {n} of {count} (95% or more), not declared always-on");
            }
        }

        return failures;
    }

    /// <summary>
    /// Effects allowed below the soak's rate floor (never below the absolute minimum), each with the
    /// reason and the test that exercises the effect directly. Put to the reviewer at P3 acceptance.
    /// Empty since P4-07: writes completed out of order at a crash, declared then (8 of 10,000),
    /// reached 165 of 10,000 once logs were written on every replicated batch.
    /// </summary>
    internal static readonly Dictionary<string, string> BelowTheSoakFloor = new(StringComparer.Ordinal);

    /// <summary>
    /// The soak's floor at its own scale, tested here because the harness only ever runs the
    /// 300-execution sample (spec §12): at 10,000 an effect must reach 1%, not 3 executions.
    /// </summary>
    [Fact]
    public void AtSoakScaleTheFloorHoldsTheRateNotTheCount()
    {
        Dictionary<string, int> Counts(int total) => ElectionEffects.Concat(Coverage.Dimensions).ToDictionary(d => d, _ => total / 2, StringComparer.Ordinal);

        var soak = Counts(SoakExecutions);
        soak["split-vote"] = 50;
        Assert.True(RateFailures(soak, SoakExecutions).Any(f => f.StartsWith("split-vote: 50 of 10000 (0.5%), below the floor of 100", StringComparison.Ordinal)),
            "rate floor: 50 of 10,000 clears the absolute 3 and must still fail");

        soak["split-vote"] = 100;
        Assert.Empty(RateFailures(soak, SoakExecutions));

        var sample = Counts(300);
        sample["split-vote"] = 3;
        Assert.Empty(RateFailures(sample, 300));
        sample["split-vote"] = 2;
        Assert.Contains(RateFailures(sample, 300), f => f.StartsWith("split-vote: 2 of 300", StringComparison.Ordinal));

        const string Declared = "writes-completed-out-of-order-at-crash";
        var declaration = new Dictionary<string, string>(StringComparer.Ordinal) { [Declared] = "declared for this test" };
        soak["split-vote"] = 100;
        soak[Declared] = 8;
        Assert.Empty(RateFailures(soak, SoakExecutions, declaration));
        soak[Declared] = 2;
        Assert.True(RateFailures(soak, SoakExecutions, declaration).Any(f => f.StartsWith(Declared + ": 2 of 10000", StringComparison.Ordinal)),
            "absolute minimum: a declared effect may sit below the rate, never below 3");
    }

    /// <summary>P5 decision 3: one undecided search fails the run, however rare, at any scale.</summary>
    [Fact]
    public void AnExhaustedSearchAlwaysFailsTheRun()
    {
        Assert.Empty(BudgetFailures([], 300));
        Assert.Contains("undecided in 1 of 300", Assert.Single(BudgetFailures(["seed 7 (key k1, 80 operations)"], 300)), StringComparison.Ordinal);
        Assert.Contains("undecided in 1 of 10000", Assert.Single(BudgetFailures(["seed 7"], SoakExecutions)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Effects allowed below the floor in the 300-execution sample only, each with its reason; the soak
    /// (10,000) must still clear the floor for them.
    /// </summary>
    private static readonly Dictionary<string, string> RareInSample = new(StringComparer.Ordinal);

    /// <summary>Effects at or above 95%, each with the reason the other case is rare.</summary>
    private static readonly Dictionary<string, string> AlwaysOn = new(StringComparer.Ordinal);
}
