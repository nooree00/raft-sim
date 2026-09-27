using Raft.Core;
using Raft.Simulation;

namespace Raft.SimRun;

/// <summary>Fixed schedules for determinism checks (P1-08): every fault kind, placed relative to the run's length.</summary>
public static class Presets
{
    public static FaultSchedule Mix(long d)
    {
        NodeId n1 = new(1), n2 = new(2), n3 = new(3);
        return new FaultSchedule(
        [
            new Skew(0, n1, 11, 10),
            new Drop(d / 20, n1, n2),
            new Duplicate(d / 15, n2, n3),
            new Delay(d / 12, n3, n1, 50),
            new Reorder(d / 10, n1, n3),
            new Partition(d / 8, n2, n1),
            new SlowDisk(d / 6, n3, 40, d / 3),
            new Crash(d / 5, n3, DiskLoss.Torn),
            new Restart((d / 5) + (d / 20), n3),
            new Heal(d / 4, n2, n1),
            new Pause(d / 3, n2),
            new Unpause(d / 2, n2),
            new Crash(d * 6 / 10, n1, DiskLoss.Reordered),
            new Restart(d * 7 / 10, n1),
            new Crash(d * 8 / 10, n2, DiskLoss.Pending),
            new Restart(d * 9 / 10, n2),
        ]);
    }
}
