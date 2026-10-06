using System.Text;
using Raft.Kv;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P9-01, phase 9 decision 5: when the session table is full, a `Register` evicts the session used
/// least recently, by the index of the entry that last named it, so every node evicts the same one.
/// Constructions at a bound of 3. Vacuity risk: a table that never fills never evicts; guarded by
/// each construction asserting the eviction (an `unknown-session|` reply) before anything else.
/// Sabotage S-sess-5 (eviction by the session's id).
/// </summary>
public sealed class SessionExpiryTests
{
    private static string Apply(KvStateMachine kv, long index, string command) => Encoding.ASCII.GetString(kv.Apply(index, Encoding.ASCII.GetBytes(command)).Span);

    /// <summary>Sessions 1, 2 and 3; session 1 used at 4; a fourth `Register` at 5 evicts 2, the least recently used, not 1, the oldest. Sabotage S-sess-5.</summary>
    [Fact]
    public void AFullTableEvictsTheLeastRecentlyUsedSessionNotTheOldest()
    {
        var kv = new KvStateMachine(maxSessions: 3);
        Apply(kv, 1, "Register|");
        Apply(kv, 2, "Register|");
        Apply(kv, 3, "Register|");
        Assert.Equal("ok", Apply(kv, 4, "Session|1|1|Append|k|a"));

        Assert.Equal("ok|5", Apply(kv, 5, "Register|"));
        Assert.Equal("unknown-session|", Apply(kv, 6, "Session|2|1|Append|k|b"));
        Assert.Equal("ok", Apply(kv, 7, "Session|1|2|Append|k|c"));
        Assert.Equal(3, kv.Sessions);
    }

    /// <summary>
    /// The prediction's construction: a retry answered from the cache is a use. Session 1's command is
    /// retried at 7, after 2 and 3 were used at 5 and 6; the `Register` at 8 evicts 2, and session 1's
    /// retry is still answered from its cache.
    /// </summary>
    [Fact]
    public void ARetryAnsweredFromTheCacheCountsAsAUse()
    {
        var kv = new KvStateMachine(maxSessions: 3);
        Apply(kv, 1, "Register|");
        Apply(kv, 2, "Register|");
        Apply(kv, 3, "Register|");
        Apply(kv, 4, "Session|1|1|Append|k|a");
        Apply(kv, 5, "Session|2|1|Append|k|b");
        Apply(kv, 6, "Session|3|1|Append|k|c");
        Assert.Equal("ok", Apply(kv, 7, "Session|1|1|Append|k|a"));

        Assert.Equal("ok|8", Apply(kv, 8, "Register|"));
        Assert.Equal("unknown-session|", Apply(kv, 9, "Session|2|2|Append|k|d"));
        Assert.Equal("ok", Apply(kv, 10, "Session|1|1|Append|k|a"));
        Assert.Equal("ok|abc", Apply(kv, 0, "Get|k"));
    }

    /// <summary>The last use is in the snapshot: a node that restored mid-way evicts the same session as one that applied everything.</summary>
    [Fact]
    public void ANodeRestoredFromASnapshotEvictsTheSameSession()
    {
        var whole = new KvStateMachine(maxSessions: 3);
        var restored = new KvStateMachine(maxSessions: 3);
        string[] first = ["Register|", "Register|", "Register|", "Session|3|1|Put|x|1", "Session|1|1|Put|y|1"];
        string[] rest = ["Register|", "Session|2|1|Put|z|1", "Session|1|2|Put|y|2", "Register|"];
        var index = 0L;
        foreach (var c in first)
        {
            Apply(whole, ++index, c);
        }

        restored.Restore(whole.Snapshot());
        foreach (var c in rest)
        {
            index++;
            Assert.Equal(Apply(whole, index, c), Apply(restored, index, c));
        }

        Assert.Equal("unknown-session|", Apply(whole, ++index, "Session|3|2|Put|x|2"));
        Assert.Equal(whole.Snapshot().ToArray(), restored.Snapshot().ToArray());
    }
}
