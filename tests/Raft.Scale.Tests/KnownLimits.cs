using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Raft.Checker;

namespace Raft.Scale.Tests;

/// <summary>A recorded known limit: one soak execution whose linearizability the checker cannot decide within the budget.</summary>
public sealed record KnownLimit(string Id, int Seed, string Key, string Digest, string RegisterRow, string ApprovedIn);

/// <summary>
/// P5-05, spec §6 "Recorded known limits". A history the checker cannot decide fails the soak (P5
/// decision 3: no declaration, no floor). A recorded known limit is the one measured exception: a
/// single execution named by its seed, the key whose search exhausts the budget, and a SHA-256 digest
/// of that key's sub-history, so it matches that exact history and nothing else; any change to the
/// workload, the simulator, Raft or the adapter changes the digest. A matching execution is still
/// checked on every run and must still be undecided: an entry whose history changed, or that became
/// decidable, fails the soak until it is removed. It is reported as unverified, never as accepted.
/// The entries live in ci/known-limits.txt; each cites an open register row and the phase report
/// that approved it (`gates register` checks both), and the soak's test names the exact set.
/// Sabotages S-kl-1..3, S-reg-6.
/// </summary>
public static class KnownLimits
{
    public const string FileName = "known-limits.txt";

    /// <summary>The recorded entries, from the copy of ci/known-limits.txt next to the test assembly.</summary>
    public static IReadOnlyList<KnownLimit> Recorded => Parse(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, FileName)));

    /// <summary>Lines "KL-n | seed | key | digest | register: row text | report: path"; '#' starts a comment.</summary>
    public static IReadOnlyList<KnownLimit> Parse(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l =>
        {
            var c = l.Split('|', StringSplitOptions.TrimEntries);
            if (c.Length != 6 || !c[4].StartsWith("register: ", StringComparison.Ordinal) || !c[5].StartsWith("report: ", StringComparison.Ordinal))
            {
                throw new FormatException($"not a known-limit line: '{l}'");
            }

            return new KnownLimit(c[0], int.Parse(c[1], System.Globalization.CultureInfo.InvariantCulture), c[2], c[3], c[4]["register: ".Length..], c[5]["report: ".Length..]);
        }).ToList();

    /// <summary>A key's sub-history, canonically: its operations in history order, one per line.</summary>
    public static string Digest(IEnumerable<Operation> subHistory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", subHistory.Select(o => o.ToString()))))).ToLowerInvariant();

    /// <summary>
    /// One execution against the entries: whether its undecided search is a recorded known limit, and
    /// the failure, if its entry no longer matches (the history changed) or is stale (decided now).
    /// </summary>
    public static (bool Recorded, string? Failure) Judge(IReadOnlyList<KnownLimit> entries, int seed, IReadOnlyList<Operation> history, CheckResult lin)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(lin);
        var entry = entries.SingleOrDefault(e => e.Seed == seed);
        if (entry is null)
        {
            return (false, null);
        }

        var digest = Digest(history.Where(o => o.Key == entry.Key));
        if (digest != entry.Digest)
        {
            return (false, $"{entry.Id}: seed {seed}, key {entry.Key} no longer has the recorded history (digest {digest[..12]}, recorded {entry.Digest[..12]}): re-measure it or remove the entry");
        }

        return lin.Verdict == Verdict.Undecided && lin.Key == entry.Key
            ? (true, null)
            : (false, $"{entry.Id}: seed {seed}, key {entry.Key} is no longer undecided at the budget ({lin.Verdict}): the entry is stale; remove it");
    }
}
