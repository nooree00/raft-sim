using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Raft.Architecture.Tests;

/// <summary>
/// Spec §4: Raft.Core takes no ambient dependencies. The project graph cannot enforce that —
/// DateTime, Random, Thread and Task.Delay are all BCL — so the compiled assembly's metadata is
/// read and every external type it references is checked against an allowlist that fails closed.
///
/// Vacuity risks: an empty Core passes trivially (guard: Core must define types); an allowlist of
/// namespaces admits System.DateTime (guard: exact type names); scanning member references alone
/// misses a type used only as a field type or in typeof (guard: the TypeRef table, which lists
/// every external type referenced anywhere); scanning a stale or reference build (guard: the
/// assembly actually loaded by this test, and a check that it is not a reference assembly).
/// Residual: a randomized hash reached through an allowed generic (EqualityComparer&lt;string&gt;)
/// is not visible here; the cross-process determinism check (phase 1) covers it.
/// Sabotages: S-amb-1..7, S-core-1.
/// </summary>
public sealed class AmbientDependencyTests
{
    /// <summary>External types Raft.Core may reference. Exact names; add deliberately, with a reason.</summary>
    internal static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        // Primitives and core object model.
        "System.Object", "System.ValueType", "System.Enum", "System.Void", "System.Boolean",
        "System.Byte", "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.String",
        "System.Nullable`1", "System.Array", "System.Span`1", "System.ReadOnlySpan`1", "System.Math",
        "System.IComparable`1", "System.IEquatable`1", "System.IDisposable",
        // Exceptions a pure state machine may throw.
        "System.ArgumentException", "System.ArgumentNullException", "System.ArgumentOutOfRangeException",
        "System.InvalidOperationException", "System.NotImplementedException",
        // Deterministic collections and text.
        "System.Collections.Generic.List`1", "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.IReadOnlyList`1", "System.Collections.Generic.IReadOnlyDictionary`2",
        "System.Collections.Generic.IEnumerable`1", "System.Collections.Generic.IEnumerator`1",
        "System.Collections.Generic.EqualityComparer`1", "System.Collections.Generic.Comparer`1",
        "System.Linq.Enumerable", "System.Text.StringBuilder", "System.Buffers.Binary.BinaryPrimitives",
        // Emitted by the compiler and SDK, not written by us; inert metadata.
        "System.Diagnostics.DebuggableAttribute", "System.Diagnostics.DebuggableAttribute+DebuggingModes",
        "System.Diagnostics.DebuggerBrowsableAttribute", "System.Diagnostics.DebuggerBrowsableState",
        "System.Reflection.AssemblyCompanyAttribute", "System.Reflection.AssemblyConfigurationAttribute",
        "System.Reflection.AssemblyFileVersionAttribute", "System.Reflection.AssemblyInformationalVersionAttribute",
        "System.Reflection.AssemblyProductAttribute", "System.Reflection.AssemblyTitleAttribute",
        "System.Runtime.CompilerServices.CompilationRelaxationsAttribute",
        "System.Runtime.CompilerServices.CompilerGeneratedAttribute", "System.Runtime.CompilerServices.IsExternalInit",
        "System.Runtime.CompilerServices.IsReadOnlyAttribute", "System.Runtime.CompilerServices.RefSafetyRulesAttribute",
        "System.Runtime.CompilerServices.RuntimeCompatibilityAttribute", "System.Runtime.Versioning.TargetFrameworkAttribute",
        "System.Runtime.CompilerServices.NullableAttribute", "System.Runtime.CompilerServices.NullableContextAttribute",
        "System.Runtime.CompilerServices.ExtensionAttribute", "System.Runtime.CompilerServices.PreserveBaseOverridesAttribute",
        // Phase 1 interface types (docs/design/node-interface.md): immutable payloads, and the
        // types a delegate declaration emits (BeginInvoke/EndInvoke are never called on .NET).
        "System.ReadOnlyMemory`1", "System.MulticastDelegate", "System.AsyncCallback", "System.IAsyncResult",
        "System.IFormatProvider",
        // Allowed only through the members listed in RestrictedMembers below.
        "System.Globalization.CultureInfo", "System.Type", "System.RuntimeTypeHandle",
        "System.Runtime.CompilerServices.RuntimeHelpers",
    };

    /// <summary>
    /// Types whose other members are ambient or nondeterministic: CultureInfo.CurrentCulture reads
    /// process state; Type offers reflection; RuntimeHelpers.GetHashCode is an identity hash that
    /// differs per run. Only the members records and invariant formatting need are allowed.
    /// </summary>
    internal static readonly Dictionary<string, HashSet<string>> RestrictedMembers = new(StringComparer.Ordinal)
    {
        ["System.Globalization.CultureInfo"] = new(StringComparer.Ordinal) { "get_InvariantCulture" },
        ["System.Type"] = new(StringComparer.Ordinal) { "GetTypeFromHandle", "op_Equality", "op_Inequality" },
        ["System.Runtime.CompilerServices.RuntimeHelpers"] = new(StringComparer.Ordinal) { "EnsureSufficientExecutionStack", "GetSubArray", "InitializeArray" },
    };

    /// <summary>Members of otherwise-allowed types that are not deterministic across processes.</summary>
    internal static readonly HashSet<string> DeniedMembers = new(StringComparer.Ordinal)
    {
        "System.String::GetHashCode",
    };

    internal static readonly HashSet<string> AllowedAssemblyRefs = new(StringComparer.Ordinal)
    {
        "System.Runtime", "System.Collections", "System.Linq", "System.Memory",
    };

    private static string CorePath => typeof(Raft.Core.Term).Assembly.Location;

    [Fact]
    public void CoreIsARealImplementationAssemblyWithTypes()
    {
        using var pe = new PEReader(File.OpenRead(CorePath));
        var md = pe.GetMetadataReader();

        var typeNames = md.TypeDefinitions.Select(h => md.GetString(md.GetTypeDefinition(h).Name)).ToList();
        Assert.Contains("Term", typeNames);

        var attributes = md.GetAssemblyDefinition().GetCustomAttributes()
            .Select(h => AttributeTypeName(md, md.GetCustomAttribute(h))).ToList();
        Assert.DoesNotContain("System.Runtime.CompilerServices.ReferenceAssemblyAttribute", attributes);
    }

    [Fact]
    public void EveryReferencedTypeIsOnTheAllowlist()
    {
        using var pe = new PEReader(File.OpenRead(CorePath));
        var md = pe.GetMetadataReader();

        var referenced = md.TypeReferences.Select(h => FullName(md, h)).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(referenced);

        var illegal = referenced.Where(t => !AllowedTypes.Contains(t)).ToList();
        Assert.True(illegal.Count == 0, "Raft.Core references types outside the allowlist:\n  " + string.Join("\n  ", illegal));
    }

    [Fact]
    public void NoDeniedMemberIsReferenced()
    {
        using var pe = new PEReader(File.OpenRead(CorePath));
        var md = pe.GetMetadataReader();

        var used = new List<string>();
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            if (mr.Parent.Kind == HandleKind.TypeReference)
            {
                used.Add(FullName(md, (TypeReferenceHandle)mr.Parent) + "::" + md.GetString(mr.Name));
            }
        }

        var illegal = used.Where(DeniedMembers.Contains).Distinct().ToList();
        Assert.True(illegal.Count == 0, "Raft.Core references denied members: " + string.Join(", ", illegal));
    }

    [Fact]
    public void RestrictedTypesAreUsedOnlyThroughTheirAllowedMembers()
    {
        using var pe = new PEReader(File.OpenRead(CorePath));
        var md = pe.GetMetadataReader();

        var illegal = new List<string>();
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            if (mr.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var type = FullName(md, (TypeReferenceHandle)mr.Parent);
            var member = md.GetString(mr.Name);
            if (RestrictedMembers.TryGetValue(type, out var allowed) && !allowed.Contains(member))
            {
                illegal.Add(type + "::" + member);
            }
        }

        Assert.True(illegal.Count == 0, "Raft.Core uses restricted types through members outside their allowlist: " + string.Join(", ", illegal.Distinct()));
    }

    [Fact]
    public void EveryAssemblyReferenceIsOnTheAllowlist()
    {
        using var pe = new PEReader(File.OpenRead(CorePath));
        var md = pe.GetMetadataReader();

        var refs = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
        var illegal = refs.Where(r => !AllowedAssemblyRefs.Contains(r)).ToList();
        Assert.True(illegal.Count == 0, "Raft.Core references assemblies outside the allowlist: " + string.Join(", ", illegal));
    }

    [Fact]
    public void NoPlatformInvoke()
    {
        using var pe = new PEReader(File.OpenRead(CorePath));
        var md = pe.GetMetadataReader();

        var pinvoke = md.MethodDefinitions
            .Select(md.GetMethodDefinition)
            .Where(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0)
            .Select(m => md.GetString(m.Name))
            .ToList();
        Assert.Empty(pinvoke);
    }

    internal static string FullName(MetadataReader md, TypeReferenceHandle h)
    {
        var tr = md.GetTypeReference(h);
        var name = md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return FullName(md, (TypeReferenceHandle)tr.ResolutionScope) + "+" + name;
        }

        var ns = md.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static string AttributeTypeName(MetadataReader md, CustomAttribute a)
    {
        switch (a.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var parent = md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent;
                return parent.Kind == HandleKind.TypeReference ? FullName(md, (TypeReferenceHandle)parent) : "?";
            case HandleKind.MethodDefinition:
                var td = md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)a.Constructor).GetDeclaringType());
                return md.GetString(td.Namespace) + "." + md.GetString(td.Name);
            default:
                return "?";
        }
    }
}
