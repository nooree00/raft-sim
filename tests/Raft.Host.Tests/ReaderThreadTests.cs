using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// is this test's failure, with the host's exit code and output, and not the end of the test run. The
/// trigger is the one that fires on demand: a client line past 64 KiB, which a traced host's reader
/// (<see cref="KernelLineReader"/>) refuses with an exception outside <c>ServeClient</c>'s list of quiet
/// endings. The stop race that found the defect (run 40421775799: a connection disposed by a stop as its
/// reader started) does not happen on demand; the root's catch covers it by covering every type. The
/// host's life is read from its process, never from an answer: an <c>ok|</c> on its port once stood in
/// for it, and in one run on GitHub (40464503656, S-thread-1 at <c>30be2ed</c>) something answered
/// while the test went on as though the host had (the person: a proxy satisfied by something other than
/// what it names). The port is the child's because its own <c>host-start</c>, written after both
/// listeners bind, is read first. Every assertion's message carries the child's state (its process, its
/// standard output and error), so a wrong result can be read from the message alone. Vacuity risk: a line
/// the host never reads to its limit leaves the reader waiting and passes on any code; guarded by the host
/// ending that connection and logging why. Sabotage S-thread-1.
/// </summary>
public sealed class ReaderThreadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A line past the traced reader's 64 KiB, its exception logged, its connection ended, the host's process still running and answering. Sabotage S-thread-1.</summary>
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
        string State() => Observed(host, output, errors);
        try
        {
            await UntilAsync(() => host.HasExited || Events(output, "host-start").Length > 0, TimeSpan.FromSeconds(20));
            Assert.True(!host.HasExited && Events(output, "host-start").Length == 1, $"the host did not start, its listeners bound, within 20 s: {State()}");
            var before = await RequestAsync(client, "Status|", TimeSpan.FromSeconds(5));
            Assert.True(before is not null && before.StartsWith("ok|", StringComparison.Ordinal) && !host.HasExited, $"the host's answer to Status| before the line was '{before ?? "none"}': {State()}");

            string ending;
            using (var tcp = new TcpClient())
            {
                await tcp.ConnectAsync(client, Ct);
                ending = await EndingAfterOverLongLineAsync(tcp.GetStream(), TimeSpan.FromSeconds(10));
            }

            Assert.True(ending != Open, $"the over-long line's connection was {ending}: the reader never reached the line's limit, so nothing was tested; {State()}");

            // The reader's ending or the process's, whichever comes: a process the exception ends exits
            // without logging, and this waits for it rather than inferring it from an answer.
            await UntilAsync(() => host.HasExited || Events(output, "reader-failed").Length > 0, TimeSpan.FromSeconds(10));
            Assert.True(!host.HasExited, $"the host's process ended after a reader thread's exception: {State()}");
            var logged = Events(output, "reader-failed");
            Assert.True(logged is ["System.IO.InvalidDataException"], $"the host logged {logged.Length} reader-failed event(s) ({string.Join(", ", logged)}), not one naming System.IO.InvalidDataException: {State()}");
            var after = await RequestAsync(client, "Status|", TimeSpan.FromSeconds(5));
            Assert.True(after is not null && after.StartsWith("ok|", StringComparison.Ordinal) && !host.HasExited, $"the host's answer to Status| after the line was '{after ?? "none"}': {State()}");
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

    private const string Open = "still open after 10 s";

    private static async Task UntilAsync(Func<bool> done, TimeSpan timeout)
    {
        for (var start = Stopwatch.GetTimestamp(); !done() && Stopwatch.GetElapsedTime(start) < timeout;)
        {
            await Task.Delay(20, Ct);
        }
    }

    private static void Keep(ConcurrentQueue<string> lines, string? line)
    {
        if (line is not null)
        {
            lines.Enqueue(line);
        }
    }

    /// <summary>
    /// The child as this test observed it, for every assertion's message: its process (running, or its exit
    /// code), the last lines of its standard output (its latest events) and the first of its standard error
    /// (a crash's exception comes first), each line shortened.
    /// </summary>
    private static string Observed(Process host, ConcurrentQueue<string> output, ConcurrentQueue<string> errors)
    {
        static string Lines(IEnumerable<string> lines) =>
            lines.Any() ? string.Join(" / ", lines.Select(l => l.Length > 200 ? l[..200] + "…" : l)) : "nothing";

        var process = host.HasExited ? FormattableString.Invariant($"its process exited with code {host.ExitCode}") : FormattableString.Invariant($"its process is running (pid {host.Id})");
        return $"{process}; standard output: {Lines(output.TakeLast(6))}; standard error: {Lines(errors.Take(6))}";
    }

    /// <summary>The <c>error</c> field of each of the host's events named <paramref name="name"/>, in order.</summary>
    private static string[] Events(ConcurrentQueue<string> output, string name) =>
        [.. output.Select(Event).Where(e => e.Name == name).Select(e => e.Error)];

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
    /// Sends a line one byte past the traced reader's 64 KiB buffer, with no newline, and reports how the
    /// host's end of the connection behaved: ended (a read returned nothing), reset (a read or the write
    /// failed, with the error), or <see cref="Open"/> at the timeout.
    /// </summary>
    private static async Task<string> EndingAfterOverLongLineAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(timeout);
        try
        {
            await stream.WriteAsync(Enumerable.Repeat((byte)'x', (64 * 1024) + 1).ToArray(), limit.Token);
            var read = await stream.ReadAsync(new byte[1], limit.Token);
            return read == 0 ? "ended" : "answered with a byte, not ended";
        }
        catch (IOException e)
        {
            return $"reset ({e.Message})";
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            return Open;
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
