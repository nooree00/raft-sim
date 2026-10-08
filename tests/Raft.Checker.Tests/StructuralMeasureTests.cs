using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Raft.Checker;
using Xunit;

namespace Raft.Checker.Tests;

/// <summary>
/// P11-07: does a structural quantity of a key's sub-history separate the histories the WGL checker
/// decides from those it cannot? The register row's prediction (phase 6): every undecided key's
/// width is at least 4 above the largest width of any key decided in under 1,000,000 states. Asked of
/// generated single-key histories (<see cref="StructuralMeasure"/>), since neither soak has an
/// undecided key. With RAFT_STRUCT_FULL=1 the measurement runs the whole grid, takes the narrowest
/// undecided ones to the long budget (<see cref="LongBudget"/>) and writes structural-measure.txt; by
/// default a small grid at 1,000,000, where "undecided" means at that budget only. Vacuity risk: a
/// grid whose histories are all decided, or all undecided, separates nothing and confirms any
/// threshold; guarded by requiring both classes. Sabotages S-struct-1 (three clients whatever the
/// shape) and S-struct-2 (the width without the indeterminate operations).
/// </summary>
public sealed class StructuralMeasureTests
{
    /// <summary>
    /// The longest search here, in place of the soak's 32,000,000 states (Raft.Scale.Tests'
    /// SoakConfig.CheckerBudget). The soak's budget is what its runner holds for the soak's keys, a few
    /// dozen operations each (7.2 to 12.5 GB measured, spec §6). The search's key spells every
    /// operation of the history, so a state of a 240-operation history is about three times larger:
    /// 32,000,000 of them were killed for memory on a 16-GB machine, twice, and 16,000,000 take about
    /// what the soak's budget takes on its own keys. The peak memory of each long search is recorded.
    /// </summary>
    private const long LongBudget = 16_000_000;

    /// <summary>The prediction's boundary for "decided": in under this many states.</summary>
    private const long Decided = 1_000_000;

    internal sealed record Row(StructuralMeasure.Shape Shape, int Seed, Verdict Verdict, long States, int Width, int LostWrites, int ReadChoices, bool Long = false, long PeakBytes = 0)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"c{Shape.Clients} lost{Shape.Lost} s{Seed}: {Verdict} {States} states{(Long ? $" (the long search, peak {PeakBytes / 1e9:F1} GB)" : "")}, width {Width}, lost writes {LostWrites}, read choices {ReadChoices}");
    }

    private static IEnumerable<(StructuralMeasure.Shape Shape, int Seed)> Grid(bool full)
    {
        int[] clients = full ? [8, 10, 12, 14, 16, 20] : [6, 20];
        double[] lost = full ? [0.1, 0.3, 0.6] : [0.1, 0.6];
        var seeds = full ? 3 : 1;
        foreach (var c in clients)
        {
            foreach (var l in lost)
            {
                for (var s = 1; s <= seeds; s++)
                {
                    yield return (new StructuralMeasure.Shape(c, 12, 0.5, l), s);
                }
            }
        }
    }

    /// <summary>
    /// Each history checked at the prediction's boundary; in the full grid, the ones undecided there
    /// again with the <see cref="LongBudget"/>, narrowest first, one at a time, until
    /// <see cref="LongConfirmed"/> stay undecided: two long searches at once were killed for memory, and
    /// each takes minutes. Progress is written as it goes (structural-measure-progress.txt), so a run
    /// killed for memory keeps what it measured.
    /// </summary>
    internal static List<Row> Measure(bool full)
    {
        var rows = new ConcurrentBag<Row>();
        Parallel.ForEach(Grid(full), new ParallelOptions { MaxDegreeOfParallelism = full ? Environment.ProcessorCount : 1 }, g =>
        {
            var h = StructuralMeasure.Generate(g.Shape, g.Seed);
            var r = WglChecker.Check(h, Decided);
            rows.Add(new Row(g.Shape, g.Seed, r.Verdict, r.StatesExplored, StructuralMeasure.Width(h), StructuralMeasure.LostWrites(h), StructuralMeasure.ReadChoices(h)));
        });
        var sorted = rows.OrderBy(r => r.Shape.Clients).ThenBy(r => r.Shape.Lost).ThenBy(r => r.Seed).ToList();
        if (!full)
        {
            return sorted;
        }

        var progress = Path.Combine(AppContext.BaseDirectory, "structural-measure-progress.txt");
        File.WriteAllLines(progress, sorted.Select(r => r.ToString()));
        var confirmed = 0;
        foreach (var narrow in sorted.Where(r => r.Verdict == Verdict.Undecided).OrderBy(r => r.Width).ToList())
        {
            if (confirmed == LongConfirmed)
            {
                break;
            }

            var r = WglChecker.Check(StructuralMeasure.Generate(narrow.Shape, narrow.Seed), LongBudget);
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var row = narrow with { Verdict = r.Verdict, States = r.StatesExplored, Long = true, PeakBytes = self.PeakWorkingSet64 };
            sorted[sorted.IndexOf(narrow)] = row;
            File.AppendAllLines(progress, [$"long search: {row}"]);
            confirmed += r.Verdict == Verdict.Undecided ? 1 : 0;
        }

        return sorted;
    }

    /// <summary>
    /// How many histories the full grid confirms undecided with the long budget, taking the ones
    /// undecided at 1,000,000 narrowest first: the narrowest are the ones the prediction turns on.
    /// </summary>
    private const int LongConfirmed = 5;

    /// <summary>
    /// The candidates against the checker. Every generated history is linearizable by construction,
    /// so the checker never rejects one; both classes occur; and the width does not separate them:
    /// some undecided history is narrower than some history decided in under 1,000,000 states, which
    /// refutes the phase-6 prediction (a margin of 4 the other way). Sabotage S-struct-1.
    /// </summary>
    [Fact]
    public void TheWidthDoesNotSeparateTheDecidedFromTheUndecided()
    {
        var full = Environment.GetEnvironmentVariable("RAFT_STRUCT_FULL") == "1";
        var rows = Measure(full);

        // Two classes by the 1,000,000-state check, the prediction's boundary: decided under it, and
        // not. The prediction is about keys the soak cannot decide, so it is read against the
        // histories the long search confirms undecided (in the full grid; by default, at 1,000,000).
        var decided = rows.Where(r => r.Verdict == Verdict.Linearizable && r.States < Decided).ToList();
        var notDecided = rows.Except(decided).ToList();
        var confirmed = full ? notDecided.Where(r => r.Long && r.Verdict == Verdict.Undecided).ToList() : notDecided;
        var need = full ? 20 : 1;

        string Range(IEnumerable<Row> rs, Func<Row, int> f) => rs.Any() ? $"{rs.Min(f)} to {rs.Max(f)}" : "none";
        string Candidate(string name, Func<Row, int> f)
        {
            // The best single threshold (at or above it, not decided) and how many histories it misplaces.
            var (errors, at) = rows.Select(f).Distinct().Select(t => (rows.Count(r => (f(r) >= t) != notDecided.Contains(r)), t)).Min();
            return $"{name}: decided {Range(decided, f)}; not decided {Range(notDecided, f)}; confirmed undecided {Range(confirmed, f)}; the best threshold, {at}, misplaces {errors} of {rows.Count}";
        }

        var report = new List<string>
        {
            $"{rows.Count} histories ({(full ? $"the full grid at {Decided:N0} states, the ones not decided again at {LongBudget:N0}, narrowest first until {LongConfirmed} stay undecided" : $"the small grid, at {Decided:N0} states")}): {decided.Count} decided in under {Decided:N0} states, {notDecided.Count} not; {rows.Count(r => r.Long)} taken to the long search, {confirmed.Count(r => r.Long)} undecided there and {rows.Count(r => r.Long && r.Verdict == Verdict.Linearizable)} decided past {Decided:N0}",
            Candidate("width", r => r.Width),
            Candidate("lost writes", r => r.LostWrites),
            Candidate("read choices", r => r.ReadChoices),
            Candidate("clients", r => r.Shape.Clients),
        };
        report.AddRange(rows.Select(r => r.ToString()));
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "structural-measure.txt"), report);

        Assert.DoesNotContain(rows, r => r.Verdict == Verdict.NotLinearizable);
        Assert.True(decided.Count >= need && notDecided.Count >= need, $"{decided.Count} decided and {notDecided.Count} not: both classes are needed (at least {need} each) before any threshold is read\n{string.Join("\n", report.Take(5))}");
        Assert.True(confirmed.Count > 0, $"no history confirmed undecided by the long search\n{string.Join("\n", report.Take(5))}");
        Assert.True(confirmed.Min(r => r.Width) < decided.Max(r => r.Width), $"every undecided history is wider than every decided one: the width separates them, against this measurement\n{string.Join("\n", report.Take(5))}");
    }

    /// <summary>
    /// The width counts an indeterminate operation as open to the end of the history: two operations
    /// that never overlap make a width of 2 when the first never responded. Sabotage S-struct-2.
    /// </summary>
    [Fact]
    public void TheWidthCountsAnIndeterminateOperationAsOpenToTheEnd()
    {
        Operation[] ops =
        [
            new(1, OpKind.Put, "k", 0, null, Value: "a"),
            new(2, OpKind.Get, "k", 10, 20, Output: "a"),
            new(3, OpKind.Get, "k", 30, 40, Output: "a"),
        ];

        Assert.Equal(2, StructuralMeasure.Width(ops));
    }

    /// <summary>
    /// The generator produces histories a correct store produces: small ones are linearizable by the
    /// brute-force oracle, and the WGL checker accepts them too.
    /// </summary>
    [Fact]
    public void SmallGeneratedHistoriesAreLinearizableByTheOracle()
    {
        for (var seed = 1; seed <= 200; seed++)
        {
            var h = StructuralMeasure.Generate(new StructuralMeasure.Shape(3, 3, 0.5, 0.4), seed);
            Assert.True(BruteForceOracle.IsLinearizable(h), $"seed {seed}: the oracle rejected a history the generator built from a linearizable store");
            Assert.True(WglChecker.Check(h).IsLinearizable, $"seed {seed}: the checker rejected it");
        }
    }
}
