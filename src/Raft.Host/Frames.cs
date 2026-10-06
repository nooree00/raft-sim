using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Raft.Host;

/// <summary>
/// The framed transport between hosts (P9-02, phase 9 decision 2): a 4-byte big-endian length, the
/// encoded message, and a CRC-32C of both. Canonical form (P3-02) does not detect a corruption that
/// decodes to another valid message, and TCP's checksum is 16 bits; a frame whose checksum fails is
/// refused, and its connection closed by the caller: to Raft, a lost message.
/// </summary>
public static class Frames
{
    public static byte[] Encode(ReadOnlySpan<byte> message)
    {
        var frame = new byte[4 + message.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, message.Length);
        message.CopyTo(frame.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4 + message.Length), Crc32C.Compute(frame.AsSpan(0, 4 + message.Length)));
        return frame;
    }

    /// <summary>
    /// The next frame's message, assembled across as many reads as the stream needs, or null at a
    /// clean end of stream. Throws <see cref="InvalidDataException"/> for a length past
    /// <paramref name="maxLength"/>, a stream cut inside a frame, or a checksum that fails.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, int maxLength, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var head = new byte[4];
        var got = await stream.ReadAtLeastAsync(head, 4, throwOnEndOfStream: false, cancel).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        if (got < 4)
        {
            throw new InvalidDataException("the stream ended inside a frame's length");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(head);
        if (length < 0 || length > maxLength)
        {
            throw new InvalidDataException($"a frame of {length} bytes, outside 0 to {maxLength}");
        }

        var body = new byte[4 + length + 4];
        head.CopyTo(body, 0);
        if (await stream.ReadAtLeastAsync(body.AsMemory(4), length + 4, throwOnEndOfStream: false, cancel).ConfigureAwait(false) < length + 4)
        {
            throw new InvalidDataException("the stream ended inside a frame");
        }

        var expected = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4 + length));
        if (Crc32C.Compute(body.AsSpan(0, 4 + length)) != expected)
        {
            throw new InvalidDataException("a frame whose checksum fails");
        }

        return body.AsSpan(4, length).ToArray();
    }
}
