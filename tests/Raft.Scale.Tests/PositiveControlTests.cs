using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raft.Core;
using Raft.Checker;
using Raft.Core.Tests;
using Raft.Simulation;
using Xunit;

namespace Raft.Scale.Tests;

/// <summary>
/// P3-07: the fsynced-then-lost positive control (spec §5). A disk that loses an fsynced vote is a
/// disk that lies, which Raft cannot survive; the checkers must say so. Placed by state, in the shape
/// of spec §8's two-leader example: n2's first durable record (a vote, answered) is lost at a crash
/// right after it, n2 is back 20 ticks later, and n1 and n3 cannot hear each other, so a second
/// candidate in the same term can reach n2. Vacuity risk: a control that is red for a reason other
/// than the lost vote proves nothing about the checkers; guarded by the same seeds with the loss
/// removed (a crash that loses only unsynced writes) being green, and by the failures naming n2.
/// Sabotages S-pos-1, S-pos-2.
/// </summary>
public sealed class PositiveControlTests
{
    private const int Runs = 200;
    private const long Duration = 3_000;
    private static readonly NodeId N1 = new(1), N2 = new(2), N3 = new(3);

    private static Fault[] Construction(DiskLoss loss) =>
        [new CrashAfterWrite(0, N2, loss, 20), new Partition(0, N1, N3), new Partition(0, N3, N1)];

    private static (InvariantResult Safety, InvariantResult Votes, InvariantResult Terms) Check(FaultSchedule s, ulong seed)
    {
        var (_, h) = Cluster.Run(seed, Duration, s, clients: 3, workload: new RaftWorkload(int.MaxValue));
        return (ElectionInvariants.ElectionSafety(h, Cluster.Options.ElectionTimeoutMin), ElectionInvariants.VoteUniqueness(h), ElectionInvariants.TermMonotonicity(h));
    }

    [Fact]
    public void LosingAnFsyncedVoteTurnsTheInvariantsRedAndTheSameCrashWithoutTheLossDoesNot()
    {
        int safety = 0, votesNamingN2 = 0, termsNamingN2 = 0, timePlaced = 0;
        for (var seed = 1UL; seed <= Runs; seed++)
        {
            var red = Check(new FaultSchedule(Construction(DiskLoss.LoseSynced)), seed);
            safety += red.Safety.Holds ? 0 : 1;
            votesNamingN2 += red.Votes.Violations.Any(v => v.StartsWith("n2 voted for", StringComparison.Ordinal)) ? 1 : 0;
            termsNamingN2 += red.Terms.Violations.Any(v => v.StartsWith("n2 at", StringComparison.Ordinal)) ? 1 : 0;

            var twin = Check(new FaultSchedule(Construction(DiskLoss.Pending)), seed);
            Assert.True(twin.Safety.Holds && twin.Votes.Holds && twin.Terms.Holds,
                $"seed {seed}: red without the lost fsync: {string.Join("; ", twin.Safety.Violations.Concat(twin.Votes.Violations).Concat(twin.Terms.Violations))}");

            // Time-placed, for the record (P3-07's prediction): the same loss at a fixed time in the first election.
            var at = 150 + ((long)seed * 37 % 300);
            var timed = Check(new FaultSchedule([new Crash(at, N2, DiskLoss.LoseSynced), new Restart(at + 20, N2), new Partition(0, N1, N3), new Partition(0, N3, N1)]), seed);
            timePlaced += timed.Safety.Holds && timed.Votes.Holds ? 0 : 1;
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "positive-control.txt"), string.Join("\n",
            $"{Runs} runs, n2 loses its first fsynced record 20 ticks before restarting, n1 and n3 unable to hear each other",
            $"state-placed: election safety red {safety}, vote uniqueness red naming n2 {votesNamingN2}, term monotonicity red naming n2 {termsNamingN2}",
            $"time-placed (a crash at 150..449): election safety or vote uniqueness red {timePlaced}",
            "same seeds, crash losing only unsynced writes: green in every run") + "\n");

        Assert.True(termsNamingN2 == Runs, $"term monotonicity named n2 in only {termsNamingN2} of {Runs} runs: a lost term went unseen");
        Assert.True(votesNamingN2 >= Coverage.FloorFor(Runs), $"vote uniqueness named n2 in only {votesNamingN2} of {Runs} runs ({Coverage.Rate(votesNamingN2, Runs)}): the lying disk is barely visible to the checker the spec names");
    }

    /// <summary>
    /// P4-08: the control reaches the log. n3 is isolated for the whole run, so every commit needs n1
    /// and n2; clients write throughout. Armed at 1000, n2 crashes right after its next write completes
    /// and the barrier releases what it held (its acknowledgement), and the disk loses that write: an
    /// fsynced log record of an entry the leader could count. Back 20 ticks later. Invariant 6 must name
    /// n2's disk in every run where n2 followed; Leader Completeness is counted. The twin loses only
    /// unsynced writes, and is green. Sabotages S-pos-4, S-pos-5.
    /// </summary>
    [Fact]
    public void LosingAnFsyncedLogRecordTurnsDurabilityRedAndTheSameCrashWithoutTheLossDoesNot()
    {
        const long duration = 3_000;
        FaultSchedule Schedule(DiskLoss loss) => new([new Isolate(0, N3, duration), new CrashAfterWrite(1_000, N2, loss, 20)]);
        (IReadOnlyList<InvariantResult> Results, bool N2Led) Run(DiskLoss loss, ulong seed)
        {
            var (sim, h) = Cluster.Run(seed, duration, Schedule(loss), clients: 3, workload: new RaftWorkload(int.MaxValue));
            var crash = sim.Observations.OfType<CrashObservation>().First(c => c.Node == N2).Time;
            var leader = h.Elections().Where(e => e.Value <= crash).MaxBy(e => e.Key.Term.Value).Key.Candidate;
            return (Cluster.CheckLog(sim, h), leader == N2);
        }

        int durableNamingN2 = 0, durableRed = 0, completenessRed = 0, n2Led = 0, redWhileFollowing = 0;
        for (var seed = 1UL; seed <= Runs; seed++)
        {
            var (results, led) = Run(DiskLoss.LoseSynced, seed);
            var red = results.ToDictionary(r => r.Invariant, StringComparer.Ordinal);
            var named = red["committed-durable"].Violations.Any(v => v.EndsWith("after n2's disk changed", StringComparison.Ordinal));
            durableRed += red["committed-durable"].Holds ? 0 : 1;
            durableNamingN2 += named ? 1 : 0;
            completenessRed += red["leader-completeness"].Holds ? 0 : 1;
            n2Led += led ? 1 : 0;
            redWhileFollowing += !led && named ? 1 : 0;

            var twin = Run(DiskLoss.Pending, seed).Results.Where(r => !r.Holds).ToList();
            Assert.True(twin.Count == 0, $"seed {seed}: red without the lost fsync: {string.Join("; ", twin.Select(r => r.Invariant + ": " + r.Violations[0]))}");
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "positive-control-log.txt"), string.Join("\n",
            $"{Runs} runs, n3 isolated, n2 loses its next fsynced write after 1000 and is back 20 ticks later; 3 clients",
            $"committed-durable red {durableRed}, naming n2's disk {durableNamingN2}; leader-completeness red {completenessRed}",
            $"n2 led at the crash in {n2Led}; red naming n2 in {redWhileFollowing} of the {Runs - n2Led} where it followed",
            "same seeds, crash losing only unsynced writes: green in every run") + "\n");
        // Where n2 follows, its write completes after the leader's, so the lost record is of an entry
        // committed in fact; where n2 leads, its own write is lost before n1's copy completes, and the
        // entry was never committed (measured: red in none of those runs).
        Assert.True(Runs - n2Led >= Coverage.FloorFor(Runs), $"n2 followed in only {Runs - n2Led} of {Runs} runs: the control was barely placed");
        Assert.True(redWhileFollowing == Runs - n2Led, $"invariant 6 named n2's disk in only {redWhileFollowing} of the {Runs - n2Led} runs where it followed: a lost log record went unseen");
    }

    /// <summary>
    /// P7-10: the positive control over the soak's generated executions. Nodes that compact past
    /// their commit index (<see cref="RaftOptions.CompactPastCommit"/>), the soak's faults, clients and
    /// threshold: the log invariants and the agreement check must catch them, and the real node, the
    /// soak's own sample, in none. The test stops once the control is caught 3 times (the floor), in at
    /// most 300 executions; P7-10's full measurement over all 300 (120 caught) is in the breakdown. Run
    /// to the end, it took 10 minutes under S-dur-2 in every Scale harness entry's neighbours, and put
    /// its shard over the ceiling on GitHub (run 37278405824).
    /// </summary>
    [Fact]
    public void ACompactionPastTheCommitIndexIsCaughtInTheGeneratedSample()
    {
        int red = 0, agreementRed = 0, threw = 0;
        var byInvariant = new Dictionary<string, int>(StringComparer.Ordinal);
        const int Executions = 300;
        var run = 0;
        for (var seed = 1; seed <= Executions && red < 3; seed++)
        {
            run++;
            var probe = new AgreementProbe();
            var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = SoakConfig.FaultsUntil });
            Simulator sim;
            ElectionHistory h;
            try
            {
                (sim, h) = Cluster.Run((ulong)seed, SoakConfig.Duration, schedule, SoakConfig.Clients, new SessionWorkload(new RaftWorkload(int.MaxValue, retry: true, think: SoakConfig.Think), Cluster.Nodes),
                    node: ctx => new RaftNode(ctx, SoakConfig.Options with { CompactPastCommit = true }, probe.For(ctx.Id)));
            }
            catch (ArgumentOutOfRangeException)
            {
                threw++;
                red++;
                continue;
            }

            var observations = sim.Observations.ToList();
            var log = new LogAnalysis(LogHistory.FromObservations(observations, h, Cluster.Nodes));
            var failed = LogAnalysis.Names.Where(n => !log.Result(n).Holds).ToList();
            foreach (var f in failed)
            {
                byInvariant[f] = byInvariant.GetValueOrDefault(f) + 1;
            }

            var disagreed = probe.Check(observations, log).Failures.Count > 0;
            agreementRed += disagreed ? 1 : 0;
            red += failed.Count > 0 || disagreed ? 1 : 0;
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "compaction-control.txt"), string.Join("\n",
            $"{run} executions (stopping at 3 caught, at most {Executions}), every node compacting past its commit index every {SoakConfig.SnapshotThreshold} applied entries",
            $"caught: {red} of {run}; by invariant: {string.Join(", ", byInvariant.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}"))}; by agreement {agreementRed}; a node threw {threw}") + "\n");
        Assert.True(red >= 3, $"the control was caught in only {red} of {run} executions");
    }

    /// <summary>One generated execution of the soak's configuration, with a P8-08 control on or off: whether linearizability rejected it, and whether the agreement check disagreed.</summary>
    private static (bool Caught, bool AgreementRed) Control(string? control, int seed)
    {
        var probe = new AgreementProbe(deduplicate: control != "no-deduplication");
        var options = control == "reads-without-a-quorum" ? SoakConfig.Options with { ReadsWithoutQuorum = true } : SoakConfig.Options;
        var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = SoakConfig.FaultsUntil });
        var (sim, h) = Cluster.Run((ulong)seed, SoakConfig.Duration, schedule, SoakConfig.Clients, new SessionWorkload(new RaftWorkload(int.MaxValue, retry: true, think: SoakConfig.Think), Cluster.Nodes),
            node: ctx => new RaftNode(ctx, options, probe.For(ctx.Id)));
        var caught = WglChecker.Check(ClientHistory.From(sim.ClientLog).History, SoakConfig.CheckerBudget).Verdict == Verdict.NotLinearizable;
        var observations = sim.Observations.ToList();
        return (caught, probe.Check(observations, new LogAnalysis(LogHistory.FromObservations(observations, h, Cluster.Nodes))).Failures.Count > 0);
    }

    /// <summary>
    /// P8-08: the deduplication control (a state machine applying every session command) over the
    /// soak's generated executions, caught by linearizability; the test stops once it is caught 3
    /// times (the floor; phase 7's acceptance), in at most 300 executions. With RAFT_CONTROL_FULL=1 it
    /// runs every execution up to RAFT_CONTROL_MAX (300 by default), the measurement the breakdown
    /// records, for either control (RAFT_CONTROL names it).
    /// </summary>
    [Fact]
    public void TheDeduplicationControlIsCaughtByLinearizabilityInTheGeneratedSample()
    {
        var full = Environment.GetEnvironmentVariable("RAFT_CONTROL_FULL") == "1";
        var control = full ? Environment.GetEnvironmentVariable("RAFT_CONTROL") ?? "no-deduplication" : "no-deduplication";
        var executions = int.TryParse(Environment.GetEnvironmentVariable("RAFT_CONTROL_MAX"), out var max) ? max : 300;
        var caughtSeeds = new List<int>();
        int run = 0, agreementRed = 0;
        for (var seed = 1; seed <= executions && (full || caughtSeeds.Count < 3); seed++)
        {
            run++;
            var (caught, disagreed) = Control(control, seed);
            if (caught)
            {
                caughtSeeds.Add(seed);
            }

            agreementRed += disagreed ? 1 : 0;
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"control-{control}.txt"),
            $"{control}: {run} executions ({(full ? "all" : "stopping at 3 caught")}, at most {executions}); caught by linearizability in {caughtSeeds.Count} (seeds {string.Join(", ", caughtSeeds)}); agreement red in {agreementRed}\n");
        Assert.True(caughtSeeds.Count >= 3, $"the {control} control was caught by linearizability in only {caughtSeeds.Count} of {run} executions");
    }

    /// <summary>
    /// P8-08: the read control (leaders answering reads from their own state at once) is caught in 7
    /// of 3,000 generated executions (0.23%; 1 of the first 300), too rare for a sample that stops at
    /// the floor: the third catch is at seed 1,418, and a sample running that far under a sabotage
    /// that slows executions is how phase 7's shard 10 went over its ceiling. So, as the budget tests
    /// name their seeds, it runs the first three executions that measurement found it caught in. Each
    /// must be rejected with the control on, and accepted with the real node on the same seed.
    /// </summary>
    [Theory]
    [InlineData(33)]
    [InlineData(1367)]
    [InlineData(1418)]
    public void TheReadControlIsCaughtByLinearizabilityWhereTheMeasurementFoundIt(int seed)
    {
        Assert.True(Control("reads-without-a-quorum", seed).Caught, $"seed {seed}: the read control's history was accepted");
        Assert.False(Control(null, seed).Caught, $"seed {seed}: the real node's history was rejected");
    }
}
