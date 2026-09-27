using System;
using Raft.Core;

namespace Raft.Simulation;

/// <summary>
/// The simulator's only randomness (spec §7). Hand-written rather than System.Random, whose
/// algorithm is not a compatibility promise; purposes are hashed with a hand-written FNV-1a, never
/// string.GetHashCode, which is randomized per process. Every draw is a function of (seed, purpose)
/// or (seed, purpose, index), so removing one fault from a schedule does not shift any other draw.
/// </summary>
public sealed class Streams
{
    public Streams(ulong seed) => Seed = seed;

    public ulong Seed { get; }

    /// <summary>A stateful stream for one purpose, independent of every other purpose.</summary>
    public IRandomSource For(string purpose) => new Xoshiro256(Mix.Combine(Seed, Mix.Hash(purpose)));

    /// <summary>A single stateless draw: the <paramref name="index"/>-th value for a purpose.</summary>
    public ulong At(string purpose, ulong index) => Mix.SplitMix64(Mix.Combine(Mix.Combine(Seed, Mix.Hash(purpose)), index));
}

/// <summary>Hashing and mixing primitives, all stable across processes, machines and runtimes.</summary>
public static class Mix
{
    /// <summary>FNV-1a, 64-bit, over the UTF-8 bytes of <paramref name="text"/> (the standard definition).</summary>
    public static ulong Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var h = 0xcbf29ce484222325UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            h = (h ^ b) * 0x100000001b3UL;
        }

        return h;
    }

    /// <summary>The SplitMix64 finalizer: a bijective avalanche of one 64-bit value.</summary>
    public static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>Combines two values into one well-mixed value.</summary>
    public static ulong Combine(ulong a, ulong b) => SplitMix64(a ^ SplitMix64(b));
}

/// <summary>xoshiro256** (Blackman &amp; Vigna), seeded by expanding one value through SplitMix64.</summary>
public sealed class Xoshiro256 : IRandomSource
{
    private ulong _s0, _s1, _s2, _s3;

    public Xoshiro256(ulong seed)
    {
        _s0 = Mix.SplitMix64(seed);
        _s1 = Mix.SplitMix64(_s0);
        _s2 = Mix.SplitMix64(_s1);
        _s3 = Mix.SplitMix64(_s2);
    }

    public ulong NextUInt64()
    {
        var result = RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);
        return result;
    }

    private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));
}
