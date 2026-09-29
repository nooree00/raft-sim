using System;
using System.IO;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// Every CI script that runs git or the gates marks its checkout safe for git itself. In GitHub's
/// SDK container the checkout belongs to another user; a job that did not run the preflight
/// first had no safe.directory and failed on GitHub only (P2-01's first push, docs/findings.md).
/// The local container runs as the checkout's owner, so it cannot show this; the rule is checked
/// here instead. Sabotage S-ci-2.
/// </summary>
public sealed class CiScriptTests
{
    [Fact]
    public void EveryCiScriptThatRunsGitOrTheGatesMarksTheCheckoutSafe()
    {
        var dir = Repo.Locate(null).PathOf("scripts");
        var scripts = Directory.GetFiles(dir, "ci-*.sh").Order(StringComparer.Ordinal).ToList();
        Assert.True(scripts.Count >= 8, $"only {scripts.Count} CI scripts");

        var missing = scripts.Where(s =>
        {
            var text = File.ReadAllText(s);
            var usesGit = text.Contains("Raft.Gates.dll", StringComparison.Ordinal) || text.Contains("git ", StringComparison.Ordinal);
            return usesGit && !text.Contains("safe.directory", StringComparison.Ordinal);
        }).Select(Path.GetFileName).ToList();

        Assert.True(missing.Count == 0, "CI scripts that run git without marking the checkout safe: " + string.Join(", ", missing));
    }

    /// <summary>
    /// P3-08: the soak runs 10,000 executions and refuses a report that covers fewer. Spec §12: it is
    /// never shrunk for time; changing the number is a change to this test and to the spec. Sabotage
    /// S-soak-2.
    /// </summary>
    [Fact]
    public void TheSoakRunsTenThousandExecutionsAndChecksItsReportCoversThem()
    {
        var text = File.ReadAllText(Repo.Locate(null).PathOf("scripts/ci-soak.sh"));

        Assert.True(text.Contains("executions=10000", StringComparison.Ordinal), "scripts/ci-soak.sh no longer runs 10,000 executions");
        Assert.True(text.Contains("RAFT_SOAK_COUNT=$executions", StringComparison.Ordinal) && text.Contains("^$executions executions ", StringComparison.Ordinal),
            "scripts/ci-soak.sh no longer passes the count to the test or checks that its report covers it");
    }
}
