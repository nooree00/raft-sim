using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Raft.Host;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P16-01: a client line's arrival is the kernel's receive time of its segment, on the trace's clock, on a
/// real loopback socket pair. Vacuity risk: an arrival stamped when the reader returns makes the reader's
/// part zero whatever it holds, so a reader that waits a planted 20 ms before reading must show the wait in
/// its part and not in delivery (S-hand-1); and an arrival on the kernel's wall clock without the offset to
/// the trace's lies outside its write, as the decomposition's causality count reads it (S-hand-3).
/// </summary>
public sealed class KernelArrivalTests
{
    private const string Line = "Put|k1|v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A connected pair: the client's stream, and the server's reader with the kernel's timestamps on, once a
    /// line comes back stamped. The kernel turns its receive timestamps on for the machine in deferred work
    /// when the first socket asks, so a pair's first segments may come unstamped (a first version failed so).
    /// </summary>
    private static (TcpClient Client, TcpClient Server, KernelLineReader Reader) Pair()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient { NoDelay = true };
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        var server = listener.AcceptTcpClient();
        Assert.True(KernelArrival.Enable(server.Client), "the kernel refused to timestamp what a TCP socket receives");
        var reader = new KernelLineReader(server.Client, Ct);
        for (var i = 0; i < 200; i++)
        {
            client.GetStream().Write("Status|\n"u8);
            Assert.Equal("Status|", reader.ReadLine());
            if (reader.Arrived is not null)
            {
                return (client, server, reader);
            }

            Thread.Sleep(5);
        }

        Assert.Fail("no line came back with the kernel's receive time in a second");
        return default;
    }

    /// <summary>The reader 20 ms late: its part holds the 20 ms, delivery a small part of it. Sabotage S-hand-1.</summary>
    [Fact]
    public void AReadersDelayIsInTheReadersPartNotInDelivery()
    {
        var (client, server, reader) = Pair();
        using (client)
        using (server)
        using (reader)
        {
            var sent = Stopwatch.GetTimestamp();
            client.GetStream().Write("Put|k1|v1\n"u8);
            Thread.Sleep(20);
            var line = reader.ReadLine();
            var read = Stopwatch.GetTimestamp();

            Assert.Equal(Line, line);
            var arrived = Assert.IsType<long>(reader.Arrived);
            var delivery = Bench.Micros(arrived - sent);
            var waited = Bench.Micros(read - arrived);
            Assert.True(waited >= 15_000, FormattableString.Invariant($"the reader's part is {waited:F0} us with the reader 20 ms late (delivery {delivery:F0} us): the arrival is not the kernel's"));
            Assert.True(delivery < 5_000, FormattableString.Invariant($"delivery is {delivery:F0} us on loopback, the reader's part {waited:F0} us"));
        }
    }

    /// <summary>An arrival on the trace's clock lies between the line's send and its read. Sabotage S-hand-3.</summary>
    [Fact]
    public void AnArrivalOnTheTracesClockLiesWithinItsWrite()
    {
        var (client, server, reader) = Pair();
        using (client)
        using (server)
        using (reader)
        {
            for (var i = 0; i < 20; i++)
            {
                var sent = Stopwatch.GetTimestamp();
                client.GetStream().Write("Put|k1|v1\n"u8);
                Assert.Equal(Line, reader.ReadLine());
                var read = Stopwatch.GetTimestamp();

                var arrived = Assert.IsType<long>(reader.Arrived);
                Assert.True(Decomposition.Within(sent, arrived, read), FormattableString.Invariant($"line {i}: arrival {Bench.Micros(arrived - sent):F1} us after its send, its read {Bench.Micros(read - sent):F1} us after: outside its write"));
            }
        }
    }

    /// <summary>A read holding two lines gives the first no arrival: the kernel stamped their segment again when the second joined it.</summary>
    [Fact]
    public void ALineReadWithAnotherAfterItHasNoArrival()
    {
        var (client, server, reader) = Pair();
        using (client)
        using (server)
        using (reader)
        {
            client.GetStream().Write("Put|k1|v1\nPut|k2|v2\n"u8);
            Thread.Sleep(20);

            Assert.Equal(Line, reader.ReadLine());
            Assert.Null(reader.Arrived);
            Assert.Equal("Put|k2|v2", reader.ReadLine());
            Assert.NotNull(reader.Arrived);
        }
    }
}
