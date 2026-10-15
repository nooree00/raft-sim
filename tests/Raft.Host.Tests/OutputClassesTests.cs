using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raft.Core;
using Raft.Host;
using Xunit;

namespace Raft.Host.Tests;

/// <summary>
/// P15-03: each output of a batch classed by the persists before it (A: its own list's, B: only an earlier
/// list's, C: none), stamped at its list's handling with that class, and counted with its hold. The split
/// exists because the observable has two causes (the batch's hold, the replication pipeline's wait), so the
/// emission must be stamped when the node emits, not when the host releases. Sabotages S-held-1 (stamped at
/// release time), S-held-2 (every output classed as held needlessly).
/// </summary>
public sealed class OutputClassesTests
{
    private static ReadOnlyMemory<byte> B(string s) => Encoding.ASCII.GetBytes(s);

    private static readonly IReadOnlyList<IReadOnlyList<Effect>> Batch =
    [
        [new Send(new NodeId(2), B("m0")), new PersistAppend("entries.log", B("a")), new Send(new NodeId(3), B("m1"))],
        [new ClientResponse(7, B("ok")), new Emit("x", [])],
        [new PersistWriteAt("term.log", 0, B("t")), new ClientResponse(8, B("ok"))],
    ];

    /// <summary>Sabotage S-held-2.</summary>
    [Fact]
    public void EachOutputIsClassedByThePersistsBeforeItInItsBatch()
    {
        var classes = OutputClasses.Of(Batch).Select(o => (o.List, o.Hold)).ToList();

        Assert.Equal([(0, OutputClasses.Hold.None), (0, OutputClasses.Hold.Own), (1, OutputClasses.Hold.Earlier), (2, OutputClasses.Hold.Own)], classes);
    }

    /// <summary>Sabotage S-held-1: each emission at its list's handling (100, 200, 300 here), not when the stamps are written.</summary>
    [Fact]
    public void AnEmissionIsStampedAtItsListsHandlingWithItsClass()
    {
        var trace = new HandOffTrace();

        OutputClasses.Stamp(trace, Batch, [100, 200, 300]);

        Assert.Equal(
            [(100L, HandOffTrace.Kind.EmittedNone, 2L), (100L, HandOffTrace.Kind.EmittedOwn, 3L), (200L, HandOffTrace.Kind.EmittedEarlier, 7L), (300L, HandOffTrace.Kind.EmittedOwn, 8L)],
            trace.Snapshot().Select(s => (s.At, s.Kind, s.Id)));
    }

    /// <summary>A hold runs from the list's handling to the batch's release; a batch of one list releases an output behind no persist as it reaches it.</summary>
    [Fact]
    public void EachClassIsCountedWithItsHold()
    {
        var (counts, ticks) = (new long[6], new long[6]);
        OutputClasses.Count(Batch, [100, 200, 300], 1_000, counts, ticks);
        OutputClasses.Count([[new ClientResponse(9, B("ok")), new PersistAppend("entries.log", B("b"))]], [400], 1_000, counts, ticks);

        Assert.Equal([1L, 0, 1, 1, 1, 1], counts);
        Assert.Equal([900L, 0, 900, 700, 800, 0], ticks);
    }
}
