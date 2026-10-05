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
        "crash-in-a-joint-configuration", "change-still-joint-at-the-end", CompactionCoverage.Joint,
    ];

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    /// <summary>One generated execution with membership changes, with <see cref="Cluster.Options"/> unless <paramref name="options"/> is given.</summary>
    internal static (Simulator Sim, ElectionHistory History, FaultSchedule Schedule) Run(int seed, RaftOptions? options = null)
    {
        var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = SoakConfig.FaultsUntil, Nodes = Universe });
        var (sim, h) = Cluster.Run((ulong)seed, SoakConfig.Duration, schedule, SoakConfig.Clients, new MembershipWorkload(SoakConfig.Think), Spares, options);
        return (sim, h, schedule);
    }

    /// <summary>
    /// P6-07: each removed server's window, from the commit in fact of a configuration that leaves it
    /// out (while an earlier one named it) to the commit of one that names it again, or the end.
    /// </summary>
    internal static List<(NodeId Server, long From, long Until)> Removals(IReadOnlyList<Observation> observations, ElectionHistory h, LogAnalysis log)
    {
        var committed = log.Commits
            .Select(c => (Config: log.CommandOf(c.Ghost) is { } cmd && Configuration.IsInternal(cmd) ? Configuration.Decode(cmd) : null, Time: observations[(int)c.Seq - 1].Time))
            .Where(c => c.Config is { IsJoint: false })
            .ToList();
        var windows = new List<(NodeId, long, long)>();
        var previous = h.Initial;
        for (var k = 0; k < committed.Count; k++)
        {
            foreach (var server in previous.Members.Where(m => !committed[k].Config!.Members.Contains(m)))
            {
                var back = committed.Skip(k + 1).Where(c => c.Config!.Members.Contains(server)).Select(c => c.Time).DefaultIfEmpty(long.MaxValue).First();
                windows.Add((server, committed[k].Time, back));
            }

            previous = committed[k].Config!;
        }

        return windows;
    }

    /// <summary>
    /// P6-07: a removed server's vote request that a server with a live leader then adopted: the
    /// server's next term-and-vote record after the delivery holds that request's term. With the
    /// disruption rule, a server that led or heard the leader within the minimum election timeout
    /// ignores the request and adopts nothing.
    /// Returns the candidacies (requests sent in a removal window) and the adoptions.
    /// </summary>
    /// <remarks>
    /// The minimum election timeout is the receiver's: a node with a skewed clock (P4-07) measures it on
    /// its own clock, so a clock 20% fast waits 150 of its units, 125 of the simulation's (P6-14 found the
    /// measure using the simulation's time, which counted that node's correct adoption as a disruption).
    /// </remarks>
    internal static (int Candidacies, int Adoptions) Disruption(ElectionHistory h, List<(NodeId Server, long From, long Until)> removals, FaultSchedule schedule)
    {
        bool Removed(NodeId n, long t) => removals.Any(r => r.Server == n && r.From <= t && t < r.Until);
        var candidacies = h.Sends.Count(s => s.Message is RequestVote && Removed(s.From, s.Time));
        var adoptions = 0;
        foreach (var d in h.Deliveries.Where(d => d.Message is RequestVote && Removed(d.From, d.Time)))
        {
            var next = h.Intended.Where(i => i.Node == d.To && i.Seq > d.Seq).OrderBy(i => i.Seq).FirstOrDefault();
            var before = h.Intended.Where(i => i.Node == d.To && i.Seq < d.Seq).OrderBy(i => i.Seq).LastOrDefault();
            var term = before?.State.Term ?? new Term(0);
            var skew = schedule.Faults.OfType<Skew>().LastOrDefault(k => k.Node == d.To && k.At <= d.Time);
            bool Within(long since) => (d.Time - since) * (skew?.Numerator ?? 1) < Cluster.Options.ElectionTimeoutMin * (skew?.Denominator ?? 1);

            // A disruption is an adoption by a server with a live leader: it led in its term, or heard
            // that term's leader, within the minimum election timeout. Without one, adopting a higher
            // term is how the cluster recovers, and the rule allows it.
            // A crash between the two forgets the leader: the restarted server has heard no one.
            var restarted = h.CrashSeqs.Where(c => c.Node == d.To && c.Seq < d.Seq).Select(c => c.Seq).DefaultIfEmpty(0).Max();
            var live = h.Sends.Any(s => s.From == d.To && s.Message is AppendEntries && s.Message.Term == term && Within(s.Time) && s.Seq > restarted && s.Seq < d.Seq)
                || h.Deliveries.Any(x => x.To == d.To && x.Message is AppendEntries && x.Message.Term == term && Within(x.Time) && x.Seq > restarted && x.Seq < d.Seq);
            // The record must follow from this delivery: a later delivery of a retried request, once the
            // leader is no longer live, is that delivery's adoption, judged on its own.
            var caused = next is not null && !h.Deliveries.Any(x => x.To == d.To && x.Seq > d.Seq && x.Seq < next.Seq);
            if (live && caused && next is not null && next.State.Term == d.Message.Term && next.State.VotedFor != d.To && !Removed(d.To, d.Time))
            {
                adoptions++;
            }
        }

        return (candidacies, adoptions);
    }

    /// <summary>
    /// P6-07: removed servers keep standing, and the disruption rule keeps the remaining servers from
    /// adopting their terms. Over 200 executions with removals, with the rule no server with a live
    /// leader adopts a removed server's term. The same seeds without the rule show adoptions, so the
    /// measure can see one; they are other executions, not the same ones replayed (the rule changes
    /// who leads, and so what is removed and when), and are counted on their own. Vacuity risk: a
    /// removed server that is crashed or partitioned never stands; guarded by a floor on executions
    /// in which a removed server stood. Sabotage S-disrupt-4.
    /// </summary>
    [Fact]
    public void RemovedServersDoNotDisruptTheNewConfiguration()
    {
        var count = Env("RAFT_MEMBERSHIP_DISRUPTION_COUNT", 200);
        var off = Cluster.Options with { DisruptionRule = false };
        int standing = 0, adoptions = 0, standingOff = 0, adoptionsOff = 0, disruptedOff = 0;
        long candidacies = 0;
        for (var seed = 1; seed <= count; seed++)
        {
            var (c, a) = Measure(seed, null);
            candidacies += c;
            adoptions += a;
            standing += c > 0 ? 1 : 0;

            var (cOff, aOff) = Measure(seed, off);
            adoptionsOff += aOff;
            standingOff += cOff > 0 ? 1 : 0;
            disruptedOff += aOff > 0 ? 1 : 0;
        }

        var line = $"{count} executions: with the rule a removed server stood in {standing} ({candidacies} requests) and {adoptions} of its terms were adopted; without it, a removed server stood in {standingOff} and its terms were adopted {adoptionsOff} times, in {disruptedOff} of those executions";
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "disruption-report.txt"), line + "\n");
        Assert.True(standing >= Coverage.FloorFor(count), "a removed server stood in too few executions: " + line);
        Assert.True(adoptions == 0, "a removed server's term was adopted: " + line);
        Assert.True(adoptionsOff > 0, "without the disruption rule nothing was adopted either: the measure cannot see a disruption: " + line);
    }

    private static (int Candidacies, int Adoptions) Measure(int seed, RaftOptions? options)
    {
        var (sim, h, schedule) = Run(seed, options);
        var observations = sim.Observations.ToList();
        var log = new LogAnalysis(LogHistory.FromObservations(observations, h, Cluster.Nodes));
        return Disruption(h, Removals(observations, h, log), schedule);
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

    /// <summary>
    /// The membership soak (P6-12): its own profile of the soak's run, beside the baseline soak, not
    /// inside it. The suite runs this 300-execution sample; the `soak-membership` CI job runs 10,000
    /// (RAFT_MEMBERSHIP_COUNT). Every invariant, linearizability with its own known limits
    /// (ci/known-limits-membership.txt), the shared effects and history contents with their floors, and
    /// the membership dimensions with theirs (completed changes among them). Sabotages S-soak-7, S-kl-4.
    /// </summary>
    internal static readonly SoakProfile Profile = new(
        "membership-report.txt", ", with membership changes", Spares, () => new MembershipWorkload(SoakConfig.Think), KnownLimits.MembershipFileName, Dimensions,
        (observations, h, log, schedule) => Effects(observations, h, log, schedule),
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // P6-12, put to the reviewer in the phase-6 report: two log writes in flight on one node when
            // it crashes, with a reordered loss keeping the later. 139 of 10,000 in the membership soak
            // (1.39%, over the floor) and 2 of its 300-execution sample: the membership workload commits
            // 101 entries per execution against the baseline's 164, so writes overlap less often. The
            // crash-mode tests exercise it directly (EntryLogTests, TermVoteLogTests).
            ["writes-completed-out-of-order-at-crash"] = "two writes in flight at a crash: 1.39% of the membership soak, 2 of its sample",

            // P7-11: 0 of the membership sample's 300, 3 of the baseline's: the membership workload commits
            // about 100 entries per execution against 164, so fewer followers diverge past a snapshot.
            ["install-discarded-the-suffix"] = "a follower holding a conflicting suffix past the snapshot: 0 of the membership sample",
        });

    /// <summary>
    /// P6-12: the membership soak is judged by its own known limits, named entry by entry, never by the
    /// baseline soak's: the two soaks run the same seeds on different workloads, so a baseline entry
    /// would match nothing here, or a membership execution with its seed. Sabotage S-kl-4.
    /// </summary>
    [Fact]
    public void TheMembershipSoakIsJudgedByItsOwnRecordedKnownLimits()
    {
        // The file itself, not only its entries: at P7-11 both soaks' files were empty for a commit,
        // and entries alone could not tell them apart (S-kl-4 survived).
        Assert.Equal(KnownLimits.MembershipFileName, Profile.LimitsFile);
        Assert.Equal(RecordedMembershipLimits, KnownLimits.RecordedIn(Profile.LimitsFile).Select(e => $"{e.Id} {e.Seed} {e.Key} {e.Digest}"));
    }

    /// <summary>The reviewed set (ci/known-limits-membership.txt). KL-2 removed at P7-11: its history no longer occurs.</summary>
    private static readonly string[] RecordedMembershipLimits = [];

    [Fact]
    public void TheMembershipSampleHoldsEveryInvariant() =>
        Soak.Run(Profile, Env("RAFT_MEMBERSHIP_FIRST", 1), Env("RAFT_MEMBERSHIP_COUNT", 300));
}
