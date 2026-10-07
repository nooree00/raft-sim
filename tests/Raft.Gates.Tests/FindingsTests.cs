using System;
using System.IO;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>P10-00: a gate's failures reach GitHub's annotations, as one, newlines encoded; off GitHub, nothing extra is printed.</summary>
public sealed class FindingsTests
{
    [Fact]
    public void OnGitHubTheFailuresAreOneAnnotation()
    {
        var f = new Findings();
        f.Fail("S-a-1: took 95.0s against 20.0s recorded\nsecond line 100%");
        var stdout = new StringWriter();

        Assert.Equal(1, f.Report("sabotage", stdout, new StringWriter(), github: true));
        Assert.Contains("::error::sabotage: 1 failure(s)%0AS-a-1: took 95.0s against 20.0s recorded%0Asecond line 100%25", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OffGitHubNoAnnotationIsPrinted()
    {
        var f = new Findings();
        f.Fail("x");
        var stdout = new StringWriter();

        f.Report("sabotage", stdout, new StringWriter(), github: false);

        Assert.DoesNotContain("::error::", stdout.ToString(), StringComparison.Ordinal);
    }
}
