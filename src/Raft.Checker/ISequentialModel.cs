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
}

public static class SequentialModel
{
    /// <summary>The output of an operation applied where it is undefined; equal to no real output.</summary>
    public const string Undefined = "⊥undefined";
}
