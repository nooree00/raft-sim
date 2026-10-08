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
/// What one soak is (P6-12): its workload, its spares, its own dimensions and its own known limits.
/// The baseline soak (<see cref="SoakTests"/>) and the membership soak (Raft.Membership.Tests) are two
/// profiles of <see cref="Soak.Run"/>, not one soak covering both: they are different workloads (P6-10
/// measured 178 operations per execution against 114), each with its own floors.
/// </summary>
/// <param name="Report">The report's file name, next to the test assembly.</param>
/// <param name="Invariants">How the report qualifies "invariants 1-11".</param>
/// <param name="Extra">Per-execution hits of <paramref name="Dimensions"/>, beyond the shared effects.</param>
/// <param name="RareInSample">The profile's own effects allowed below the floor in its 300-execution sample only, each with its reason; its 10,000-execution soak must still clear the floor for them.</param>
internal sealed record SoakProfile(
    string Report,
    string Invariants,
    int Spares,
    Func<IClientWorkload> Workload,
    string LimitsFile,
    IReadOnlyList<string> Dimensions,
    Func<IReadOnlyList<Observation>, ElectionHistory, LogAnalysis, FaultSchedule, IEnumerable<string>>? Extra = null,
    IReadOnlyDictionary<string, string>? RareInSample = null);

/// <summary>
/// The soak's run and rules (P3-08, P4-07, P5-03; moved here from <see cref="SoakTests"/> at P6-12 so
/// that the membership soak runs the same code): every invariant, every client history well formed and
/// linearizable (a search that exhausts its budget fails the run unless it is one of the soak's own
/// recorded known limits), and the distribution of effects and history contents with their floors.
/// </summary>
internal static class Soak
{
    public const long FaultsUntil = SoakConfig.FaultsUntil, Duration = SoakConfig.Duration, CheckerBudget = SoakConfig.CheckerBudget;

    public const int Clients = SoakConfig.Clients;

    /// <summary>What replication did in a run (P4-07), measured from the log analysis and the observations.</summary>
    public static readonly string[] ReplicationEffects =
    [
        "follower-caught-up-by-backtracking", "conflicting-suffix-truncated", "leader-crashed-with-uncommitted-entries",
        "entry-committed-in-a-later-term", "crash-with-a-log-write-in-flight",
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
        "history-append-completed", "history-delete-completed", "history-cas-true", "history-cas-false", "history-command-retried",
    ];

    /// <summary>The <see cref="HistoryContents"/> one history holds.</summary>
    /// <param name="retries">Attempts that were retries of an operation already in the history (P8-00: a retry is the same operation, so the history itself no longer shows it).</param>
    internal static HashSet<string> Contents(IReadOnlyList<Operation> history, int retries = 0)
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
        If(retries > 0, "history-command-retried");
        return hit;
    }

    public static readonly string[] ElectionEffects =
    [
        "split-vote", "leader-replaced-after-crash", "vote-denied-already-voted", "requestvote-ignored",
        "leader-stepped-down", "candidate-stepped-down", "elected-after-restart", "elected-during-one-way-partition",
    ];


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

        // P4's "command-retried-and-duplicated" (two committed entries with the same bytes) retired at
        // P8-07: with every write in a session, the same bytes are the same (session, sequence), and
        // SessionCoverage's "retry-committed-twice-applied-once" hit exactly its executions.

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

    /// <summary>Runs <paramref name="count"/> executions from seed <paramref name="first"/> of <paramref name="profile"/>, asserting each, and writes its report.</summary>
    public static void Run(SoakProfile profile, int first, int count)
    {
        var effects = ElectionEffects.Concat(ReplicationEffects).Concat(Coverage.Dimensions).Concat(CompactionCoverage.Dimensions).Concat(SessionCoverage.Dimensions).Concat(profile.Dimensions).ToDictionary(d => d, _ => new HashSet<int>(), StringComparer.Ordinal);
        var compared = 0;
        var timeToLeader = new List<long>();
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        int liveness = 0, noSuffix = 0, commitChecked = 0;
        long elections = 0, committed = 0, minCommitted = long.MaxValue;
        var timeToCommit = new List<long>();
        var contents = HistoryContents.ToDictionary(d => d, _ => 0, StringComparer.Ordinal);
        var undecided = new List<string>();
        var unverified = new List<string>();
        var limits = KnownLimits.RecordedIn(profile.LimitsFile);
        var limitFailures = new List<string>();
        long operations = 0, indeterminate = 0, refused = 0, maxStates = 0, membership = 0;
        var hardest = new List<(long States, int Seed)>();
        var checking = new System.Diagnostics.Stopwatch();
        var total = System.Diagnostics.Stopwatch.StartNew();
        var maxPerKey = 0;
        for (var seed = first; seed < first + count; seed++)
        {
            var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = FaultsUntil, Nodes = Cluster.Nodes + profile.Spares });
            var probe = new AgreementProbe();
            var (sim, h) = Cluster.Run((ulong)seed, Duration, schedule, Clients, profile.Workload(), profile.Spares, node: ctx => new RaftNode(ctx, SoakConfig.Options, probe.For(ctx.Id)));
            var observations = sim.Observations.ToList();
            var stable = Stability.StableFrom(schedule, observations);
            var results = Cluster.Check(h, stable, Duration);
            var log = new LogAnalysis(LogHistory.FromObservations(observations, h, Cluster.Nodes));
            var commit = CommitLiveness.Clause(h, observations, log, stable, Duration, Cluster.Window, 2 * Cluster.Window);
            foreach (var r in results.Concat(LogAnalysis.Names.Select(log.Result)).Append(commit))
            {
                Assert.True(r.Holds, $"seed {seed}, {r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
            }

            // P7-08's done criterion in every execution: each node's state against the committed entries replayed.
            var (disagreements, n) = probe.Check(observations, log);
            Assert.True(disagreements.Count == 0, $"seed {seed}: {string.Join("; ", disagreements.Take(3))}");
            compared += n;

            var client = ClientHistory.From(sim.ClientLog);

            // P8-02's guard: every write is sent in a session, or deduplication is never exercised on it.
            var sessionless = sim.ClientLog.Count(o => System.Text.Encoding.ASCII.GetString(o.Request.Span) is var r
                && (r.StartsWith("Put|", StringComparison.Ordinal) || r.StartsWith("Append|", StringComparison.Ordinal) || r.StartsWith("Delete|", StringComparison.Ordinal) || r.StartsWith("Cas|", StringComparison.Ordinal)));
            Assert.True(sessionless == 0, $"seed {seed}: {sessionless} writes sent without a session");
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
            membership += client.Membership;
            indeterminate += client.Indeterminate;
            refused += client.Refused;
            maxStates = Math.Max(maxStates, lin.StatesExplored);
            maxPerKey = Math.Max(maxPerKey, client.History.GroupBy(o => o.Key).Select(g => g.Count()).DefaultIfEmpty(0).Max());
            foreach (var c in Contents(client.History, client.Retries))
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

            var hits = Effects(h, sim.Trace.Lines, schedule).Concat(Replication(observations, h, log)).Concat(Coverage.Of(sim.Trace.Lines)).Concat(CompactionCoverage.Of(observations)).Concat(SessionCoverage.Of(observations, log, h, sim.ClientLog, sim.Trace.Lines, SoakConfig.Options.HeartbeatInterval));
            foreach (var e in profile.Extra is null ? hits : hits.Concat(profile.Extra(observations, h, log, schedule)))
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
        failures.AddRange(RateFailures(effects.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal), count, rareInSample: profile.RareInSample));
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
            $"{count} executions (seeds {first}..{first + count - 1}), faults generated until {FaultsUntil}, run to {Duration}{(profile.Spares > 0 ? $", {Cluster.Nodes + profile.Spares} nodes ({Cluster.Nodes} configured, {profile.Spares} spares)" : "")}",
            $"invariants 1-11{profile.Invariants}: no violation; elections {elections}; {Clients} clients, retrying on timeout; compaction every {SoakConfig.SnapshotThreshold} applied entries",
            $"agreement (P7-08): {compared} node states compared with the committed entries replayed, no disagreement",
            $"liveness checked in {liveness}, no stable suffix in {noSuffix}; the commit clause checked in {commitChecked}",
            $"entries committed in fact: {committed} ({committed / count} per execution, fewest {minCommitted})",
            timeToCommit.Count == 0 ? "time to commit: none measured" : $"time to a command committed after the stable suffix: max {timeToCommit.Max()}, mean {timeToCommit.Average().ToString("F0", CultureInfo.InvariantCulture)} (window {Cluster.Window})",
            timeToLeader.Count == 0 ? "time to leader: none measured" : $"time to leader after the stable suffix: max {timeToLeader.Max()}, mean {timeToLeader.Average().ToString("F0", CultureInfo.InvariantCulture)} (window {Cluster.Window})",
            $"linearizability (WGL, budget {CheckerBudget} states per key): {count - undecided.Count - unverified.Count} checked and accepted, 0 rejected; {unverified.Count} unverified{(unverified.Count == 0 ? "" : ": " + string.Join("; ", unverified))}; any other undecided search fails the run ({undecided.Count} here); most states explored {maxStates}",
            $"hardest histories (states explored, seed): {string.Join(", ", hardest.OrderByDescending(x => x.States).Take(5).Select(x => $"{x.States} seed {x.Seed}"))}",
            $"time: checking {checking.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s of {total.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s",
            $"client histories: {operations} operations ({operations / count} per execution), {indeterminate} indeterminate, {refused} refusals left out; largest per-key sub-history {maxPerKey}{(profile.Spares > 0 ? $"; membership requests {membership}, counted apart" : "")}",
            $"history contents (floor {Coverage.FloorFor(count, FloorRate)}):",
        };
        report.AddRange(contents.Select(kv => $"  {kv.Key,-42} {kv.Value,6} / {count}  ({Coverage.Rate(kv.Value, count)})"));
        report.AddRange(undecided.Take(20).Select(u => "  undecided: " + u));
        report.Add($"effects (floor {Coverage.FloorFor(count, FloorRate)}: 1% of {count}, at least {Coverage.Floor}):");
        report.AddRange(effects.Select(kv => $"  {kv.Key,-42} {kv.Value.Count,6} / {count}  ({Coverage.Rate(kv.Value.Count, count)})"
            + (kv.Value.Count < Coverage.FloorFor(count, FloorRate) && BelowTheSoakFloor.ContainsKey(kv.Key) ? "  declared below the floor" : "")));
        report.AddRange(failures.Select(f => "  FAIL " + f));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, profile.Report), string.Join("\n", report) + "\n");

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
    internal static List<string> RateFailures(IReadOnlyDictionary<string, int> counts, int count, IReadOnlyDictionary<string, string>? belowTheFloor = null, IReadOnlyDictionary<string, string>? rareInSample = null)
    {
        belowTheFloor ??= BelowTheSoakFloor;
        var failures = new List<string>();
        var floor = Coverage.FloorFor(count, FloorRate);
        foreach (var (d, n) in counts)
        {
            var sampleRare = count < SoakExecutions && (RareInSample.ContainsKey(d) || rareInSample?.ContainsKey(d) == true);
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
    internal static readonly Dictionary<string, string> BelowTheSoakFloor = new(StringComparer.Ordinal)
    {
        // P7-11, put to the reviewer in the phase-7 report: 63 of 10,000 in the baseline soak (0.63%).
        // A follower must hold entries past the snapshot's index that conflict with it: diverged under
        // one leader, then sent the snapshot of another, which most followers that fall behind never
        // are (their logs simply end before the snapshot). Exercised directly by
        // InstallSnapshotTests.AFollowerWhoseLogDisagreesWithTheSnapshotDiscardsIt.
        ["install-discarded-the-suffix"] = "a follower holding a conflicting suffix past the snapshot: 0.63% of the baseline soak",

        // P8-07: a correct ReadIndex answers after another node's later election only when its round was
        // acknowledged before that election and the answer released after it: the window between a
        // quorum's last acknowledgement and the old leader learning of the new term. 1 of the baseline
        // sample's 300, 0 of the membership sample's. Its soak counts are P8-09's measurement.
        ["read-answered-after-a-later-term-began"] = "an old leader's read released after a later election, its round acknowledged before it: 1 of the baseline sample",

        // P8-09: 174 of the baseline soak's 10,000 (1.74%, over the floor) and 32 of the membership
        // soak's (0.32%): a commit, a lost answer, a compaction past the entry and the retry reaching a
        // node that restored, within one client's retries, on a workload committing about two thirds of
        // the baseline's entries. Exercised directly by SessionCompactionTests (install and restart).
        ["retry-deduplicated-by-a-restored-table"] = "a retry answered from a table a snapshot carried: 0.32% of the membership soak, 1.74% of the baseline",

        // P11-03: one append in flight per follower (P11-02) made it rarer. A follower keeps its suffix
        // at an install when it already holds entries past the snapshot's index; before the fix the
        // resends' many chains of appends crossed the snapshot's chunks and often delivered such
        // entries, and now a follower being sent a snapshot gets none meanwhile. On the same 1,000
        // seeds: 74 before and 28 after in the baseline soak (2.8%, over the floor), 26 before and 7
        // after in the membership soak (0.7%, under it). Exercised directly by
        // InstallSnapshotTests.AFollowerHoldingTheSnapshotsLastEntryKeepsTheEntriesAfterIt. A
        // consequence of the fix, which made a rare path rarer, not a property of the generator
        // (phase 11's acceptance): a change to how the leader sends is what to look at if it moves.
        ["install-kept-the-suffix"] = "an install over a suffix that agrees with the snapshot: 0.7% of the membership soak's first 1,000 since P11-02, 2.8% of the baseline's",

        // P12-08, the control-margin audit: 108 of the membership soak's 10,000 (1.08%) and 163 of the
        // baseline's (1.63%), over the floor of 100 but within the audit's factor of 1.5 of it in the
        // membership soak, where a change to the workload could take it under without any defect. A
        // crash must find two or more writes in flight on a disk that loses a reordered subset, and
        // keep a later one. Exercised directly by CoverageTests.TheThreeEventsPhaseOneNeverProducedAreReachable
        // (a slow disk makes writes overlap) and EntryLogTests.ATornEntryFollowedByAnEntryWhoseCutWasLostRecoversTheEntry.
        ["writes-completed-out-of-order-at-crash"] = "a crash keeping a later write and losing an earlier one: 1.08% of the membership soak, 1.63% of the baseline",
    };

    /// <summary>
    /// Effects allowed below the floor in the 300-execution sample only, each with its reason; the soak
    /// (10,000) must still clear the floor for them.
    /// </summary>
    internal static readonly Dictionary<string, string> RareInSample = new(StringComparer.Ordinal)
    {
        // P8-02: 2 of the baseline sample's 300 once sessions changed every execution (3 at phase 7);
        // declared below the soak floor already (63 of 10,000 at P7-11), and rare in the membership
        // sample since phase 7. The 10,000 must still clear the absolute minimum.
        ["install-discarded-the-suffix"] = "an install over a conflicting suffix: 2 of the baseline sample with sessions",

        // P8-07: see its entry below the soak floor; 1 of the baseline sample, 0 of the membership sample.
        ["read-answered-after-a-later-term-began"] = "a read released after a later election: 1 of the baseline sample, 0 of the membership sample",

        // P11-02: 4 of the baseline sample's 300 before one append in flight per follower, 2 after (the
        // sample commits as many entries either way, 132 per execution); declared below the soak floor
        // since P8-09 (174 of the baseline soak's 10,000), which must still clear the absolute minimum.
        // A composite of a commit, a lost answer, a compaction past the entry and a retry reaching a
        // node that restored: rare at 300, and the fix moved when answers go.
        ["retry-deduplicated-by-a-restored-table"] = "a retry answered from a restored table: 4 of the baseline sample before P11-02, 2 after",
    };

    /// <summary>Effects at or above 95%, each with the reason the other case is rare.</summary>
    internal static readonly Dictionary<string, string> AlwaysOn = new(StringComparer.Ordinal)
    {
        // P7-11: the soaks compact every 20 applied entries and an execution commits about 150 (100 with
        // membership changes), so an execution without a compaction is one that committed fewer than 20.
        ["log-compacted"] = "compaction every 20 applied entries against about 150 committed per execution",

        // P7-11: 286 of the baseline sample's 300 (95.3%), 9,431 of its 10,000 soak (94.3%): nearly every
        // execution crashes or partitions some node for longer than 20 committed entries take, and it
        // catches up by installing a snapshot. The rare case is an execution whose faults all fall on
        // the leader or end before 20 entries pass.
        ["snapshot-installed-by-a-follower"] = "a node left behind by more than 20 entries in nearly every execution",
    };
}
