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
}
