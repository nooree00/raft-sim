using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Raft.Host;

/// <summary>
/// P12-04: how the load generator's schedule waits for a write's time, on a thread of its own. On
/// Linux it sleeps on the kernel's monotonic clock until 80 µs before the time, with the thread's
/// timer slack at its least (by default the kernel may wake a sleeper up to 50 µs late), and spins
/// the rest; elsewhere it sleeps whole milliseconds while more than two remain, then spins. The
/// first version slept only while more than two milliseconds remained, so above 500 writes a second
/// it spun through every interval: in Compose at 3,125 a second its container took 100 to 120% of a
/// processor beside a leader at about 70%, and the median and the tail grew (interleaved against
/// this version, `p12-04-compose-ab-*`). A sleep to within 20 µs, tried first, woke too late too
/// often (a 99th percentile of 49 and 177 µs alone); 80 µs leaves the spin under a quarter of a
/// processor at 3,125 a second.
/// </summary>
internal static class Pacing
{
    private static readonly long SpinTicks = Stopwatch.Frequency / 12_500;

    /// <summary>Sets the calling thread's timer slack to its least, on Linux; nothing elsewhere.</summary>
    public static void Prepare()
    {
        if (OperatingSystem.IsLinux())
        {
            _ = NativeMethods.Prctl(NativeMethods.SetTimerSlack, 1, 0, 0, 0);
        }
    }

    /// <summary>Returns at <paramref name="target"/>, a <see cref="Stopwatch.GetTimestamp"/> value, and never before it.</summary>
    public static void WaitUntil(long target)
    {
        var wake = target - SpinTicks;
        if (OperatingSystem.IsLinux() && Stopwatch.Frequency == 1_000_000_000)
        {
            // Stopwatch's timestamp on Linux is the monotonic clock in nanoseconds.
            var at = new NativeMethods.Timespec { Seconds = wake / 1_000_000_000, Nanoseconds = wake % 1_000_000_000 };
            while (Stopwatch.GetTimestamp() < wake && NativeMethods.ClockNanosleep(NativeMethods.ClockMonotonic, NativeMethods.TimerAbsolute, in at, IntPtr.Zero) == NativeMethods.Interrupted)
            {
            }
        }
        else
        {
            var millisecond = Stopwatch.Frequency / 1000;
            while (wake - Stopwatch.GetTimestamp() > 2 * millisecond)
            {
                Thread.Sleep(1);
            }
        }

        while (Stopwatch.GetTimestamp() < target)
        {
            Thread.SpinWait(20);
        }
    }
}

/// <summary>The two libc calls the generator's schedule sleeps with (Linux, P12-04).</summary>
internal static partial class NativeMethods
{
    public const int ClockMonotonic = 1;
    public const int TimerAbsolute = 1;
    public const int SetTimerSlack = 29;
    public const int Interrupted = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    /// <summary>Returns 0, or the error number (EINTR, 4, when a signal interrupted the sleep).</summary>
    [LibraryImport("libc", EntryPoint = "clock_nanosleep")]
    public static partial int ClockNanosleep(int clock, int flags, in Timespec request, IntPtr remaining);

    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    public static partial int Prctl(int option, nuint argument2, nuint argument3, nuint argument4, nuint argument5);
}
