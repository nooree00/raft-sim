using System;
using System.Collections.Generic;
using System.Linq;

namespace Raft.Checker;

/// <summary>
/// The reference oracle (spec §6): as naive as possible, so that it is correct by inspection.
/// For every subset of the indeterminate operations (each may or may not have taken effect) and
/// every permutation of the chosen operations, accept if the permutation respects real-time order
/// and the sequential model reproduces every completed operation's output. No memoisation, no
/// per-key decomposition — it must not depend on the properties the WGL checker will rely on.
/// Exponential; refuses more than <see cref="MaxOperations"/> operations.
/// </summary>
public static class BruteForceOracle
{
    public const int MaxOperations = 9;

    public static bool IsLinearizable(IReadOnlyList<Operation> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        if (ops.Count > MaxOperations)
        {
            throw new ArgumentException($"brute force is limited to {MaxOperations} operations; got {ops.Count}", nameof(ops));
        }

        var completed = ops.Where(o => !o.IsIndeterminate).ToList();
        var indeterminate = ops.Where(o => o.IsIndeterminate).ToList();

        for (var mask = 0; mask < 1 << indeterminate.Count; mask++)
        {
            var chosen = new List<Operation>(completed);
            chosen.AddRange(indeterminate.Where((_, i) => (mask & (1 << i)) != 0));
            if (Permutations(chosen).Any(Legal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Legal(IReadOnlyList<Operation> order)
    {
        for (var i = 0; i < order.Count; i++)
        {
            for (var j = i + 1; j < order.Count; j++)
            {
                if (order[j].Precedes(order[i]))
                {
                    return false;
                }
            }
        }

        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var op in order)
        {
            var output = KvModel.Apply(state, op);
            if (!op.IsIndeterminate && output != op.Output)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<List<Operation>> Permutations(List<Operation> items)
    {
        if (items.Count <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var rest = new List<Operation>(items);
            rest.RemoveAt(i);
            foreach (var tail in Permutations(rest))
            {
                tail.Insert(0, items[i]);
                yield return tail;
            }
        }
    }
}
