using System;
using System.IO;
using System.Linq;
using Raft.Gates;
using Xunit;

namespace Raft.Gates.Tests;

/// <summary>
/// The TRX reading behind the test-count floor. Vacuity risk: counting skipped tests as executed
/// lets a project whose every test is skipped pass the floor; so a skipped result is in the fixture.
/// </summary>
public sealed class TestCountTests
{
    private const string Trx = """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testId="a" outcome="Passed" duration="00:00:01" />
            <UnitTestResult testId="b" outcome="Failed" duration="00:00:02" />
            <UnitTestResult testId="c" outcome="NotExecuted" duration="00:00:00" />
            <UnitTestResult testId="d" outcome="Passed" duration="00:00:04" />
          </Results>
          <TestDefinitions>
            <UnitTest id="a" storage="/x/bin/Debug/net10.0/Alpha.Tests.dll" />
            <UnitTest id="b" storage="/x/bin/Debug/net10.0/Alpha.Tests.dll" />
            <UnitTest id="c" storage="/x/bin/Debug/net10.0/Alpha.Tests.dll" />
            <UnitTest id="d" storage="/y/bin/Debug/net10.0/Beta.Tests.dll" />
          </TestDefinitions>
        </TestRun>
        """;

    [Fact]
    public void ResultsAreGroupedByAssemblyAndSkippedTestsAreNotExecuted()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".trx");
        File.WriteAllText(path, Trx);
        try
        {
            var results = TestCount.Parse(path).OrderBy(r => r.Project, StringComparer.Ordinal).ToList();

            Assert.Equal(2, results.Count);
            Assert.Equal(new TestCount.ProjectResult("Alpha.Tests", 2, 1, TimeSpan.FromSeconds(3)), results[0]);
            Assert.Equal(new TestCount.ProjectResult("Beta.Tests", 1, 0, TimeSpan.FromSeconds(4)), results[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private const string Source = """
        namespace Alpha.Tests;

        public sealed class Things
        {
            private sealed class Helper
            {
                public void NotATest() { }
            }

            [Fact]
            public void AFact()
            {
            }

            [Theory]
            [MemberData(nameof(Cases))]
            public void ATheory(string c)
            {
            }

            public void NotMarked() { }
        }
        """;

    /// <summary>P4-11: the written tests of a file are its [Fact] and [Theory] methods, under the top-level class, and nothing else.</summary>
    [Fact]
    public void WrittenTestsAreTheFactsAndTheoriesOfTheTopLevelClass()
    {
        Assert.Equal(["Alpha.Tests.Things.AFact", "Alpha.Tests.Things.ATheory"], TestCount.Written(Source));

        // A fixture's source inside a raw string literal is text, not a test of the file holding it.
        var holder = "namespace H;\npublic sealed class Holder\n{\n    private const string S = \"\"\"\n        [Fact]\n        public void Inner() { }\n        \"\"\";\n}\n";
        Assert.Empty(TestCount.Written(holder));
    }

    /// <summary>
    /// A test written in a project and absent from its results is reported, whatever the counts say;
    /// one written under tests/ outside any project is reported too; a test that ran is not.
    /// </summary>
    [Fact]
    public void AWrittenTestThatDidNotRunIsReportedAndOneThatRanIsNot()
    {
        var ran = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>(StringComparer.Ordinal)
        {
            ["Alpha.Tests"] = new(StringComparer.Ordinal) { "Alpha.Tests.Things.AFact" },
        };
        var problems = TestCount.Unexecuted([("tests/Alpha.Tests/Things.cs", Source), ("tests/Loose/Things.cs", Source)], ["Alpha.Tests"], ran).ToList();

        Assert.Contains("Alpha.Tests: Alpha.Tests.Things.ATheory is written in tests/Alpha.Tests/Things.cs but was not executed", problems);
        Assert.Contains(problems, p => p.StartsWith("Alpha.Tests.Things.AFact is written in tests/Loose/Things.cs, outside any test project", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.StartsWith("Alpha.Tests: Alpha.Tests.Things.AFact", StringComparison.Ordinal));
        Assert.True(problems.Count == 3, "a written test that did not run went unreported: " + string.Join("; ", problems));
    }

    /// <summary>A harness target must have run in the project its entry names; a theory's case counts by its method.</summary>
    [Fact]
    public void AHarnessTargetThatDidNotRunInItsNamedProjectIsReported()
    {
        var ran = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>(StringComparer.Ordinal)
        {
            ["Alpha.Tests"] = new(StringComparer.Ordinal) { "Alpha.Tests.Things.ATheory" },
            ["Beta.Tests"] = new(StringComparer.Ordinal) { "Alpha.Tests.Things.AFact" },
        };
        var problems = TestCount.TargetsNotExecuted(
            [("S-a-1", "tests/Alpha.Tests", "Alpha.Tests.Things.ATheory(c: \"x\")"), ("S-a-2", "tests/Alpha.Tests", "Alpha.Tests.Things.AFact")], ran).ToList();

        Assert.Equal(["S-a-2: target Alpha.Tests.Things.AFact was not executed in Alpha.Tests, the project the entry names"], problems);
    }

    /// <summary>P5-07: a run over named projects covers those; a name that is not a project fails, or a typo narrows the run to nothing. Sabotage S-count-4.</summary>
    [Fact]
    public void ANamedProjectThatIsNotATestProjectIsAProblem()
    {
        string[] all = ["Alpha.Tests", "Beta.Tests"];

        Assert.Equal(all, TestCount.Scope(all, null).Expected);
        Assert.Empty(TestCount.Scope(all, null).Problems);
        Assert.Equal(["Beta.Tests"], TestCount.Scope(all, "Beta.Tests").Expected);
        Assert.Empty(TestCount.Scope(all, "Beta.Tests").Problems);
        Assert.Equal(["--projects: Gamma.Tests is not a test project under tests/"], TestCount.Scope(all, "Alpha.Tests,Gamma.Tests").Problems);
        Assert.Equal(["--projects names no project"], TestCount.Scope(all, "").Problems);
    }
}
