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
}
