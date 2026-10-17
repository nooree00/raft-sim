using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Raft.Host;

/// <summary>
/// P16-01, phase 16 decision 2: when a client's line reached the leader's host, by the kernel's receive
/// time of the segment that carried it (`SO_TIMESTAMPNS`, read with `recvmsg`), so client-to-leader splits
/// into delivery (the generator's write to the kernel's receipt) and the reader (the receipt to the line
/// read). In a traced bench run only; a host that cannot have the timestamp reads as it always did, and its
/// lines carry no arrival. The kernel stamps a queued segment again when later data joins it, so a line
/// is given its read's time only when the read ended at its newline: the bench's generator keeps one write
/// outstanding on each connection, so a line is alone in its connection's queue.
/// </summary>
public static class KernelArrival
{
    /// <summary>Asks the kernel to timestamp what <paramref name="socket"/> receives; false where it cannot (not Linux, or refused).</summary>
    public static bool Enable(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            // The raw option: SetSocketOption maps the names it knows, and this is not one of them.
            socket.SetRawSocketOption(NativeMethods.SolSocket, NativeMethods.SoTimestampNs, BitConverter.GetBytes(1));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// A kernel receive time (CLOCK_REALTIME, nanoseconds) on the trace's clock (<see cref="Stopwatch"/>,
    /// CLOCK_MONOTONIC): the offset between the two clocks read now, between two reads of the trace's
    /// clock, the tightest of three. Read at each line, so a slewed wall clock moves no arrival far.
    /// Sabotage S-hand-3.
    /// </summary>
    public static long ToTraceClock(long realtimeNanos)
    {
        var best = long.MaxValue;
        var offset = 0L;
        for (var i = 0; i < 3; i++)
        {
            var before = Nanos(Stopwatch.GetTimestamp());
            var wall = Realtime();
            var after = Nanos(Stopwatch.GetTimestamp());
            if (after - before < best)
            {
                best = after - before;
                offset = wall - (before + ((after - before) / 2));
            }
        }

        return Ticks(realtimeNanos - offset);
    }

    private static long Nanos(long ticks) => Stopwatch.Frequency == 1_000_000_000 ? ticks : (long)((Int128)ticks * 1_000_000_000 / Stopwatch.Frequency);

    private static long Ticks(long nanos) => Stopwatch.Frequency == 1_000_000_000 ? nanos : (long)((Int128)nanos * Stopwatch.Frequency / 1_000_000_000);

    private static long Realtime()
    {
        _ = NativeMethods.ClockGettime(NativeMethods.ClockRealtime, out var now);
        return (now.Seconds * 1_000_000_000) + now.Nanoseconds;
    }
}

/// <summary>
/// A client connection's lines, read with `recvmsg`, each with the kernel's receive time of its segment
/// on the trace's clock (<see cref="Arrived"/>) when its read ended at its newline (<see cref="KernelArrival"/>);
/// for a socket <see cref="KernelArrival.Enable"/> accepted. Reads as <see cref="StreamReader.ReadLine"/>
/// does for lines ended by "\n" or "\r\n", and checks for the host's stop between waits. It waits as the
/// stream's reader does, in one blocking receive a line: a first version waited in `poll` and then read,
/// and put client-to-leader's 99th percentile at 3,125 writes a second at 6.2 to 7.7 ms against the
/// stream reader's 1.9 to 2.3 (m:p16-02-cost-*, m:p16-02-cost2-*), the instrument slowing the wake it
/// measures. Its buffers are on the pinned-object heap (that alone moved nothing, m:p16-02-cost2-*).
/// </summary>
public sealed class KernelLineReader : TextReader
{
    private readonly Socket _socket;
    private readonly CancellationToken _stop;
    private readonly byte[] _buffer = GC.AllocateUninitializedArray<byte>(64 * 1024, pinned: true);
    private readonly byte[] _control = GC.AllocateArray<byte>(128, pinned: true);
    private int _start;
    private int _end;
    private long? _stamp;

    /// <summary>The socket made blocking, its receives timing out every tenth of a second so the reader sees the host's stop.</summary>
    public KernelLineReader(Socket socket, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
        _stop = stop;
        var handle = socket.SafeHandle;
        var added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            var fd = (int)handle.DangerousGetHandle();
            var flags = NativeMethods.Fcntl(fd, NativeMethods.GetFlags, 0);
            if (flags < 0 || NativeMethods.Fcntl(fd, NativeMethods.SetFlags, flags & ~NativeMethods.NonBlocking) < 0)
            {
                throw new IOException($"fcntl failed with error {Marshal.GetLastPInvokeError()}");
            }
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }

        var timeout = new byte[16];
        BitConverter.TryWriteBytes(timeout.AsSpan(8), 100_000L);
        socket.SetRawSocketOption(NativeMethods.SolSocket, NativeMethods.SoReceiveTimeout, timeout);
    }

    /// <summary>The kernel's receive time of the line <see cref="ReadLine"/> returned last, on the trace's clock, or null when its read held more than it. Sabotage S-hand-1.</summary>
    public long? Arrived { get; private set; }

    public override string? ReadLine()
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var line = Encoding.ASCII.GetString(_buffer, _start, newline - _start).TrimEnd('\r');
                Arrived = newline + 1 == _end ? _stamp : null;
                _start = newline + 1;
                return line;
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                throw new InvalidDataException("a client line longer than 64 KiB");
            }

            var read = Receive();
            if (read <= 0)
            {
                Arrived = null;
                if (_end == _start)
                {
                    return null;
                }

                var rest = Encoding.ASCII.GetString(_buffer, _start, _end - _start);
                _start = _end;
                return rest;
            }
        }
    }

    /// <summary>
    /// One blocking `recvmsg` into the buffer's free space: the bytes read, 0 at the stream's end or the host's
    /// stop; the read's timestamp kept only when the read did not fill the space (a read that filled it may
    /// have stopped inside a segment that later data had joined).
    /// </summary>
    private unsafe int Receive()
    {
        var handle = _socket.SafeHandle;
        var added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            var fd = (int)handle.DangerousGetHandle();
            var space = _buffer.Length - _end;
            fixed (byte* buffer = _buffer, control = _control)
            {
                while (!_stop.IsCancellationRequested)
                {
                    var iov = new NativeMethods.IoVec { Base = buffer + _end, Length = (nuint)space };
                    var message = new NativeMethods.MsgHdr { Iov = &iov, IovLength = 1, Control = control, ControlLength = (nuint)_control.Length };
                    var n = (int)NativeMethods.RecvMsg(fd, &message, 0);
                    if (n > 0)
                    {
                        _end += n;
                        _stamp = n < space && Timestamp(control, (int)message.ControlLength) is { } wall ? KernelArrival.ToTraceClock(wall) : null;
                        return n;
                    }

                    if (n == 0)
                    {
                        return 0;
                    }

                    // Interrupted, or the receive timeout passed with nothing to read: look at the stop and wait again.
                    var error = Marshal.GetLastPInvokeError();
                    if (error is NativeMethods.Interrupted or NativeMethods.WouldBlock)
                    {
                        continue;
                    }

                    throw new IOException($"recvmsg failed with error {error}");
                }

                return 0;
            }
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>The `SCM_TIMESTAMPNS` control message's time (CLOCK_REALTIME, nanoseconds), or null when the read carried none.</summary>
    private static unsafe long? Timestamp(byte* control, int length)
    {
        // struct cmsghdr { size_t len; int level; int type; } then its data, each message aligned to 8.
        for (var off = 0; off + 16 <= length;)
        {
            var size = (int)*(ulong*)(control + off);
            var level = *(int*)(control + off + 8);
            var type = *(int*)(control + off + 12);
            if (size < 16)
            {
                break;
            }

            if (level == NativeMethods.SolSocket && type == NativeMethods.SoTimestampNs && size >= 32)
            {
                var time = (NativeMethods.Timespec*)(control + off + 16);
                return (time->Seconds * 1_000_000_000) + time->Nanoseconds;
            }

            off += (size + 7) & ~7;
        }

        return null;
    }
}

/// <summary>The libc calls and constants a kernel receive time needs (Linux, x86-64 and arm64: P16-01).</summary>
internal static partial class NativeMethods
{
    public const int ClockRealtime = 0;
    public const int SolSocket = 1;
    public const int SoTimestampNs = 35;
    public const int SoReceiveTimeout = 20;
    public const int WouldBlock = 11;
    public const int GetFlags = 3;
    public const int SetFlags = 4;
    public const int NonBlocking = 0x800;

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct IoVec
    {
        public byte* Base;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MsgHdr
    {
        public void* Name;
        public uint NameLength;
        public IoVec* Iov;
        public nuint IovLength;
        public byte* Control;
        public nuint ControlLength;
        public int Flags;
    }

    [LibraryImport("libc", EntryPoint = "recvmsg", SetLastError = true)]
    public static unsafe partial nint RecvMsg(int fd, MsgHdr* message, int flags);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    public static partial int Fcntl(int fd, int command, int argument);

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    public static partial int ClockGettime(int clock, out Timespec now);
}
