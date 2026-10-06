using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Raft.Checker;
using Raft.Core;
using Raft.Core.Tests;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P9-06, phase 9 decisions 3 and 4: the real client's history. Vacuity risk: a client whose history
/// is translated wrongly can make any execution look linearizable, for example by dropping or
/// closing indeterminate attempts (spec §6). Guarded by a server that never answers one request: its
/// attempt must come back without a response and the adapter must keep its operation open.
/// Sabotage S-client-4 (a given-up attempt written with the time it gave up as its response).
/// </summary>
public sealed class RealClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AHistoryWrittenAsJsonLinesReadsBackTheSame()
    {
        HistoryEntry[] written =
        [
            new(0, 1, 1, "Register|", 10, 20, "ok|4"),
            new(0, 2, 1, "Session|4|1|Append|k|c0s1", 30, null, ""),
            new(0, 3, 2, "Session|4|1|Append|k|c0s1", 1_030, 1_050, "ok"),
        ];
        var text = new StringWriter();
        foreach (var e in written)
        {
            RealClient.Write(text, e);
        }

        Assert.Equal(written, RealClient.Read(new StringReader(text.ToString())));
    }

    /// <summary>
    /// A server that registers the client, then reads every later request and never answers. The
    /// client's write is given up after its retries: each attempt recorded with no response, one
    /// operation in the history, left indeterminate. Sabotage S-client-4.
    /// </summary>
    [Fact]
    public async Task AnAttemptThatIsNeverAnsweredIsRecordedWithoutAResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stopServer = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = Task.Run(async () =>
        {
            var held = new List<TcpClient>();
            try
            {
                while (true)
                {
                    var c = await listener.AcceptTcpClientAsync(stopServer.Token);
                    held.Add(c);
                    var reader = new StreamReader(c.GetStream(), Encoding.ASCII);
                    if (await reader.ReadLineAsync(stopServer.Token) == "Register|")
                    {
                        await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes("ok|4\n"), stopServer.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                held.ForEach(c => c.Dispose());
            }
        }, Ct);

        var entries = new List<HistoryEntry>();
        var config = new ClientConfig(new Dictionary<NodeId, DnsEndPoint> { [new NodeId(1)] = new("127.0.0.1", port) }, 1, TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(100), Retries: 2);
        await RealClient.RunAsync(config, entries.Add, Ct);
        await stopServer.CancelAsync();
        await server;

        var unanswered = entries.Where(e => e.Request != "Register|").ToList();
        Assert.NotEmpty(unanswered);
        Assert.All(unanswered, e => Assert.Null(e.Response));
        var history = ClientHistory.From(HostHistory.ToClientLog(entries));
        Assert.True(history.Indeterminate >= 1 && history.Completed == 0, $"completed {history.Completed}, indeterminate {history.Indeterminate}: an unanswered attempt closed its operation");
        Assert.True(History.Problems(history.History).Count == 0, "history problems: " + string.Join("; ", History.Problems(history.History).Take(5)));
    }
}
