using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Raft.Core;

namespace Raft.Host;

/// <summary>
/// Three hosts in one process on loopback, for a measurement (P10-04's in-process cluster): fixed
/// ports from <paramref name="basePort"/> (the measuring process runs alone, so no other process
/// asks for them), data under <paramref name="root"/>, Core's events discarded (the Compose nodes
/// write theirs to standard output; the record says which), and every sync counted per host.
/// </summary>
public sealed class LocalCluster(string root, int basePort = 27_000) : IAsyncDisposable
{
    private readonly List<NodeHost> _hosts = [];
    private readonly Dictionary<NodeId, CountingFileSystem> _files = [];

    public IReadOnlyDictionary<NodeId, DnsEndPoint> Clients { get; private set; } = new Dictionary<NodeId, DnsEndPoint>();

    public IReadOnlyList<NodeHost> Hosts => _hosts;

    /// <summary>Each host's syncs so far.</summary>
    public long Syncs(NodeId n) => _files[n].Syncs;

    public void Start(RaftOptions? options = null)
    {
        var ids = Enumerable.Range(1, 3).Select(i => new NodeId(i)).ToList();
        var peers = ids.ToDictionary(n => n, n => new DnsEndPoint("127.0.0.1", basePort + n.Value));
        Clients = ids.ToDictionary(n => n, n => new DnsEndPoint("127.0.0.1", basePort + 100 + n.Value));
        foreach (var n in ids)
        {
            var dir = Path.Combine(root, n.ToString());
            var config = new HostConfig(n, peers, new IPEndPoint(IPAddress.Loopback, basePort + n.Value), new IPEndPoint(IPAddress.Loopback, basePort + 100 + n.Value), dir, options ?? RaftOptions.Default,
                FileSystem: d => _files[n] = new CountingFileSystem(new DirectoryFileSystem(d)));
            var host = new NodeHost(config, TextWriter.Null);
            host.Start();
            _hosts.Add(host);
        }
    }

    /// <summary>The host that leads, waiting up to <paramref name="within"/>.</summary>
    public async Task<NodeId> LeaderAsync(TimeSpan within, CancellationToken cancel)
    {
        var end = DateTime.UtcNow + within;
        while (DateTime.UtcNow < end)
        {
            for (var i = 0; i < _hosts.Count; i++)
            {
                if (_hosts[i].Role == Role.Leader)
                {
                    return new NodeId(i + 1);
                }
            }

            await Task.Delay(50, cancel).ConfigureAwait(false);
        }

        throw new TimeoutException("no host leads");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hosts)
        {
            await h.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>A data directory whose syncs are counted (P10-04: the leader's syncs per committed write).</summary>
public sealed class CountingFileSystem(IFileSystem inner) : IFileSystem
{
    private long _syncs;

    public long Syncs => Interlocked.Read(ref _syncs);

    public IReadOnlyList<string> List() => inner.List();

    public byte[]? Read(string name) => inner.Read(name);

    public bool Exists(string name) => inner.Exists(name);

    public void Append(string name, ReadOnlySpan<byte> data) => inner.Append(name, data);

    public void WriteAt(string name, long offset, ReadOnlySpan<byte> data) => inner.WriteAt(name, offset, data);

    public void Truncate(string name, long length) => inner.Truncate(name, length);

    public void Rename(string source, string target) => inner.Rename(source, target);

    public void Delete(string name) => inner.Delete(name);

    public void Sync(string name)
    {
        Interlocked.Increment(ref _syncs);
        inner.Sync(name);
    }

    public void SyncDirectory() => inner.SyncDirectory();
}
