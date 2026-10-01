using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Raft.Checker;

public enum Verdict
{
    Linearizable,
    NotLinearizable,

    /// <summary>The search exhausted its state budget. Neither a pass nor a rejection.</summary>
    Undecided,
}

/// <summary>
/// A checker's answer. On rejection: the key whose sub-history cannot be linearized, that
/// sub-history, and the longest linearizable prefix the search found for it (the point past which
/// no operation fits). <see cref="StatesExplored"/> is the deterministic cost: distinct
/// (linearized set, model state) pairs the search visited, over all keys.
/// </summary>
public sealed record CheckResult(Verdict Verdict, string? Key, IReadOnlyList<Operation> SubHistory, IReadOnlyList<Operation> LongestPrefix, long StatesExplored)
{
    public bool IsLinearizable => Verdict == Verdict.Linearizable;
}

/// <summary>
/// The Wing &amp; Gong / Lowe linearizability checker (spec §6): depth-first search over
/// linearization orders with memoisation over reachable (set of linearized operations, model
/// state), after decomposing the history per key (Herlihy &amp; Wing locality — valid because every
/// operation touches one key). An operation may be linearized next if no unlinearized completed
/// operation responded before it was invoked. An indeterminate operation (no response) may be
/// linearized at any point after its invocation, or never, and is unconstrained by any response: the
/// search succeeds once every completed operation has been placed. A budget in states turns an
/// unbounded search into <see cref="Verdict.Undecided"/>, never into a verdict. Each key's search has
/// the whole budget (P5-05): shared, an undecided verdict named whichever key was being searched when
/// an earlier key had spent it. A key that cannot be linearized is reported even when another key was
/// undecided; <see cref="CheckResult.StatesExplored"/> is the total over keys. Sabotages S-wgl-5, S-wgl-6.
/// An indeterminate read-only operation is removed before the search (<see cref="ISequentialModel{TState}.IsReadOnly"/>):
/// it changes nothing and its output is unknown, so no linearization depends on it. Sabotages S-wgl-7, S-wgl-8.
/// </summary>
public static class WglChecker
{
    public const long DefaultBudget = 1_000_000;

    public static CheckResult Check(IReadOnlyList<Operation> ops, long budget = DefaultBudget, bool decompose = true) =>
        Check(ops, KvModel.Instance, budget, decompose);

    public static CheckResult Check<TState>(IReadOnlyList<Operation> ops, ISequentialModel<TState> model, long budget = DefaultBudget, bool decompose = true)
    {
        ArgumentNullException.ThrowIfNull(ops);
        ArgumentNullException.ThrowIfNull(model);
        ops = ops.Where(o => !(o.IsIndeterminate && model.IsReadOnly(o))).ToList();
        var groups = decompose
            ? ops.GroupBy(o => o.Key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (Key: (string?)g.Key, Ops: g.ToList())).ToList()
            : [(Key: (string?)null, Ops: ops.ToList())];
        long explored = 0;
        CheckResult? undecided = null;
        foreach (var (key, sub) in groups)
        {
            var search = new Search<TState>(sub, model, budget);
            var verdict = search.Run();
            explored += search.Explored;
            if (verdict == Verdict.NotLinearizable)
            {
                return new CheckResult(Verdict.NotLinearizable, key, sub, search.LongestPrefix, explored);
            }

            if (verdict == Verdict.Undecided)
            {
                undecided ??= new CheckResult(Verdict.Undecided, key, sub, search.LongestPrefix, explored);
            }
        }

        return undecided is null
            ? new CheckResult(Verdict.Linearizable, null, [], [], explored)
            : undecided with { StatesExplored = explored };
    }

    /// <summary>
    /// Failure signatures for the shrinker (P2-10): "linearizability@key" for every key whose
    /// sub-history cannot be linearized, "undecided@key" where the budget ran out. The key is part
    /// of the signature, so shrinking one anomaly cannot converge on another at a different key.
    /// </summary>
    public static IReadOnlySet<string> Signatures(IReadOnlyList<Operation> ops, long budget = DefaultBudget)
    {
        ArgumentNullException.ThrowIfNull(ops);
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in ops.Select(o => o.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var r = Check(ops.Where(o => o.Key == key).ToList(), budget);
            if (r.Verdict != Verdict.Linearizable)
            {
                signatures.Add((r.Verdict == Verdict.Undecided ? "undecided@" : "linearizability@") + key);
            }
        }

        return signatures;
    }

    private sealed class Search<TState>(List<Operation> ops, ISequentialModel<TState> model, long budget)
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly List<Operation> _path = [];
        private readonly int _completed = ops.Count(o => !o.IsIndeterminate);

        public long Explored { get; private set; }

        public List<Operation> LongestPrefix { get; private set; } = [];

        private bool _outOfBudget;

        public Verdict Run()
        {
            var found = Step(new BitArray(ops.Count), model.NewState(), 0);
            return found ? Verdict.Linearizable : _outOfBudget ? Verdict.Undecided : Verdict.NotLinearizable;
        }

        private bool Step(BitArray done, TState state, int completedDone)
        {
            if (completedDone == _completed)
            {
                return true;
            }

            var key = Key(done, state);
            if (!_seen.Add(key))
            {
                return false;
            }

            if (++Explored > budget)
            {
                _outOfBudget = true;
                return false;
            }

            // The earliest response among unlinearized completed operations: nothing invoked after it may go first.
            var horizon = long.MaxValue;
            for (var i = 0; i < ops.Count; i++)
            {
                if (!done[i] && ops[i].Response is { } r && r < horizon)
                {
                    horizon = r;
                }
            }

            for (var i = 0; i < ops.Count && !_outOfBudget; i++)
            {
                var op = ops[i];
                if (done[i] || op.Invoke > horizon)
                {
                    continue;
                }

                var next = model.Copy(state);
                var output = model.Apply(next, op);
                if (!op.IsIndeterminate && output != op.Output)
                {
                    continue;
                }

                done[i] = true;
                _path.Add(op);
                if (_path.Count > LongestPrefix.Count)
                {
                    LongestPrefix = _path.ToList();
                }

                if (Step(done, next, completedDone + (op.IsIndeterminate ? 0 : 1)))
                {
                    return true;
                }

                _path.RemoveAt(_path.Count - 1);
                done[i] = false;
            }

            return false;
        }

        private string Key(BitArray done, TState state)
        {
            var bits = new char[done.Length];
            for (var i = 0; i < done.Length; i++)
            {
                bits[i] = done[i] ? '1' : '0';
            }

            return new string(bits) + "|" + model.Fingerprint(state);
        }
    }
}
