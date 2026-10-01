using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Checker;
using Raft.Simulation;
using Xunit;
using static Raft.Core.Tests.ManualCluster;

namespace Raft.Core.Tests;

/// <summary>
/// P5-02: three exact executions whose histories hold an indeterminate operation, each accepted by
/// the WGL checker and the brute-force oracle, and the naive treatments of spec §6 measured on the
/// same histories. Vacuity risk: a construction whose steps do not happen as intended (the write
/// never commits, the truncation never happens) passes without testing anything. Guarded: each step
/// is asserted by what the disks and the client log hold. Sabotages S-indet-1, S-indet-2.
/// </summary>
public sealed class IndeterminateConstructionTests
{
    /// <summary>n1 leads term 1 and its no-op is committed everywhere.</summary>
    private static ManualCluster Elected()
    {
        var c = new ManualCluster(RaftOptions.Default);
        c.Stand(N1);
        c.Settle(All);
        c.Tick(N1, 50);
        c.Settle(All);
        return c;
    }

    /// <summary>
    /// n2 wins an election with n3, commits its no-op, and tells n3. n3 first waits out the old
    /// leader (a voter that heard from a leader recently ignores a vote request, P3-06), and may
    /// stand itself meanwhile, so n2 stands again until it wins.
    /// </summary>
    private static void N2Leads(ManualCluster c, params NodeId[] among)
    {
        var term = c.Stand(N2, above: 1);
        foreach (var voter in among.Where(n => n != N2))
        {
            c.Tick(voter, 200);
        }

        c.Settle(among);
        for (var attempt = 0; attempt < 3 && c.RoleOf(N2) != Role.Leader; attempt++)
        {
            term = c.Stand(N2, above: term);
            c.Settle(among);
        }

        c.Tick(N2, 50);
        c.Settle(among);
        Assert.Equal(Role.Leader, c.RoleOf(N2));
    }

    private static void Request(ManualCluster c, NodeId leader, string command, int client, params NodeId[] among)
    {
        c.Client(leader, command, client);
        c.Settle(among);
        c.Tick(leader, 50);
        c.Settle(among);
    }

    private static int Copies(ManualCluster c, NodeId n, string command) =>
        c.EntriesOf(n).Count(e => Encoding.ASCII.GetString(e.Command) == command);

    private static string ReplyTo(ManualCluster c, int client, string command) =>
        Encoding.ASCII.GetString(c.ClientLog().Last(o => o.Client == client && Encoding.ASCII.GetString(o.Request.Span) == command).Reply.Span);

    /// <summary>(a) A write reaches n2 only; its leader crashes, unanswered; n2 leads and commits it; a read sees it.</summary>
    internal static (ManualCluster Cluster, long GaveUp) TimedOutThenCommitted()
    {
        var c = Elected();
        c.Client(N1, "Put|k|a", client: 0);
        c.Deliver(N1, N2);
        var gaveUp = c.Now;
        c.Crash(N1);
        N2Leads(c, N2, N3);
        Request(c, N2, "Get|k", 1, N2, N3);

        Assert.Equal(1, Copies(c, N3, "Put|k|a"));
        Assert.Null(c.ClientLog()[0].Response);
        Assert.Equal("ok|a", ReplyTo(c, 1, "Get|k"));
        return (c, gaveUp);
    }

    /// <summary>(b) A write reaches no one; its leader crashes, unanswered; n2 leads; n1 returns and truncates it; a read does not see it.</summary>
    internal static (ManualCluster Cluster, long GaveUp) TimedOutThenTruncated()
    {
        var c = Elected();
        c.Client(N1, "Put|k|a", client: 0);
        c.Drop(N1);
        Assert.Equal(1, Copies(c, N1, "Put|k|a"));
        var gaveUp = c.Now;
        c.Crash(N1);
        N2Leads(c, N2, N3);
        c.Restart(N1);
        c.Tick(N2, 50);
        c.Settle(All);
        Request(c, N2, "Get|k", 1, All);

        Assert.Equal(0, Copies(c, N1, "Put|k|a"));
        Assert.Null(c.ClientLog()[0].Response);
        Assert.Equal("ok|-", ReplyTo(c, 1, "Get|k"));
        return (c, gaveUp);
    }

    /// <summary>(c) An Append reaches n2; its leader crashes, unanswered; n2 commits it; the client retries it to n2; a read sees both.</summary>
    internal static (ManualCluster Cluster, long GaveUp) RetriedAndCommittedTwice()
    {
        var c = Elected();
        c.Client(N1, "Append|k|x", client: 0);
        c.Deliver(N1, N2);
        var gaveUp = c.Now;
        c.Crash(N1);
        N2Leads(c, N2, N3);
        Request(c, N2, "Append|k|x", 0, N2, N3);
        Request(c, N2, "Get|k", 1, N2, N3);

        Assert.Equal(2, Copies(c, N3, "Append|k|x"));
        Assert.Equal("ok", ReplyTo(c, 0, "Append|k|x"));
        Assert.Equal("ok|xx", ReplyTo(c, 1, "Get|k"));
        return (c, gaveUp);
    }

    public static TheoryData<string> Constructions => new() { "timed-out-then-committed", "timed-out-then-truncated", "retried-and-committed-twice" };

    private static (ManualCluster Cluster, long GaveUp) Build(string name) => name switch
    {
        "timed-out-then-committed" => TimedOutThenCommitted(),
        "timed-out-then-truncated" => TimedOutThenTruncated(),
        "retried-and-committed-twice" => RetriedAndCommittedTwice(),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(Constructions))]
    public void TheHistoryIsAcceptedByTheCheckerAndTheOracle(string construction)
    {
        var (c, _) = Build(construction);
        var r = ClientHistory.From(c.ClientLog());

        Assert.Equal(1, r.Indeterminate);
        Assert.Empty(History.Problems(r.History));
        Assert.True(WglChecker.Check(r.History).IsLinearizable, string.Join("\n", r.History));
        Assert.True(BruteForceOracle.IsLinearizable(r.History), string.Join("\n", r.History));
    }

    /// <summary>
    /// Spec §6's two wrong treatments, as worded there: "treating it as failed" (it did not take
    /// effect) and "dropping it". For this checker both remove the operation, so they are one
    /// transformation under two names. The third is what a naive recorder does: close the operation
    /// at the moment its client gave up, as if that were its response.
    /// </summary>
    internal static IReadOnlyList<Operation> Treated(IReadOnlyList<Operation> history, string treatment, long gaveUp) => treatment switch
    {
        "as-failed" or "dropped" => history.Where(o => !o.IsIndeterminate).ToList(),
        "closed-at-timeout" => history.Select(o => o.IsIndeterminate ? o with { Response = gaveUp } : o).ToList(),
        _ => throw new ArgumentException(treatment),
    };

    [Theory]
    [InlineData("timed-out-then-committed", "as-failed", false)]
    [InlineData("timed-out-then-committed", "dropped", false)]
    [InlineData("timed-out-then-committed", "closed-at-timeout", true)]
    [InlineData("timed-out-then-truncated", "as-failed", true)]
    [InlineData("timed-out-then-truncated", "dropped", true)]
    [InlineData("timed-out-then-truncated", "closed-at-timeout", false)]
    [InlineData("retried-and-committed-twice", "as-failed", false)]
    [InlineData("retried-and-committed-twice", "dropped", false)]
    [InlineData("retried-and-committed-twice", "closed-at-timeout", true)]
    public void EachNaiveTreatmentsVerdict(string construction, string treatment, bool accepted)
    {
        var (c, gaveUp) = Build(construction);
        var treated = Treated(ClientHistory.From(c.ClientLog()).History, treatment, gaveUp);

        Assert.Equal(accepted, WglChecker.Check(treated).IsLinearizable);
        Assert.Equal(accepted, BruteForceOracle.IsLinearizable(treated));
    }
}
