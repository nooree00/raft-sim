using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Raft.Core;
using Raft.Host;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P9-02, phase 9 decision 2: the framed transport. Vacuity risk: a loopback socket hands back a
/// small frame in one read, so reassembly is never exercised; guarded by a stream that returns one
/// byte per read, and by the largest frame the configured options allow (spec §10). Sabotage
/// S-frame-1 (the checksum computed and not compared).
/// </summary>
public sealed class FrameTests
{
    private const int Limit = 128 * 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A stream that returns at most one byte per read, as a slow network may.</summary>
    private sealed class OneByte(byte[] data) : Stream
    {
        private int _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position { get => _at; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at == data.Length || count == 0)
            {
                return 0;
            }

            buffer[offset] = data[_at++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static readonly Message[] Messages =
    [
        new RequestVote(new Term(3), new NodeId(2), 7, new Term(2)),
        new RequestVoteResponse(new Term(3), true),
        new AppendEntries(new Term(3), new NodeId(1), 4, new Term(2), [new LogEntry(new Term(3), Encoding.ASCII.GetBytes("Put|k|v"))], 4, 9),
        new AppendEntriesResponse(new Term(3), false, 2, 9),
        new InstallSnapshot(new Term(3), new NodeId(1), 20, new Term(2), 0, [1, 2, 3], false),
        new InstallSnapshotResponse(new Term(3), 20, 3, false),
    ];

    [Fact]
    public void TheChecksumIsCrc32CByItsPublishedCheckValue() =>
        Assert.Equal(0xE3069283u, Crc32C.Compute(Encoding.ASCII.GetBytes("123456789")));

    [Fact]
    public async Task EveryMessageTypeRoundTripsOverALoopbackSocket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var sender = new TcpClient();
        await sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Ct);
        using var receiver = await listener.AcceptTcpClientAsync(Ct);
        foreach (var m in Messages)
        {
            await sender.GetStream().WriteAsync(Frames.Encode(MessageCodec.Encode(m)), Ct);
        }

        foreach (var m in Messages)
        {
            var bytes = await Frames.ReadAsync(receiver.GetStream(), Limit, Ct);
            Assert.True(m.Equals(MessageCodec.Decode(bytes!)), $"{m} came back as {MessageCodec.Decode(bytes!)}");
        }
    }

    [Fact]
    public async Task AFrameDeliveredOneBytePerReadIsAssembled()
    {
        var frames = Messages.SelectMany(m => Frames.Encode(MessageCodec.Encode(m))).ToArray();
        var stream = new OneByte(frames);
        foreach (var m in Messages)
        {
            Assert.True(m.Equals(MessageCodec.Decode((await Frames.ReadAsync(stream, Limit, Ct))!)));
        }

        Assert.Null(await Frames.ReadAsync(stream, Limit, Ct));
    }

    /// <summary>Every single-bit flip of a frame is refused, never delivered as a message. Sabotage S-frame-1.</summary>
    [Fact]
    public async Task ACorruptedBitIsNeverDelivered()
    {
        var frame = Frames.Encode(MessageCodec.Encode(Messages[0]));
        var delivered = new List<string>();
        for (var bit = 0; bit < frame.Length * 8; bit++)
        {
            var copy = (byte[])frame.Clone();
            copy[bit / 8] ^= (byte)(1 << (bit % 8));
            try
            {
                var got = await Frames.ReadAsync(new MemoryStream(copy), Limit, Ct);
                delivered.Add($"bit {bit}: {(got is null ? "end" : Convert.ToHexString(got))}");
            }
            catch (InvalidDataException)
            {
            }
        }

        Assert.True(delivered.Count == 0, "a corrupted frame was delivered: " + string.Join("; ", delivered.Take(3)));
    }

    /// <summary>
    /// Spec §10: the largest frame the configured options allow, a full batch (64 entries) of the
    /// largest command (1 MB), about 64 MB, crosses a loopback socket whole.
    /// </summary>
    [Fact]
    public async Task TheLargestFrameTheOptionsAllowCrossesALoopbackSocket()
    {
        var options = RaftOptions.Default;
        var command = new byte[options.MaxCommandBytes];
        new Random(1).NextBytes(command);
        var entries = Enumerable.Range(0, options.MaxEntriesPerAppend).Select(i => new LogEntry(new Term(1), command)).ToList();
        var message = MessageCodec.Encode(new AppendEntries(new Term(1), new NodeId(1), 0, Term.Zero, entries, 0));
        Assert.True(message.Length > 64 * 1024 * 1024, $"the largest frame is {message.Length} bytes");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var sender = new TcpClient();
        await sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Ct);
        using var receiver = await listener.AcceptTcpClientAsync(Ct);
        var write = sender.GetStream().WriteAsync(Frames.Encode(message), Ct).AsTask();
        var read = await Frames.ReadAsync(receiver.GetStream(), Limit, Ct);
        await write;
        Assert.Equal(message.Length, read!.Length);
        Assert.True(message.AsSpan().SequenceEqual(read));
    }
}
