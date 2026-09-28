namespace Raft.Simulation;

/// <summary>
/// The fixed parameters of a run. Time is in abstract units (think milliseconds); every duration here
/// is in those units. Integer arithmetic throughout — no floating point touches the clock.
/// </summary>
public sealed record SimulationConfig
{
    public int Nodes { get; init; } = 3;

    public long Duration { get; init; } = 600_000;

    /// <summary>Units between ticks delivered to a node (before clock skew).</summary>
    public long TickInterval { get; init; } = 1;

    public long MinNetworkDelay { get; init; } = 1;

    public long MaxNetworkDelay { get; init; } = 10;

    public long MinDiskLatency { get; init; } = 1;

    public long MaxDiskLatency { get; init; } = 3;

    /// <summary>Simulated clients (P2-08), network endpoints n101, n102, ...; each issues one operation at a time.</summary>
    public int Clients { get; init; }

    /// <summary>A client gives up on an operation after this long; it is then recorded as indeterminate.</summary>
    public long ClientTimeout { get; init; } = 500;
}
