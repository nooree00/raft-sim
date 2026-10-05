using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Raft.Kv;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P8-01: the session table in the key-value state machine (phase 8 decisions 2 and 3). Tested with
/// `Append` and `Cas`, never `Put` alone: a duplicated `Put` is invisible (spec §6). Vacuity risk: a
/// table never consulted passes every `Put` test; guarded by every duplicate here being an `Append`
/// or a `Cas`. Sabotages S-sess-1, S-sess-2.
/// </summary>
public sealed class SessionTests
{
    private static string Apply(KvStateMachine kv, long index, string command) => Encoding.ASCII.GetString(kv.Apply(index, Encoding.ASCII.GetBytes(command)).Span);

    private static string Get(KvStateMachine kv, string key) => Apply(kv, 0, "Get|" + key);

    [Fact]
    public void ARetriedAppendIsAppliedOnceAndAnsweredAgain()
    {
        var kv = new KvStateMachine();
        Assert.Equal("ok|5", Apply(kv, 5, "Register|"));
        Assert.Equal("ok", Apply(kv, 6, "Session|5|1|Append|x|a"));
        Assert.Equal("ok", Apply(kv, 7, "Session|5|1|Append|x|a"));
        Assert.Equal("ok|a", Get(kv, "x"));
    }

    /// <summary>A retried `Cas` answers what it answered the first time, though the value it compared has changed since.</summary>
    [Fact]
    public void ARetriedCasReturnsItsFirstAnswer()
    {
        var kv = new KvStateMachine();
        Apply(kv, 1, "Register|");
        Apply(kv, 2, "Register|");
        Assert.Equal("ok|true", Apply(kv, 3, "Session|1|1|Cas|x|-|v"));
        Assert.Equal("ok", Apply(kv, 4, "Session|2|1|Put|x|w"));
        Assert.Equal("ok|true", Apply(kv, 5, "Session|1|1|Cas|x|-|v"));
        Assert.Equal("ok|w", Get(kv, "x"));
    }

    [Fact]
    public void ALowerSequenceNumberIsRefusedAndAnUnknownSessionToo()
    {
        var kv = new KvStateMachine();
        Apply(kv, 1, "Register|");
        Apply(kv, 2, "Session|1|2|Append|x|b");
        Assert.Equal("stale|", Apply(kv, 3, "Session|1|1|Append|x|a"));
        Assert.Equal("unknown-session|", Apply(kv, 4, "Session|9|1|Append|x|c"));
        Assert.Equal("error", Apply(kv, 0, "Register|"));
        Assert.Equal("ok|b", Get(kv, "x"));
    }

    [Fact]
    public void SessionsAreIndependent()
    {
        var kv = new KvStateMachine();
        Apply(kv, 1, "Register|");
        Apply(kv, 2, "Register|");
        Apply(kv, 3, "Session|1|1|Append|x|a");
        Apply(kv, 4, "Session|2|1|Append|x|b");
        Apply(kv, 5, "Session|1|1|Append|x|a");
        Apply(kv, 6, "Session|2|2|Append|x|c");
        Assert.Equal("ok|abc", Get(kv, "x"));
    }

    /// <summary>Spec §5 item 6: the table is in the snapshot, so a retry after a restore is answered from it and applied nothing. Sabotage S-sess-1.</summary>
    [Fact]
    public void TheSessionTableIsInTheSnapshot()
    {
        var kv = new KvStateMachine();
        Apply(kv, 4, "Register|");
        Apply(kv, 5, "Session|4|1|Append|x|a");
        var restored = new KvStateMachine();
        restored.Restore(kv.Snapshot());

        Assert.Equal(1, restored.Sessions);
        Assert.Equal("ok", Apply(restored, 9, "Session|4|1|Append|x|a"));
        Assert.Equal("ok|a", Get(restored, "x"));
        Assert.Equal(kv.Snapshot().ToArray(), restored.Snapshot().ToArray());
    }

    /// <summary>Equal tables give equal bytes, whatever order the sessions were registered and used in.</summary>
    [Fact]
    public void TheSnapshotsBytesAreCanonicalWithSessions()
    {
        var a = new KvStateMachine();
        Apply(a, 3, "Register|");
        Apply(a, 9, "Register|");
        Apply(a, 10, "Session|9|1|Put|y|1");
        Apply(a, 11, "Session|3|1|Put|x|1");
        var b = new KvStateMachine();
        b.Restore(a.Snapshot());
        var c = new KvStateMachine();
        Apply(c, 9, "Register|");
        Apply(c, 3, "Register|");
        Apply(c, 10, "Session|3|1|Put|x|1");
        Apply(c, 11, "Session|9|1|Put|y|1");

        Assert.Equal(a.Snapshot().ToArray(), c.Snapshot().ToArray());
        Assert.Equal(a.Snapshot().ToArray(), b.Snapshot().ToArray());
    }

    /// <summary>
    /// The reviewer's addition to decision 3, a §10 limit test: exactly <see cref="KvStateMachine.MaxSessions"/>
    /// sessions register, each applies one command and keeps its reply, the snapshot round-trips, and
    /// one more registration is refused. The snapshot's size is written beside the assembly, per
    /// session and at the bound, for phase 9.
    /// </summary>
    [Fact]
    public void TheLargestSessionCountSnapshotsAndRestoresAndOneMoreIsRefused()
    {
        var watch = Stopwatch.StartNew();
        var kv = new KvStateMachine();
        var empty = kv.Snapshot().Length;
        for (var i = 1L; i <= KvStateMachine.MaxSessions; i++)
        {
            Apply(kv, i, "Register|");
            Apply(kv, KvStateMachine.MaxSessions + i, "Session|" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|1|Cas|k|-|v");
        }

        Assert.Equal("sessions-full|", Apply(kv, (3L * KvStateMachine.MaxSessions) + 1, "Register|"));
        var snapshot = kv.Snapshot();
        var restored = new KvStateMachine();
        restored.Restore(snapshot);
        Assert.Equal(KvStateMachine.MaxSessions, restored.Sessions);
        Assert.Equal(snapshot.ToArray(), restored.Snapshot().ToArray());
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "session-limit.txt"), FormattableString.Invariant(
            $"{KvStateMachine.MaxSessions} sessions, each holding a Cas reply (\"ok|true\" or \"ok|false\"): snapshot {snapshot.Length} bytes ({empty} with none), {(snapshot.Length - empty) / (double)KvStateMachine.MaxSessions:F1} bytes per session; {watch.Elapsed.TotalSeconds:F1} s\n"));
    }
}
