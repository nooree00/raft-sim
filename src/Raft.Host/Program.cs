using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Raft.Core;

namespace Raft.Host;

/// <summary>
/// `Raft.Host node --id 1 --peers 1=n1:7000,2=n2:7000,3=n3:7000 --client-port 7100 --data /data`
/// runs one node until it is killed. `Raft.Host client --nodes 1=n1:7100,... --clients 3 --seconds 60
/// --timeout-ms 1000 --history /out/history.jsonl` runs the real client and writes its history.
/// `Raft.Host request --node n1:7100 --line Status|` sends one line and prints the reply (the
/// orchestration asks each node its role this way).
/// Core's events go to standard output as JSON lines (phase 9 decision 6).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("usage: raft-host node|client [options]").ConfigureAwait(false);
            return 2;
        }

        var options = Options(args);
        switch (args[0])
        {
            case "node":
                var id = new NodeId(int.Parse(options["--id"], CultureInfo.InvariantCulture));
                var peers = Endpoints(options["--peers"]);
                // The P9-07 positive control, for its one measurement in Compose: never set in a real configuration.
                var raft = Environment.GetEnvironmentVariable("RAFT_ANSWER_AT_APPEND") == "1" ? RaftOptions.Default with { AnswerAtAppend = true } : RaftOptions.Default;
                var config = new HostConfig(id, peers, new IPEndPoint(IPAddress.Any, peers[id].Port), new IPEndPoint(IPAddress.Any, int.Parse(options["--client-port"], CultureInfo.InvariantCulture)), options["--data"], raft);
                var host = new NodeHost(config, Console.Out);
                await using (host.ConfigureAwait(false))
                {
                    host.Start();
                    await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
                }

                return 0;
            case "client":
                var client = new ClientConfig(
                    Endpoints(options["--nodes"]),
                    int.Parse(options.GetValueOrDefault("--clients", "3"), CultureInfo.InvariantCulture),
                    TimeSpan.FromSeconds(double.Parse(options.GetValueOrDefault("--seconds", "30"), CultureInfo.InvariantCulture)),
                    TimeSpan.FromMilliseconds(double.Parse(options.GetValueOrDefault("--timeout-ms", "1000"), CultureInfo.InvariantCulture)),
                    Seed: int.Parse(options.GetValueOrDefault("--seed", "1"), CultureInfo.InvariantCulture));
                var history = new StreamWriter(options["--history"]) { AutoFlush = true };
                await using (history.ConfigureAwait(false))
                {
                    await RealClient.RunAsync(client, e => RealClient.Write(history, e), CancellationToken.None).ConfigureAwait(false);
                }

                return 0;
            case "request":
                var at = options["--node"];
                var colon = at.LastIndexOf(':');
                var reply = await RealClient.SendAsync(new DnsEndPoint(at[..colon], int.Parse(at[(colon + 1)..], CultureInfo.InvariantCulture)), options["--line"], TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                await Console.Out.WriteLineAsync(reply ?? "no-reply").ConfigureAwait(false);
                return reply is null ? 1 : 0;
            default:
                await Console.Error.WriteLineAsync("unknown mode " + args[0]).ConfigureAwait(false);
                return 2;
        }
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i + 1 < args.Length; i += 2)
        {
            o[args[i]] = args[i + 1];
        }

        return o;
    }

    /// <summary>"1=n1:7000,2=n2:7000" as endpoints by node id.</summary>
    private static Dictionary<NodeId, DnsEndPoint> Endpoints(string list)
    {
        var d = new Dictionary<NodeId, DnsEndPoint>();
        foreach (var item in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (id, address) = (item[..item.IndexOf('=', StringComparison.Ordinal)], item[(item.IndexOf('=', StringComparison.Ordinal) + 1)..]);
            var colon = address.LastIndexOf(':');
            d[new NodeId(int.Parse(id, CultureInfo.InvariantCulture))] = new DnsEndPoint(address[..colon], int.Parse(address[(colon + 1)..], CultureInfo.InvariantCulture));
        }

        return d;
    }
}
