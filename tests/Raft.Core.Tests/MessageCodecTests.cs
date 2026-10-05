using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Raft.Core;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P3-02: the codec's canonical form. Every message has exactly one encoding, and every byte string
/// decodes to at most one message. Tested both ways: decode(encode(m)) = m over generated messages,
/// and encode(decode(b)) = b for every b that decodes, over byte strings mutated from valid
/// encodings. Vacuity risk: random bytes almost never decode, so the second property would hold
/// over nothing. Guarded: the inputs start from valid encodings, and a floor of them must decode.
/// Sabotages S-codec-1..3.
/// </summary>
public sealed class MessageCodecTests
{
    private const int Messages = 2_000;

    internal static Message RandomMessage(Random rng)
    {
        Term T() => new(rng.Next(3) == 0 ? rng.NextInt64(0, long.MaxValue) : rng.Next(0, 20));
        long I() => rng.Next(3) == 0 ? rng.NextInt64(0, long.MaxValue) : rng.Next(0, 50);
        NodeId N() => new(rng.Next(1, 6));
        byte[] B() => Enumerable.Range(0, rng.Next(0, 6)).Select(_ => (byte)rng.Next(256)).ToArray();
        return rng.Next(6) switch
        {
            0 => new RequestVote(T(), N(), I(), T()),
            1 => new RequestVoteResponse(T(), rng.Next(2) == 0),
            2 => new AppendEntries(T(), N(), I(), T(),
                Enumerable.Range(0, rng.Next(0, 4)).Select(_ => new LogEntry(T(), Enumerable.Range(0, rng.Next(0, 6)).Select(_ => (byte)rng.Next(256)).ToArray())).ToList(), I()),
            3 => new AppendEntriesResponse(T(), rng.Next(2) == 0, I()),
            4 => new InstallSnapshot(T(), N(), I(), T(), I(), B(), rng.Next(2) == 0),
            _ => new InstallSnapshotResponse(T(), I(), I(), rng.Next(2) == 0),
        };
    }

    [Fact]
    public void EveryMessageDecodesToItself()
    {
        var rng = new Random(3);
        var kinds = new HashSet<Type>();
        for (var i = 0; i < Messages; i++)
        {
            var m = RandomMessage(rng);
            kinds.Add(m.GetType());
            var decoded = MessageCodec.Decode(MessageCodec.Encode(m));

            Assert.True(m.Equals(decoded), $"{m} decoded as {decoded}");
        }

        Assert.Equal(6, kinds.Count);
    }

    /// <summary>
    /// Mutations of valid encodings: a byte flipped, inserted, deleted, the tail cut or extended.
    /// Whatever decodes must re-encode to exactly the same bytes, or two encodings mean one message.
    /// </summary>
    [Fact]
    public void EveryByteStringThatDecodesIsTheEncodingOfWhatItDecodesTo()
    {
        var rng = new Random(5);
        int tried = 0, decoded = 0;
        for (var i = 0; i < Messages; i++)
        {
            var bytes = MessageCodec.Encode(RandomMessage(rng)).ToList();
            for (var k = 0; k < 8; k++)
            {
                var b = Mutate(bytes, rng);
                tried++;
                if (MessageCodec.Decode(b.ToArray()) is { } m)
                {
                    decoded++;
                    Assert.True(MessageCodec.Encode(m).SequenceEqual(b), $"not canonical: {Convert.ToHexString(b.ToArray())} decodes to {m}, which encodes as {Convert.ToHexString(MessageCodec.Encode(m))}");
                }
            }
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "codec-mutations.txt"),
            $"{decoded} of {tried} mutated encodings decode\n{FlipReport()}\n");
        Assert.True(decoded >= tried / 10, $"only {decoded} of {tried} mutations decode: the canonical property is barely exercised");
    }

    private static List<byte> Mutate(List<byte> bytes, Random rng)
    {
        var b = new List<byte>(bytes);
        switch (rng.Next(5))
        {
            case 0: var i = rng.Next(b.Count); b[i] = (byte)(b[i] ^ (1 << rng.Next(8))); break;
            case 1: b.Insert(rng.Next(b.Count + 1), (byte)rng.Next(256)); break;
            case 2: b.RemoveAt(rng.Next(b.Count)); break;
            case 3: b.RemoveRange(b.Count - 1 - rng.Next(Math.Min(4, b.Count - 1)), 1); break;
            default: b.Add((byte)rng.Next(256)); break;
        }

        return b;
    }

    /// <summary>
    /// P3-02's prediction, recorded, not asserted: the share of single-bit flips of a valid
    /// RequestVote that decode to a different valid message, which canonical form cannot detect.
    /// </summary>
    private static string FlipReport()
    {
        var rng = new Random(9);
        int flips = 0, different = 0;
        for (var n = 0; n < 200; n++)
        {
            var m = new RequestVote(new Term(rng.Next(1, 100)), new NodeId(rng.Next(1, 6)), rng.Next(0, 1000), new Term(rng.Next(0, 100)));
            var bytes = MessageCodec.Encode(m);
            for (var i = 0; i < bytes.Length; i++)
            {
                for (var bit = 0; bit < 8; bit++)
                {
                    var b = (byte[])bytes.Clone();
                    b[i] ^= (byte)(1 << bit);
                    flips++;
                    different += MessageCodec.Decode(b) is { } d && !d.Equals(m) ? 1 : 0;
                }
            }
        }

        return $"single-bit flips of RequestVote: {different} of {flips} decode to a different valid message ({(100.0 * different / flips).ToString("F0", CultureInfo.InvariantCulture)}%)";
    }

    [Theory]
    [InlineData("trailing byte", "010000000000000001000000020000000000000003000000000000000000")]
    [InlineData("unknown type", "09000000000000000101")]
    [InlineData("boolean 2", "02000000000000000102")]
    [InlineData("negative term", "02FFFFFFFFFFFFFFFF01")]
    [InlineData("node id 0", "0100000000000000010000000000000000000000000000000000000000")]
    [InlineData("truncated", "01000000000000000100000002000000000000000300000000000000")]
    [InlineData("entry count past the end", "0300000000000000010000000100000000000000000000000000000000000000000000000000000001")]
    [InlineData("empty", "")]
    public void AMalformedOrNonCanonicalByteStringDecodesToNothing(string what, string hex) =>
        Assert.True(MessageCodec.Decode(Convert.FromHexString(hex)) is null, what + " decoded");

    [Fact]
    public void TheHandWrittenEncodingsAreTheOnesTheCodecProduces()
    {
        Assert.Equal("02000000000000000701", Convert.ToHexString(MessageCodec.Encode(new RequestVoteResponse(new Term(7), true))));
        Assert.Equal("0100000000000000010000000200000000000000030000000000000000", Convert.ToHexString(MessageCodec.Encode(new RequestVote(new Term(1), new NodeId(2), 3, Term.Zero))));
        Assert.Equal("0300000000000000010000000100000000000000000000000000000000000000000000000000000000", Convert.ToHexString(MessageCodec.Encode(new AppendEntries(new Term(1), new NodeId(1), 0, Term.Zero, [], 0))));
    }
}
