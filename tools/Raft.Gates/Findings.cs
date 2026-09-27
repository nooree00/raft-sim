using System.Collections.Generic;
using System.IO;

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

    public int Report(string gate, TextWriter stdout, TextWriter stderr)
    {
        foreach (var n in _notes)
        {
            stdout.WriteLine($"[{gate}] {n}");
        }

        foreach (var f in _failures)
        {
            stderr.WriteLine($"[{gate}] FAIL: {f}");
        }

        stdout.WriteLine(_failures.Count == 0 ? $"[{gate}] ok" : $"[{gate}] {_failures.Count} failure(s)");
        return _failures.Count == 0 ? 0 : 1;
    }
}
