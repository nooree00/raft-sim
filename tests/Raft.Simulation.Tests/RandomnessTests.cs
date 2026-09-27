using System;
using System.Linq;
using Raft.Core;
using Raft.Simulation;
using Xunit;

namespace Raft.Simulation.Tests;

/// <summary>
/// P1-03. Vacuity risk: a stream compared only with itself in one process passes even if it depends
/// on per-process state (a randomized hash); the committed golden values cannot be reproduced by
/// such a stream. Sabotages S-rng-1 (per-process hash), S-rng-2 (purposes share a stream).
/// </summary>
public sealed class RandomnessTests
{
    private static double Unit(ulong x) => (x >> 11) * (1.0 / (1UL << 53));

    [Fact]
    public void GoldenValuesAreStableAcrossProcessesAndVersions()
    {
        var s = new Streams(42);
        var net = s.For("net");
        ulong[] first = [net.NextUInt64(), net.NextUInt64(), net.NextUInt64()];

        Assert.Equal(Golden.NetFirstThree, first);
        Assert.Equal(Golden.DelayAt7, s.At("delay:n1->n2", 7));
        Assert.Equal(Golden.FnvHelloWorld, Mix.Hash("hello world"));
    }

    [Fact]
    public void TheSamePurposeGivesTheSameStream()
    {
        var a = new Streams(7).For("election:n1");
        var b = new Streams(7).For("election:n1");

        Assert.Equal(Enumerable.Range(0, 100).Select(_ => a.NextUInt64()), Enumerable.Range(0, 100).Select(_ => b.NextUInt64()));
    }

    [Fact]
    public void DrawingFromOnePurposeDoesNotShiftAnother()
    {
        var s = new Streams(7);
        var fresh = s.For("disk:n2");
        var expected = Enumerable.Range(0, 10).Select(_ => fresh.NextUInt64()).ToArray();

        var busy = s.For("net:n1");
        for (var i = 0; i < 1000; i++)
        {
            busy.NextUInt64();
        }

        var after = s.For("disk:n2");
        Assert.Equal(expected, Enumerable.Range(0, 10).Select(_ => after.NextUInt64()).ToArray());
    }

    [Theory]
    [InlineData("delay:n1->n2", "delay:n1->n3")]
    [InlineData("a", "b")]
    [InlineData("election:n1", "election:n2")]
    public void PurposesDifferingInOneCharacterAreUncorrelated(string p1, string p2)
    {
        var s = new Streams(1);
        var a = s.For(p1);
        var b = s.For(p2);
        var xs = Enumerable.Range(0, 1000).Select(_ => Unit(a.NextUInt64())).ToArray();
        var ys = Enumerable.Range(0, 1000).Select(_ => Unit(b.NextUInt64())).ToArray();
        var zs = Enumerable.Range(0, 1000).Select(i => Unit(s.At(p1, (ulong)i))).ToArray();
        var ws = Enumerable.Range(0, 1000).Select(i => Unit(s.At(p2, (ulong)i))).ToArray();

        Assert.True(Math.Abs(Correlation(xs, ys)) < 0.1, $"streams: r = {Correlation(xs, ys):F3}");
        Assert.True(Math.Abs(Correlation(zs, ws)) < 0.1, $"indexed draws: r = {Correlation(zs, ws):F3}");
    }

    [Fact]
    public void DrawsAreRoughlyUniform()
    {
        var r = new Streams(3).For("uniform");
        var buckets = new int[16];
        const int N = 16_000;
        for (var i = 0; i < N; i++)
        {
            buckets[r.NextLong(16)]++;
        }

        var expected = N / 16.0;
        var chi2 = buckets.Sum(b => (b - expected) * (b - expected) / expected);
        Assert.True(chi2 < 37.7, $"chi-square {chi2:F1} over 15 degrees of freedom (p = 0.001 bound 37.7)");
    }

    private static double Correlation(double[] x, double[] y)
    {
        var mx = x.Average();
        var my = y.Average();
        var cov = x.Zip(y, (a, b) => (a - mx) * (b - my)).Sum();
        var vx = x.Sum(a => (a - mx) * (a - mx));
        var vy = y.Sum(b => (b - my) * (b - my));
        return cov / Math.Sqrt(vx * vy);
    }
}

/// <summary>Committed once from a first run; a change here is a reproducibility break, not a fix.</summary>
internal static class Golden
{
    public static readonly ulong[] NetFirstThree = [9556732507261916718UL, 10341377289003547014UL, 1931008167239349763UL];
    public const ulong DelayAt7 = 4012543761930424559UL;
    public const ulong FnvHelloWorld = 0x779a65e7023cd2e7UL;
}
