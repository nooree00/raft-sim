using System;
using System.IO;
using Raft.Host;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P10-01: the configuration a record carries is read from the machine, not typed, and three values
/// the process cannot see come from the starting script or stay empty for the gate to refuse.
/// </summary>
public sealed class MeasurementTests
{
    [Theory]
    [InlineData("max 100000", "none")]
    [InlineData("150000 100000", "1.5")]
    [InlineData("120000 100000\n", "1.2")]
    [InlineData("", "")]
    public void TheCgroupQuotaIsReadAsProcessors(string cpuMax, string expected) => Assert.Equal(expected, Measurement.CpuLimit(cpuMax));

    /// <summary>This container's machine has cgroup v1 (no cpu.max): the quota is read from its cpu controller instead.</summary>
    [Theory]
    [InlineData("-1\n", "100000\n", "none")]
    [InlineData("150000", "100000", "1.5")]
    [InlineData("", "", "")]
    public void ACgroupV1QuotaIsReadWhenThereIsNoCpuMax(string quota, string period, string expected) => Assert.Equal(expected, Measurement.CpuLimit("", quota, period));

    [Fact]
    public void TheDataDirectorysFileSystemIsItsLongestMountPoint()
    {
        const string mounts = "overlay / overlay rw 0 0\n/dev/vdb /data ext4 rw 0 0\ntmpfs /dev tmpfs rw 0 0\n";

        Assert.Equal("ext4 (/data)", Measurement.FileSystemOf("/data/n1", mounts));
        Assert.Equal("overlay (/)", Measurement.FileSystemOf("/tmp/x", mounts));
    }

    [Fact]
    public void APercentileIsAMeasuredValueByNearestRank()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        Assert.Equal(5, Measurement.Percentile(sorted, 50));
        Assert.Equal(10, Measurement.Percentile(sorted, 99));
        Assert.Equal(1, Measurement.Percentile(sorted, 0));
    }

    [Fact]
    public void WhatTheProcessCannotSeeStaysEmptyUnlessTheScriptSetsIt()
    {
        var bare = Measurement.Config("none", "1", "none", Path.GetTempPath(), _ => null);
        var set = Measurement.Config("none", "1", "none", Path.GetTempPath(), n => n == "RAFT_COMMIT" ? "abc123" : null);

        Assert.Equal("", bare["commit"]);
        Assert.Equal("", bare["image"]);
        Assert.Equal("abc123", set["commit"]);
        Assert.False(string.IsNullOrEmpty(bare["configuration"]));
        Assert.False(string.IsNullOrEmpty(bare["runtime"]));
    }
}
