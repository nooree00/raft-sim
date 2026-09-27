using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Raft.Architecture.Tests;

/// <summary>
/// P1-08: the simulator must be as closed as Core — its determinism is the property everything
/// rests on — so its compiled metadata is scanned too, against Core's allowlist plus the extras
/// below, each with a reason. Same vacuity guards as the Core scan. Sabotage S-det-2.
/// </summary>
public sealed class SimulationAmbientDependencyTests
{
    /// <summary>Beyond Core's allowlist (and Raft.Core's own types, a scanned, closed assembly).</summary>
    private static readonly HashSet<string> Extras = new(StringComparer.Ordinal)
    {
        // Deterministic collections. Hashed sets and maps are used for membership only, never
        // enumerated for output; the cross-process trace comparison covers what this cannot see.
        "System.Collections.Generic.HashSet`1", "System.Collections.Generic.PriorityQueue`2",
        "System.Collections.Generic.Queue`1", "System.Collections.Generic.SortedDictionary`2",
        "System.Collections.Generic.SortedDictionary`2+Enumerator", "System.Collections.Generic.List`1+Enumerator",
        "System.Collections.Generic.KeyValuePair`2", "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IComparer`1", "System.Collections.Generic.IDictionary`2",
        "System.Collections.Generic.IEqualityComparer`1", "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IReadOnlyCollection`1", "System.Collections.Generic.CollectionExtensions",
        "System.Collections.ICollection", "System.Collections.IEnumerable", "System.Collections.IEnumerator",
        "System.Collections.IList",
        // Delegates, tuples, slicing, attributes and exceptions the compiler emits.
        "System.Action", "System.Func`1", "System.Func`2", "System.Predicate`1", "System.ValueTuple`2",
        "System.Index", "System.Range", "System.MemoryExtensions", "System.IndexOutOfRangeException",
        "System.NotSupportedException", "System.ParamArrayAttribute", "System.Runtime.InteropServices.InAttribute",
        "System.Runtime.CompilerServices.TupleElementNamesAttribute",
        // Allowed only through the members in ExtraRestricted.
        "System.StringComparer", "System.Text.Encoding", "System.Reflection.MemberInfo",
    };

    /// <summary>
    /// StringComparer only as Ordinal (culture-aware comparers read process state); Encoding only
    /// as UTF-8 bytes; MemberInfo only for GetType().Name in messages. DefaultInterpolatedStringHandler
    /// is deliberately absent: interpolation formats numbers with the current culture.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> ExtraRestricted = new(StringComparer.Ordinal)
    {
        ["System.StringComparer"] = new(StringComparer.Ordinal) { "get_Ordinal" },
        ["System.Text.Encoding"] = new(StringComparer.Ordinal) { "get_UTF8", "GetBytes" },
        ["System.Reflection.MemberInfo"] = new(StringComparer.Ordinal) { "get_Name" },
    };

    private static readonly HashSet<string> ExtraAssemblyRefs = new(StringComparer.Ordinal)
    {
    };

    private static string SimulationPath => typeof(Raft.Simulation.Simulator).Assembly.Location;

    private static MetadataReader Open(out PEReader pe)
    {
        pe = new PEReader(File.OpenRead(SimulationPath));
        return pe.GetMetadataReader();
    }

    [Fact]
    public void EveryReferencedTypeIsOnTheAllowlist()
    {
        var md = Open(out var pe);
        using (pe)
        {
            var referenced = md.TypeReferences.Select(h => AmbientDependencyTests.FullName(md, h)).Distinct().Order(StringComparer.Ordinal).ToList();
            Assert.True(referenced.Count > 50, $"only {referenced.Count} type references: is this the simulator?");
            var illegal = referenced.Where(t => !t.StartsWith("Raft.Core.", StringComparison.Ordinal) &&
                !AmbientDependencyTests.AllowedTypes.Contains(t) && !Extras.Contains(t)).ToList();
            Assert.True(illegal.Count == 0, "Raft.Simulation references types outside the allowlist:\n  " + string.Join("\n  ", illegal));
        }
    }

    [Fact]
    public void NoDeniedOrRestrictedMemberIsMisused()
    {
        var md = Open(out var pe);
        using (pe)
        {
            var illegal = new List<string>();
            foreach (var h in md.MemberReferences)
            {
                var mr = md.GetMemberReference(h);
                if (mr.Parent.Kind != HandleKind.TypeReference)
                {
                    continue;
                }

                var type = AmbientDependencyTests.FullName(md, (TypeReferenceHandle)mr.Parent);
                var member = md.GetString(mr.Name);
                if (AmbientDependencyTests.DeniedMembers.Contains(type + "::" + member) ||
                    (AmbientDependencyTests.RestrictedMembers.TryGetValue(type, out var allowed) && !allowed.Contains(member)) ||
                    (ExtraRestricted.TryGetValue(type, out var extra) && !extra.Contains(member)))
                {
                    illegal.Add(type + "::" + member);
                }
            }

            Assert.True(illegal.Count == 0, "Raft.Simulation misuses denied or restricted members: " + string.Join(", ", illegal.Distinct()));
        }
    }

    [Fact]
    public void EveryAssemblyReferenceIsOnTheAllowlist()
    {
        var md = Open(out var pe);
        using (pe)
        {
            var refs = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
            var illegal = refs.Where(r => !AmbientDependencyTests.AllowedAssemblyRefs.Contains(r) && !ExtraAssemblyRefs.Contains(r) && r != "Raft.Core").ToList();
            Assert.True(illegal.Count == 0, "Raft.Simulation references assemblies outside the allowlist: " + string.Join(", ", illegal));
        }
    }
}
