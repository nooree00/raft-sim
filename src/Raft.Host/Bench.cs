using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Raft.Host;

/// <summary>
/// The measurements behind phase 10's target (P10-02, decision 2): the model's inputs, each measured
/// alone. Latencies are kept per sample in <see cref="Stopwatch"/> ticks and read as percentiles of
/// the sorted samples (decision 3; phase 9's finding that microseconds are coarser than a loopback
/// round trip).
/// </summary>
public static class Bench
{
    /// <summary>
    /// The data directory's sync: <paramref name="count"/> appends of <paramref name="bytes"/> bytes
    /// to one file, each followed by the sync the host's disk executor issues after a persist
    /// (<see cref="DirectoryFileSystem.Sync"/>), timed from the append's start to the sync's return.
    /// </summary>
    public static double[] Sync(string directory, int count, int bytes)
    {
        var files = new DirectoryFileSystem(directory);
        var name = "bench-sync-" + Guid.NewGuid().ToString("N");
        var data = new byte[bytes];
        new Random(1).NextBytes(data);
        var samples = new double[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                var start = Stopwatch.GetTimestamp();
                files.Append(name, data);
                files.Sync(name);
                samples[i] = Micros(Stopwatch.GetTimestamp() - start);
            }
        }
        finally
        {
            files.Delete(name);
        }

        return samples;
    }

    /// <summary>
    /// P12-06's floor for the persists: <paramref name="writers"/> threads, each appending and syncing
    /// its own file in a directory of its own under <paramref name="directory"/>, back to back, as a
    /// cluster's nodes sync on one disk; every sync's time from its append's start to its return.
    /// </summary>
    public static double[] SyncConcurrent(string directory, int count, int bytes, int writers)
    {
        var all = new double[writers][];
        var threads = Enumerable.Range(0, writers).Select(w => new Thread(() => all[w] = Sync(Directory.CreateDirectory(Path.Combine(directory, $"w{w}")).FullName, count, bytes))).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        return [.. all.SelectMany(x => x)];
    }

    /// <summary>
    /// P12-06's floor for the host's queues: one input handed from a thread to a loop blocked on it,
    /// as the host's readers hand to its loop (an unbounded channel whose continuations run on the
    /// writer, the loop waiting on <c>WaitToReadAsync</c>), each timed from before the write to the
    /// take; the writer sleeps a millisecond between inputs, so each wakes a loop that was asleep, as
    /// at 625 writes a second.
    /// </summary>
    public static double[] HandOff(int count)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<long>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = true });
        var samples = new double[count];
        var loop = new Thread(() =>
        {
            for (var i = 0; i < count;)
            {
                if (!channel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    return;
                }

                while (channel.Reader.TryRead(out var stamp))
                {
                    samples[i++] = Micros(Stopwatch.GetTimestamp() - stamp);
                }
            }
        });
        loop.Start();
        for (var i = 0; i < count; i++)
        {
            Thread.Sleep(1);
            channel.Writer.TryWrite(Stopwatch.GetTimestamp());
        }

        loop.Join();
        return samples;
    }

    /// <summary>
    /// P12-06's floor for the network hops: a frame of <paramref name="bytes"/> bytes written to a
    /// loopback connection whose reader is blocked on a thread of its own (the host's peer reads since
    /// P12-05), each timed from before the write to the frame's read; a millisecond apart, so each
    /// read wakes a reader that was asleep.
    /// </summary>
    public static double[] Hop(int count, int bytes)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient { NoDelay = true };
        client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var peer = listener.AcceptTcpClient();
        var samples = new double[count];
        var sent = new long[count];
        var reader = new Thread(() =>
        {
            var stream = peer.GetStream();
            for (var i = 0; i < count; i++)
            {
                _ = Frames.Read(stream, 1 << 20) ?? throw new IOException("the writer closed");
                samples[i] = Micros(Stopwatch.GetTimestamp() - Volatile.Read(ref sent[i]));
            }
        });
        reader.Start();
        var s = client.GetStream();
        var frame = Frames.Encode(new byte[bytes]);
        for (var i = 0; i < count; i++)
        {
            Thread.Sleep(1);
            Volatile.Write(ref sent[i], Stopwatch.GetTimestamp());
            s.Write(frame);
        }

        reader.Join();
        return samples;
    }

    /// <summary>
    /// The loopback round trip: a framed message of <paramref name="bytes"/> bytes sent over one TCP
    /// connection and the same frame echoed back, <paramref name="count"/> times, each timed from the
    /// write's start to the echo's last byte, as the host's transport sends and reads frames.
    /// </summary>
    public static async Task<double[]> RoundTripAsync(int count, int bytes, CancellationToken cancel)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var echo = Task.Run(
            async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(cancel).ConfigureAwait(false);
                peer.NoDelay = true;
                var stream = peer.GetStream();
                while (await Frames.ReadAsync(stream, 1 << 20, cancel).ConfigureAwait(false) is { } payload)
                {
                    await stream.WriteAsync(Frames.Encode(payload), cancel).ConfigureAwait(false);
                }
            },
            cancel);

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port, cancel).ConfigureAwait(false);
        var s = client.GetStream();
        var frame = Frames.Encode(new byte[bytes]);
        var samples = new double[count];
        for (var i = 0; i < count; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await s.WriteAsync(frame, cancel).ConfigureAwait(false);
            _ = await Frames.ReadAsync(s, 1 << 20, cancel).ConfigureAwait(false) ?? throw new IOException("the echo closed");
            samples[i] = Micros(Stopwatch.GetTimestamp() - start);
        }

        client.Close();
        await echo.ConfigureAwait(false);
        return samples;
    }

    /// <summary>The summary a record keeps: count, mean and percentiles (nearest rank) in microseconds.</summary>
    public static Dictionary<string, double> Summary(IEnumerable<double> samples)
    {
        var sorted = samples.Order().ToArray();
        if (sorted.Length == 0)
        {
            // Nothing answered: a count of none, and no percentile of nothing (JSON has no NaN).
            return new Dictionary<string, double>(StringComparer.Ordinal) { ["count"] = 0 };
        }

        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["count"] = sorted.Length,
            ["mean_us"] = Math.Round(sorted.Average(), 1),
            ["p50_us"] = Math.Round(Measurement.Percentile(sorted, 50), 1),
            ["p90_us"] = Math.Round(Measurement.Percentile(sorted, 90), 1),
            ["p99_us"] = Math.Round(Measurement.Percentile(sorted, 99), 1),
            ["p999_us"] = Math.Round(Measurement.Percentile(sorted, 99.9), 1),
            ["max_us"] = Math.Round(sorted[^1], 1),
        };
    }

    /// <summary>Stopwatch ticks as microseconds, without the truncation of whole microseconds.</summary>
    public static double Micros(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;
}
