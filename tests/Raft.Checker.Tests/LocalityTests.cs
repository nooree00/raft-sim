using System;
using System.Collections.Generic;
using System.Linq;
using Raft.Checker;
using Xunit;

namespace Raft.Checker.Tests;

/// <summary>
/// Herlihy &amp; Wing, Theorem 1 (p. 470): H is linearizable iff for each object x, H|x is. The
/// oracle does not decompose by key, so the theorem is an external check on it: over random
/// two-key histories, its verdict on H must equal the conjunction of its verdicts on each H|x.
/// Vacuity risk: random histories that are all linearizable, or all not, make the equivalence
/// trivial — so both verdicts must occur a meaningful number of times. Sabotage S-hist-4.
/// </summary>
public sealed class LocalityTests
{
    private const int Histories = 400;

    [Fact]
    public void AHistoryIsLinearizableIffEachKeysSubhistoryIs()
    {
        var rng = new Random(20260927); // tests may use randomness; Raft.Core may not
        int yes = 0, no = 0;
        for (var n = 0; n < Histories; n++)
        {
            var h = RandomHistory(rng);
            var whole = BruteForceOracle.IsLinearizable(h);
            var perKey = h.GroupBy(o => o.Key).All(g => BruteForceOracle.IsLinearizable(g.ToList()));

            Assert.True(whole == perKey, $"history {n}: whole {whole}, per key {perKey}\n  " + string.Join("\n  ", h));
            if (whole)
            {
                yes++;
            }
            else
            {
                no++;
            }
        }

        Assert.True(yes >= Histories / 10 && no >= Histories / 10, $"verdicts too one-sided to test anything: {yes} linearizable, {no} not");
    }

    /// <summary>Up to 7 operations over keys x and y, overlapping intervals, plausible outputs.</summary>
    private static List<Operation> RandomHistory(Random rng)
    {
        var ops = new List<Operation>();
        var count = rng.Next(3, 8);
        var values = new[] { "1", "2", "3" };
        for (var i = 0; i < count; i++)
        {
            var key = rng.Next(2) == 0 ? "x" : "y";
            long invoke = rng.Next(0, 40);
            long? response = rng.Next(8) == 0 ? null : invoke + rng.Next(1, 15);
            var client = i; // one operation per client keeps every history well-formed
            ops.Add(rng.Next(3) switch
            {
                0 => new Operation(client, OpKind.Put, key, invoke, response, Value: values[rng.Next(3)]),
                1 => new Operation(client, OpKind.Get, key, invoke, response, Output: rng.Next(4) == 0 ? null : values[rng.Next(3)]),
                _ => new Operation(client, OpKind.CompareAndSwap, key, invoke, response,
                    Value: values[rng.Next(3)], Expected: rng.Next(3) == 0 ? null : values[rng.Next(3)], Output: rng.Next(2) == 0 ? "true" : "false"),
            });
        }

        return ops.OrderBy(o => o.Invoke).ToList();
    }
}
