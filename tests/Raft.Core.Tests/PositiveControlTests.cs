using System;
using System.IO;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Core.Tests;

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
        var (_, h) = Cluster.Run(seed, Duration, s);
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
        Assert.True(votesNamingN2 >= Coverage.Floor, $"vote uniqueness named n2 in only {votesNamingN2} of {Runs} runs: the lying disk is barely visible to the checker the spec names");
    }
}
