using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Raft.Gates;

/// <summary>
/// Spec §12: "a phase report certifies the commit that contains it". `verify-run --sha` asks the
/// GitHub API for the ci workflow's runs *for that exact commit* and requires the latest to have
/// completed successfully with every required job successful — a skipped job is not a pass.
/// `reports` does this for the commit that last changed each docs/phases/*/report.md. A report
/// committed at HEAD cannot be verified by its own, still-running workflow; it is verified by the
/// first run after it, and a red one then fails every later run until a new report replaces it.
/// Vacuity risks: a query that matches zero runs and loops over nothing (guarded: zero runs
/// fails); accepting a run for another commit (guarded: runs are fetched by head_sha and each is
/// checked against it).
/// </summary>
internal static class VerifyRun
{
    public static readonly string[] RequiredJobs = ["build", "each-commit", "secrets", "readme-walk"];

    /// <summary>
    /// The soak is required in its own right from the phase-3 report on (P3 acceptance): through
    /// `build` alone, moving it out of `build` would drop the requirement silently. Earlier reports
    /// certify runs from before the job existed (P3-08), so they keep the four jobs.
    /// </summary>
    public const int SoakRequiredFrom = 3;

    /// <summary>The membership soak, a soak of its own (P6-12), is required likewise from the phase-6 report on.</summary>
    public const int MembershipSoakRequiredFrom = 6;

    internal static IReadOnlyList<string> RequiredFor(int phase) =>
        phase >= MembershipSoakRequiredFrom ? [.. RequiredJobs, "soak", "soak-membership"]
        : phase >= SoakRequiredFrom ? RequiredJobs.Append("soak").ToList()
        : RequiredJobs;

    public static Findings RunOne(Repo repo, string[] args)
    {
        var rest = args.ToList();
        var sha = Options.Take(rest, "--sha") ?? throw new ArgumentException("--sha is required");
        var f = new Findings();
        Verify(Api.FromEnvironment(rest), sha, f, RequiredFor(int.MaxValue));
        return f;
    }

    public static Findings RunReports(Repo repo, string[] args)
    {
        var f = new Findings();
        var dir = repo.PathOf("docs/phases");
        var reports = Directory.Exists(dir)
            ? Directory.GetDirectories(dir, "P*").Select(d => Path.Combine(d, "report.md")).Where(File.Exists).Order(StringComparer.Ordinal).ToList()
            : [];
        if (reports.Count == 0)
        {
            f.Note("no phase reports yet");
            return f;
        }

        var head = repo.Git("rev-parse", "HEAD").StdOut.Trim();
        var api = Api.FromEnvironment(args.ToList());
        foreach (var report in reports)
        {
            var rel = Path.GetRelativePath(repo.Root, report);
            var sha = repo.Git("log", "-1", "--format=%H", "--", rel).StdOut.Trim();
            if (sha == head)
            {
                f.Note($"{rel}: certifies HEAD {sha[..7]}, which this run is still building; verified by the next run");
                continue;
            }

            var phase = int.Parse(Path.GetFileName(Path.GetDirectoryName(report)!)[1..], System.Globalization.CultureInfo.InvariantCulture);
            f.Note($"{rel}: certifies {sha[..7]}");
            Verify(api, sha, f, RequiredFor(phase));
        }

        return f;
    }

    internal static void Verify(Api api, string sha, Findings f, IReadOnlyList<string> required)
    {
        var runs = api.Get($"actions/runs?head_sha={sha}&per_page=100");
        var jobsFor = new Func<long, string>(id => api.Get($"actions/runs/{id}/jobs?per_page=100"));
        Evaluate(sha, runs, jobsFor, f, required);
    }

    /// <summary>The decision, separated from HTTP so it can be tested on recorded responses.</summary>
    internal static void Evaluate(string sha, string runsJson, Func<long, string> jobsJson, Findings f, IReadOnlyList<string> required)
    {
        using var runs = JsonDocument.Parse(runsJson);
        var ci = runs.RootElement.GetProperty("workflow_runs").EnumerateArray()
            .Where(r => r.GetProperty("name").GetString() == "ci")
            .ToList();
        if (ci.Count == 0)
        {
            f.Fail($"{sha[..7]}: no ci workflow run for this commit");
            return;
        }

        foreach (var r in ci)
        {
            if (r.GetProperty("head_sha").GetString() != sha)
            {
                f.Fail($"{sha[..7]}: the API returned run {r.GetProperty("id").GetInt64()} for another commit");
                return;
            }
        }

        var latest = ci.OrderByDescending(r => r.GetProperty("run_number").GetInt64())
            .ThenByDescending(r => r.GetProperty("run_attempt").GetInt64()).First();
        var id = latest.GetProperty("id").GetInt64();
        var status = latest.GetProperty("status").GetString();
        var conclusion = latest.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (status != "completed" || conclusion != "success")
        {
            f.Fail($"{sha[..7]}: run {id} is {status}/{conclusion ?? "none"}");
            return;
        }

        using var jobs = JsonDocument.Parse(jobsJson(id));
        var byName = jobs.RootElement.GetProperty("jobs").EnumerateArray()
            .ToDictionary(j => j.GetProperty("name").GetString()!, j => j, StringComparer.Ordinal);
        foreach (var name in required)
        {
            if (!byName.TryGetValue(name, out var job))
            {
                f.Fail($"{sha[..7]}: run {id} has no job '{name}'");
                continue;
            }

            var jc = job.TryGetProperty("conclusion", out var jcv) && jcv.ValueKind == JsonValueKind.String ? jcv.GetString() : null;
            f.Require(jc == "success", $"{sha[..7]}: run {id} job '{name}' concluded {jc ?? "none"}");
            if (jc == "success" && job.TryGetProperty("started_at", out var s) && job.TryGetProperty("completed_at", out var e)
                && s.ValueKind == JsonValueKind.String && e.ValueKind == JsonValueKind.String)
            {
                f.Note($"{sha[..7]}: run {id} job {name}: {(e.GetDateTimeOffset() - s.GetDateTimeOffset()).TotalSeconds:F0}s");
            }
        }
    }
}

/// <summary>A minimal GitHub REST client: GITHUB_TOKEN and GITHUB_REPOSITORY from the environment.</summary>
internal sealed class Api
{
    private readonly HttpClient _http;
    private readonly string _repo;

    private Api(HttpClient http, string repo)
    {
        _http = http;
        _repo = repo;
    }

    public static Api FromEnvironment(List<string> args)
    {
        var repo = Options.Take(args, "--repo") ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")
            ?? throw new InvalidOperationException("GITHUB_REPOSITORY (or --repo) is required");
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? throw new InvalidOperationException("GITHUB_TOKEN is required");
        var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("raft-sim-gates", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return new Api(http, repo);
    }

    public string Get(string path)
    {
        using var response = _http.GetAsync(new Uri($"repos/{_repo}/{path}", UriKind.Relative)).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GET {path}: {(int)response.StatusCode} {response.ReasonPhrase}: {body[..Math.Min(300, body.Length)]}");
        }

        return body;
    }
}
