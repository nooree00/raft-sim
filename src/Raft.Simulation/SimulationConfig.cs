namespace Raft.Simulation;

/// <summary>
/// The fixed parameters of a run. Time is in abstract units (think milliseconds); every duration here
/// is in those units. Integer arithmetic throughout — no floating point touches the clock.
/// </summary>
public sealed record SimulationConfig
{
    public int Nodes { get; init; } = 3;

    /// <summary>
    /// The initial configuration: nodes 1 to this; 0 means every node (P6-08). The others start as
    /// spares, outside the configuration, until a membership change adds them.
    /// </summary>
    public int Members { get; init; }

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

    /// <summary>
    /// Positive control only (P7-00): renames are issued at once instead of waiting behind the rename
    /// barrier for every earlier write to be durable. A crash can then keep a rename and lose the
    /// write it depends on, which the disk models as an empty file under the real name.
    /// </summary>
    public bool ReleaseRenamesEarly { get; init; }
}
