using System.Globalization;

namespace Raft.Core;

/// <summary>A server's identity within a cluster.</summary>
public readonly record struct NodeId(int Value)
{
    public override string ToString() => "n" + Value.ToString(CultureInfo.InvariantCulture);
}
