using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Raft.Core;
using Raft.Kv;
using Raft.Simulation;

namespace Raft.Core.Tests;

/// <summary>
/// P7-08, the done criterion: every node's state machine agrees with the committed entries. Each
/// node's key-value state, when it restores a snapshot and when an incarnation ends, is compared
/// with a replay of the entries committed in fact, by their recorded commands, from the empty state
/// up to the index the node had applied. Never with another node (spec §10: a party whose answer
/// came from the other proves nothing, and a follower's snapshot came from a leader). The applied
/// ghost ids are State Machine Safety's (P4-01, decision 5).
/// </summary>
internal sealed class AgreementProbe
{
    /// <summary>Never compacting in a test's lifetime: the largest threshold small commands allow.</summary>
    public static readonly RaftOptions Uncompacted = RaftOptions.Default with { MaxCommandBytes = 4_096, SnapshotThreshold = RaftOptions.LargestThresholdFor(4_096) };

    private readonly Dictionary<NodeId, List<Recorded>> _machines = [];

    /// <summary>A new state machine for a node's next incarnation, recorded.</summary>
    public IStateMachine For(NodeId node)
    {
        var m = new Recorded();
        (_machines.TryGetValue(node, out var l) ? l : _machines[node] = []).Add(m);
        return m;
    }

    /// <summary>Every disagreement, and how many comparisons were made.</summary>
    public (List<string> Failures, int Compared) Check(IReadOnlyList<Observation> observations, LogAnalysis log)
    {
        var failures = new List<string>();
        var compared = 0;
        var committed = log.Commits.OrderBy(c => c.Index).Select(c => log.CommandOf(c.Ghost) ?? []).ToList();
        var replays = new Dictionary<long, string>();
        string Replay(long index)
        {
            if (!replays.TryGetValue(index, out var bytes))
            {
                var kv = new KvStateMachine();
                foreach (var c in committed.Take((int)index).Where(c => c.Length > 0 && !Configuration.IsInternal(c)))
                {
                    kv.Apply(c);
                }

                replays[index] = bytes = Convert.ToHexString(kv.Snapshot().Span);
            }

            return bytes;
        }

        foreach (var (node, machines) in _machines)
        {
            var incarnations = Incarnations(observations, node);
            for (var j = 0; j < machines.Count && j < incarnations.Count; j++)
            {
                var (restores, applied) = incarnations[j];
                var m = machines[j];
                if (restores.Count != m.Restores.Count)
                {
                    failures.Add($"{node} incarnation {N(j + 1)}: {N(m.Restores.Count)} restores of its state machine, {N(restores.Count)} restore events");
                    continue;
                }

                var checks = restores.Zip(m.Restores, (index, bytes) => (Index: index, Bytes: bytes, What: "restored")).Append((Index: applied, Bytes: m.Final, What: "held"));
                foreach (var (index, bytes, what) in checks)
                {
                    if (index > committed.Count)
                    {
                        failures.Add($"{node} incarnation {N(j + 1)} {what} a state at {N(index)}, past the {N(committed.Count)} entries committed in fact");
                        continue;
                    }

                    compared++;
                    if (Convert.ToHexString(bytes) != Replay(index))
                    {
                        failures.Add($"{node} incarnation {N(j + 1)} {what} a state at {N(index)} that differs from the committed entries replayed to {N(index)}");
                    }
                }
            }
        }

        return (failures, compared);
    }

    /// <summary>For each incarnation of <paramref name="node"/>: its restore events' indices, and the last index it applied or restored.</summary>
    private static List<(List<long> Restores, long Applied)> Incarnations(IReadOnlyList<Observation> observations, NodeId node)
    {
        var result = new List<(List<long> Restores, long Applied)>();
        foreach (var o in observations.Where(o => o.Node == node))
        {
            switch (o)
            {
                case StartObservation:
                    result.Add(([], 0));
                    break;
                case EmittedObservation { Event.Name: "apply" or "restore" } e when result.Count > 0:
                    var index = long.Parse(e.Event.Fields.Single(f => f.Key == "index").Value, CultureInfo.InvariantCulture);
                    if (e.Event.Name == "restore")
                    {
                        result[^1].Restores.Add(index);
                    }

                    result[^1] = (result[^1].Restores, index);
                    break;
            }
        }

        return result;
    }

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>A key-value state machine that keeps the bytes of each snapshot it restored.</summary>
    private sealed class Recorded : IStateMachine
    {
        private readonly KvStateMachine _kv = new();

        public List<byte[]> Restores { get; } = [];

        public byte[] Final => _kv.Snapshot().ToArray();

        public ReadOnlyMemory<byte> Apply(ReadOnlyMemory<byte> command) => _kv.Apply(command);

        public ReadOnlyMemory<byte> Snapshot() => _kv.Snapshot();

        public void Restore(ReadOnlyMemory<byte> snapshot)
        {
            _kv.Restore(snapshot);
            Restores.Add(_kv.Snapshot().ToArray());
        }
    }
}
