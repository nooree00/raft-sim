using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Raft.Gates;

/// <summary>What a gate found. A gate that checked nothing must say so as a failure, not pass.</summary>
internal sealed class Findings
{
    private readonly List<string> _failures = [];
    private readonly List<string> _notes = [];

    public IReadOnlyList<string> Failures => _failures;

    public IReadOnlyList<string> Notes => _notes;

    public void Fail(string message) => _failures.Add(message);

    public void Note(string message) => _notes.Add(message);

    public void Require(bool condition, string messageIfFalse)
    {
        if (!condition)
        {
            Fail(messageIfFalse);
        }
    }

    public int Report(string gate, TextWriter stdout, TextWriter stderr, bool? github = null)
    {
        foreach (var n in _notes)
        {
            stdout.WriteLine($"[{gate}] {n}");
        }

        foreach (var f in _failures)
        {
            stderr.WriteLine($"[{gate}] FAIL: {f}");
        }

        // On GitHub, the failures as one annotation, newlines encoded: the job log is not always
        // readable where a failure is diagnosed, and the annotations are (P10: shard 5's failure
        // reached them as "exit code 1" and nothing else).
        if (_failures.Count > 0 && (github ?? Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"))
        {
            var text = string.Join("\n", _failures.Take(20).Select(f => f.Length > 600 ? f[..600] + "…" : f));
            stdout.WriteLine($"::error::{gate}: {_failures.Count} failure(s)%0A" + text.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal));
        }

        stdout.WriteLine(_failures.Count == 0 ? $"[{gate}] ok" : $"[{gate}] {_failures.Count} failure(s)");
        return _failures.Count == 0 ? 0 : 1;
    }
}
