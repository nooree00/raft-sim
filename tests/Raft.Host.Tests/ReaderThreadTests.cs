using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Raft.Host;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P16-12: an exception on a reader thread ends its connection, logged, and never the host's process. The
/// host runs in a process of its own (`Raft.Host node`, one node, traced), so a process the exception ends
/// is this test's failure, with the host's exit code and standard error, and not the end of the test run.
/// The trigger is the one that fires on demand: a client line past 64 KiB, which a traced host's reader
/// (<see cref="KernelLineReader"/>) refuses with an exception outside <c>ServeClient</c>'s list of quiet
/// endings. The stop race that found the defect (run 40421775799: a connection disposed by a stop as its
/// reader started) does not happen on demand; the root's catch covers it by covering every type. Vacuity
/// risk: a line the host never reads to its limit leaves the reader waiting and passes on any code; guarded
/// by the host closing that connection and logging its ending. Sabotage S-thread-1.
/// </summary>
public sealed class ReaderThreadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A line past the traced reader's 64 KiB, its exception logged, its connection closed, the host still answering. Sabotage S-thread-1.</summary>
    [Fact]
    public async Task AReaderThreadsExceptionEndsItsConnectionNotTheProcess()
    {
        var peer = HostCluster.FreePort();
        var client = new IPEndPoint(IPAddress.Loopback, HostCluster.FreePort());
        var data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"reader-{Guid.NewGuid():N}")).FullName;
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])[typeof(NodeHost).Assembly.Location, "node", "--id", "1", "--peers", FormattableString.Invariant($"1=127.0.0.1:{peer}"), "--client-port", client.Port.ToString(CultureInfo.InvariantCulture), "--data", data])
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["RAFT_HANDOFF_TRACE"] = "1";
        var output = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        using var host = Process.Start(psi)!;
        host.OutputDataReceived += (_, e) => Keep(output, e.Data);
        host.ErrorDataReceived += (_, e) => Keep(errors, e.Data);
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();
        try
        {
            string? answer = null;
            for (var start = Stopwatch.GetTimestamp(); answer is null && !host.HasExited && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(20);)
            {
                answer = await RequestAsync(client, "Status|", TimeSpan.FromSeconds(1));
            }

            Assert.True(answer is not null && answer.StartsWith("ok|", StringComparison.Ordinal), $"the host never answered Status| before the line was sent: {Gone(host, errors)}");

            bool closed;
            using (var tcp = new TcpClient())
            {
                await tcp.ConnectAsync(client, Ct);
                closed = await ClosedAfterOverLongLineAsync(tcp.GetStream(), TimeSpan.FromSeconds(10));
            }

            Assert.True(closed, "the host did not close the over-long line's connection within 10 s: its reader never reached the line's limit, so nothing was tested");
            answer = await RequestAsync(client, "Status|", TimeSpan.FromSeconds(5));
            if (answer is null)
            {
                // A host the exception ended may still be exiting: its exit code is the failure's message.
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await host.WaitForExitAsync(exit.Token);
                }
                catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
                {
                }
            }

            Assert.True(answer is not null && answer.StartsWith("ok|", StringComparison.Ordinal), $"the host stopped answering after a reader thread's exception: {Gone(host, errors)}");
            string[] logged = [];
            for (var start = Stopwatch.GetTimestamp(); logged.Length == 0 && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(5);)
            {
                await Task.Delay(20, Ct);
                logged = [.. output.Select(Event).Where(e => e.Name == "reader-failed").Select(e => e.Error)];
            }

            Assert.Equal("System.IO.InvalidDataException", Assert.Single(logged));
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill();
            }

            await host.WaitForExitAsync(CancellationToken.None);
            Directory.Delete(data, recursive: true);
        }
    }

    private static void Keep(ConcurrentQueue<string> lines, string? line)
    {
        if (line is not null)
        {
            lines.Enqueue(line);
        }
    }

    /// <summary>What the host's process shows when it stops answering: its exit code if it ended, and its standard error's first lines.</summary>
    private static string Gone(Process host, ConcurrentQueue<string> errors) =>
        (host.HasExited ? FormattableString.Invariant($"its process exited with code {host.ExitCode}") : "its process is still running") + "; standard error: " + string.Join(" / ", errors.Take(4));

    /// <summary>A host event's name and, for a reader's ending, its exception's type; a line that is not an event is neither.</summary>
    private static (string Name, string Error) Event(string line)
    {
        try
        {
            var e = JsonDocument.Parse(line).RootElement;
            return (e.GetProperty("Event").GetString()!, e.GetProperty("Fields").TryGetProperty("error", out var error) ? error.GetString()! : "");
        }
        catch (JsonException)
        {
            return ("", "");
        }
    }

    /// <summary>
    /// Sends a line one byte past the traced reader's 64 KiB buffer, with no newline, and waits for the host
    /// to end the connection: a read that returns nothing, or one the host's reset fails. False if the
    /// connection is still open at the timeout.
    /// </summary>
    private static async Task<bool> ClosedAfterOverLongLineAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(timeout);
        try
        {
            await stream.WriteAsync(Enumerable.Repeat((byte)'x', (64 * 1024) + 1).ToArray(), limit.Token);
            return await stream.ReadAsync(new byte[1], limit.Token) == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Sends one request on a new connection and reads its reply, or null within the timeout.</summary>
    private static async Task<string?> RequestAsync(IPEndPoint endpoint, string request, TimeSpan timeout)
    {
        using var client = new TcpClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(endpoint, limit.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request + "\n"), limit.Token);
            using var reader = new StreamReader(stream, Encoding.ASCII);
            return await reader.ReadLineAsync(limit.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or IOException)
        {
            return null;
        }
    }
}
