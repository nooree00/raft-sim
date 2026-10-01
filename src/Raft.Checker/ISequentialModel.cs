using System.Collections.Generic;

namespace Raft.Checker;

/// <summary>
/// A sequential specification: a fresh state, and the output of applying an operation to a state
/// (mutating it). An operation that is undefined in a state (Herlihy &amp; Wing's partial operations,
/// e.g. Dequeue on an empty queue) returns <see cref="Undefined"/>, which matches no recorded output.
/// </summary>
public interface ISequentialModel<TState>
{
    TState NewState();

    string? Apply(TState state, Operation op);

    /// <summary>An independent copy (the WGL search applies operations to copies and backtracks).</summary>
    TState Copy(TState state);

    /// <summary>
    /// A canonical text for the state: equal states give equal text. The WGL checker memoises on
    /// (linearized operations, fingerprint); two states with one fingerprint would merge searches.
    /// </summary>
    string Fingerprint(TState state);

    /// <summary>
    /// True for an operation that never changes the state (a read). Indeterminate, it has no effect
    /// and no output to explain, so the WGL checker removes it before the search (P5-05): it cannot
    /// change a verdict, and each one doubles the sets of operations the search can reach.
    /// </summary>
    bool IsReadOnly(Operation op) => false;

    /// <summary>
    /// A sub-history with the indeterminate operations no observation depends on removed (P5-06).
    /// Removing an indeterminate operation chooses "it never took effect", always allowed, so a
    /// reduction can only make the checker reject more, never accept more: a wrong one rejects a
    /// correct history. The default removes nothing.
    /// </summary>
    IReadOnlyList<Operation> Reduce(IReadOnlyList<Operation> subHistory) => subHistory;
}

public static class SequentialModel
{
    /// <summary>The output of an operation applied where it is undefined; equal to no real output.</summary>
    public const string Undefined = "⊥undefined";
}
