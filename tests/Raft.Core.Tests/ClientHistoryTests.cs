using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Checker;
using Raft.Simulation;
using Xunit;

namespace Raft.Core.Tests;

/// <summary>
/// P5-01: each adapter rule on a hand-built client log, with the history the rule produces
/// accepted and its twin one step away (the rule broken) rejected by the WGL checker, or failing
/// the history's structural checks.
/// </summary>
public sealed class ClientHistoryTests
{
    private static ClientOp Op(int client, long id, string request, long invoke, long? response, string reply = "") =>
        new(client, id, new NodeId(1), Encoding.ASCII.GetBytes(request), invoke, response, Encoding.ASCII.GetBytes(reply));

    [Fact]
    public void ARefusedOperationIsLeftOutAndCounted()
    {
        var log = new[]
        {
            Op(0, 1, "Put|k|a", 0, 5, "redirect|2"),
            Op(0, 2, "Put|k|b", 10, 15, "too-large|1048576"),
            Op(0, 3, "Get|k", 20, 25, "ok|-"),
        };
        var r = ClientHistory.From(log);

        Assert.Equal((1, 0, 2), (r.Completed, r.Indeterminate, r.Refused));
        Assert.Equal(OpKind.Get, Assert.Single(r.History).Kind);
        Assert.True(WglChecker.Check(r.History).IsLinearizable);

        // The twin: the refused Put mapped as a completed write, and the Get after it that saw nothing is unexplainable.
        var twin = r.History.Prepend(new Operation(0, OpKind.Put, "k", 0, 5, Value: "a")).ToList();
        Assert.False(WglChecker.Check(twin).IsLinearizable);
    }

    [Fact]
    public void ATimedOutOperationIsIndeterminateAndMayTakeEffectAfterItsTimeout()
    {
        var log = new[]
        {
            Op(0, 1, "Put|k|a", 0, null),
            Op(1, 1, "Get|k", 50, 55, "ok|a"),
        };
        var r = ClientHistory.From(log);

        Assert.Equal((1, 1, 0), (r.Completed, r.Indeterminate, r.Refused));
        Assert.True(r.History[0].IsIndeterminate);
        Assert.True(WglChecker.Check(r.History).IsLinearizable);

        // The twin: the indeterminate write dropped, and the read of its value has no write to explain it.
        Assert.False(WglChecker.Check(r.History.Where(o => !o.IsIndeterminate).ToList()).IsLinearizable);
    }

    [Fact]
    public void AfterATimeoutTheClientsNextOperationBelongsToAFreshLogicalClient()
    {
        var log = new[]
        {
            Op(0, 1, "Put|k|a", 0, null),
            Op(0, 2, "Get|k", 30, 35, "ok|a"),
            Op(0, 3, "Get|k", 40, 45, "ok|a"),
        };
        var r = ClientHistory.From(log);

        Assert.Equal([0, ClientHistory.Generation, ClientHistory.Generation], r.History.Select(o => o.Client));
        Assert.Empty(History.Problems(r.History));

        // The twin: the same logical client, now with an operation that never ends overlapping its next one.
        var same = ClientHistory.From(log, freshClientAfterTimeout: false);
        Assert.Contains(History.Problems(same.History), p => p.Contains("client 0 has overlapping operations", StringComparison.Ordinal));
    }

    [Fact]
    public void ARetryIsASecondOperationAndBothMayTakeEffect()
    {
        var log = new[]
        {
            Op(0, 1, "Append|k|x", 0, null),
            Op(0, 2, "Append|k|x", 20, 30, "ok"),
            Op(0, 3, "Get|k", 40, 45, "ok|xx"),
        };
        var r = ClientHistory.From(log);

        Assert.Equal(3, r.History.Count);
        Assert.True(WglChecker.Check(r.History).IsLinearizable);

        // The twin: the retry folded into its original, one Append for two copies of its value.
        Assert.False(WglChecker.Check(r.History.Skip(1).ToList()).IsLinearizable);
    }

    [Fact]
    public void OutputsAreReadFromTheReply()
    {
        var log = new[]
        {
            Op(0, 1, "Cas|k|-|a", 0, 5, "ok|true"),
            Op(0, 2, "Cas|k|-|b", 10, 15, "ok|false"),
            Op(0, 3, "Get|k", 20, 25, "ok|a"),
            Op(0, 4, "Delete|k", 30, 35, "ok"),
            Op(0, 5, "Get|k", 40, 45, "ok|-"),
        };
        var r = ClientHistory.From(log);

        Assert.Equal(new string?[] { "true", "false", "a", null, null }, r.History.Select(o => o.Output));
        Assert.Empty(History.Problems(r.History));
        Assert.True(WglChecker.Check(r.History).IsLinearizable);
        Assert.Equal([OpKind.Get, OpKind.CompareAndSwap, OpKind.Delete], ClientHistory.Kinds(r.History).Order());
    }

    [Fact]
    public void EveryOperationIsAccountedForAndAnUnknownReplyIsReported()
    {
        var log = new[]
        {
            Op(0, 1, "Put|k|a", 0, 5, "ok"),
            Op(0, 2, "Put|k|b", 10, null),
            Op(1, 1, "Put|k|c", 0, 5, "redirect|"),
            Op(1, 2, "Put|k|d", 10, 15, "error"),
        };
        var r = ClientHistory.From(log);

        Assert.Equal(log.Length, r.Accounted);
        Assert.Equal(r.Completed + r.Indeterminate, r.History.Count);
        Assert.Equal("client 1 request 2 'Put|k|d': reply 'error'", Assert.Single(r.Unexplained));
    }

    /// <summary>
    /// P6-08: membership requests are counted apart from the key-value history, and `busy|` is a
    /// definite failure (P6 decision 4, the reviewer's condition): counted, not indeterminate, and the
    /// client's next operation stays on the same logical client. Sabotage S-adapt-4 (`busy|` taken as
    /// an operation that timed out).
    /// </summary>
    [Fact]
    public void AMembershipRequestIsCountedApartAndBusyIsADefiniteFailure()
    {
        var log = new[]
        {
            Op(0, 1, "Member|1,2,4", 0, 5, "ok"),
            Op(0, 2, "Member|1,2,5", 10, 15, "busy|"),
            Op(0, 3, "Put|k|a", 20, 25, "ok"),
            Op(1, 1, "Get|k", 30, 35, "ok|a"),
        };
        var r = ClientHistory.From(log);

        Assert.Equal((2, 2, 1, 0, 0), (r.Completed, r.Membership, r.Busy, r.Indeterminate, r.Refused));
        Assert.Equal(log.Length, r.Accounted);
        Assert.Equal([0, 1], r.History.Select(o => o.Client));
        Assert.True(WglChecker.Check(r.History).IsLinearizable);
    }

    /// <summary>
    /// P8-00, the known-bad history phase 8 must reject: a retried `Append` applied twice. The first
    /// attempt times out, the retry is answered, and a later read sees the value twice. One operation
    /// with two effects: no ordering explains it. The same log with the retry under a new sequence
    /// number (two operations, the client's mistake) is accepted, which is why the merge is needed.
    /// Sabotage S-adapt-5.
    /// </summary>
    [Fact]
    public void ARetriedAppendAppliedTwiceIsRejected()
    {
        ClientOp[] Log(long retrySequence) =>
        [
            Op(0, 1, "Register|", 0, 5, "ok|7"),
            Op(0, 2, "Session|7|1|Append|x|v", 10, null),
            Op(0, 3, $"Session|7|{retrySequence}|Append|x|v", 20, 30, "ok"),
            Op(1, 4, "Get|x", 40, 50, "ok|vv"),
        ];

        var merged = ClientHistory.From(Log(1));
        Assert.Equal((2, 0, 1, 1), (merged.Completed, merged.Indeterminate, merged.Registrations, merged.Retries));
        Assert.Equal(merged.Accounted, Log(1).Length);
        var append = merged.History.Single(o => o.Kind == OpKind.Append);
        Assert.Equal((10L, (long?)30), (append.Invoke, append.Response));
        Assert.False(WglChecker.Check(merged.History).IsLinearizable, "a retry applied twice was accepted");

        var separate = ClientHistory.From(Log(2));
        Assert.Equal((2, 1), (separate.Completed, separate.Indeterminate));
        Assert.True(WglChecker.Check(separate.History).IsLinearizable);
    }

    /// <summary>
    /// The vacuity guard: two distinct operations with the same command in different sessions are two
    /// operations, each with its own effect, never merged. A merge keyed on the command would make this
    /// correct history unexplainable.
    /// </summary>
    [Fact]
    public void TheSameCommandInTwoSessionsIsTwoOperations()
    {
        var log = new[]
        {
            Op(0, 1, "Session|7|1|Append|x|v", 10, 20, "ok"),
            Op(1, 2, "Session|8|1|Append|x|v", 12, 22, "ok"),
            Op(2, 3, "Get|x", 40, 50, "ok|vv"),
        };
        var r = ClientHistory.From(log);

        Assert.Equal(2, r.History.Count(o => o.Kind == OpKind.Append));
        Assert.True(WglChecker.Check(r.History).IsLinearizable);
    }
}

