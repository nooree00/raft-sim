using System;

namespace Raft.Core;

/// <summary>A Raft term (paper §5.1): a monotonically increasing logical clock. Term 0 precedes any election.</summary>
public readonly record struct Term(long Value) : IComparable<Term>
{
    public static Term Zero { get; }

    public Term Next() => new(checked(Value + 1));

    public int CompareTo(Term other) => Value.CompareTo(other.Value);

    public static bool operator <(Term left, Term right) => left.Value < right.Value;

    public static bool operator >(Term left, Term right) => left.Value > right.Value;

    public static bool operator <=(Term left, Term right) => left.Value <= right.Value;

    public static bool operator >=(Term left, Term right) => left.Value >= right.Value;
}
