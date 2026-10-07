using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Raft.Host;

/// <summary>
/// A measurement as a record (P10-01, phase 10 decision 5): an id, the task it serves, what was
/// measured, its configuration, and its results. The configuration is filled in by
/// <see cref="Measurement.Config"/>, not typed: `gates measurements` refuses a record missing any
/// field, so a number without its build configuration cannot reach a report.
/// </summary>
public sealed record MeasurementRecord(string Id, string Task, string Measure, IReadOnlyDictionary<string, string> Config, IReadOnlyDictionary<string, double> Results);

/// <summary>What a measurement ran on, read from the process and the machine, and the record's file.</summary>
public static class Measurement
{
    /// <summary>
    /// The configuration beside every number. Three fields come from the script that starts the
    /// tool, because the process cannot see them: the commit (RAFT_COMMIT), the SDK that built it
    /// (RAFT_SDK) and the image it runs in (RAFT_IMAGE); unset, they stay empty and the gate refuses
    /// the record. The CPU is recorded three ways, because they differ in a container: the machine's
    /// processors, the cgroup's quota, and the count .NET reports (which rounds the quota up).
    /// </summary>
    public static Dictionary<string, string> Config(string warmup, string repetition, string load, string dataDirectory, Func<string, string?>? env = null, string procRoot = "/proc", string cgroupRoot = "/sys/fs/cgroup")
    {
        env ??= Environment.GetEnvironmentVariable;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["commit"] = env("RAFT_COMMIT") ?? "",
            ["configuration"] = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
                ?? typeof(Measurement).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "",
            ["sdk"] = env("RAFT_SDK") ?? "",
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["image"] = env("RAFT_IMAGE") ?? "",
            ["os"] = RuntimeInformation.OSDescription,
            ["kernel"] = ReadOr(Path.Combine(procRoot, "sys/kernel/osrelease"), "").Trim(),
            ["cpuModel"] = CpuModel(ReadOr(Path.Combine(procRoot, "cpuinfo"), "")),
            ["cpuCount"] = CpuCount(ReadOr(Path.Combine(procRoot, "cpuinfo"), "")),
            ["cpuLimit"] = CpuLimit(ReadOr(Path.Combine(cgroupRoot, "cpu.max"), ""), ReadOr(Path.Combine(cgroupRoot, "cpu/cpu.cfs_quota_us"), ""), ReadOr(Path.Combine(cgroupRoot, "cpu/cpu.cfs_period_us"), "")),
            ["dotnetProcessorCount"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            ["gc"] = (GCSettings.IsServerGC ? "server" : "workstation") + (AppContext.GetData("System.GC.Concurrent") is false ? ", non-concurrent" : ", concurrent"),
            ["tieredCompilation"] = Switch("System.Runtime.TieredCompilation", env("DOTNET_TieredCompilation")),
            ["tieredPgo"] = Switch("System.Runtime.TieredPGO", env("DOTNET_TieredPGO")),
            ["dataFileSystem"] = FileSystemOf(dataDirectory, ReadOr(Path.Combine(procRoot, "mounts"), "")),
            ["warmup"] = warmup,
            ["repetition"] = repetition,
            ["load"] = load,
        };
    }

    /// <summary>
    /// The CPU quota as processors: cgroup v2's cpu.max ("150000 100000" is 1.5, "max" is none), else
    /// cgroup v1's quota and period (a quota of -1 is none); empty if neither is readable.
    /// </summary>
    public static string CpuLimit(string cpuMax, string v1Quota = "", string v1Period = "")
    {
        var parts = cpuMax.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            var v1 = v1Quota.Trim();
            if (v1 == "-1")
            {
                return "none";
            }

            parts = v1.Length > 0 && v1Period.Trim().Length > 0 ? [v1, v1Period.Trim()] : [];
            if (parts.Length != 2)
            {
                return "";
            }
        }

        if (parts[0] == "max")
        {
            return "none";
        }

        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var quota) && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var period) && period > 0
            ? (quota / period).ToString("0.##", CultureInfo.InvariantCulture)
            : "";
    }

    /// <summary>The file system holding the directory: the longest mount point that prefixes it, as "type (mount point)".</summary>
    public static string FileSystemOf(string directory, string mounts)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var full = Path.GetFullPath(directory);
        var best = mounts.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' '))
            .Where(p => p.Length >= 3 && (full == p[1] || full.StartsWith(p[1].TrimEnd('/') + "/", StringComparison.Ordinal) || p[1] == "/"))
            .OrderByDescending(p => p[1].Length)
            .FirstOrDefault();
        return best is null ? "" : $"{best[2]} ({best[1]})";
    }

    public static void Write(string directory, MeasurementRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new
        {
            id = record.Id,
            task = record.Task,
            measure = record.Measure,
            config = record.Config,
            results = record.Results,
        }, Indented);
        File.WriteAllText(Path.Combine(directory, record.Id + ".json"), json + "\n");
    }

    /// <summary>The p-th percentile (0 to 100) of sorted samples, nearest rank: a value that was measured, not interpolated.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        // The tolerance: 99.9 / 100 * 1000 is 999.0000000000001 in floating point, and its ceiling is
        // the 1000th sample, not the 999th the nearest rank names.
        var rank = (int)Math.Ceiling((p * sorted.Count / 100) - 1e-9);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string CpuModel(string cpuinfo) =>
        cpuinfo.Split('\n').FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal)) is { } line ? line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim() : "";

    private static string CpuCount(string cpuinfo)
    {
        var n = cpuinfo.Split('\n').Count(l => l.StartsWith("processor", StringComparison.Ordinal));
        return n == 0 ? "" : n.ToString(CultureInfo.InvariantCulture);
    }

    private static string Switch(string name, string? env) =>
        AppContext.GetData(name) is { } value ? value.ToString() ?? "" : env is { Length: > 0 } ? "env " + env : "default (on)";

    private static string ReadOr(string path, string fallback)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }
}
