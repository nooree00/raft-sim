# The node↔world interface

The contract between `Raft.Core` (a node) and whatever runs it — the simulator (`Raft.Simulation`)
and, in phase 9, the TCP host. Written before either side exists, and traced against every item of
the paper's Figure 2 (plus Figure 13, §6 and §8) in the table at the end. Spec §4 is the parent.

## 1. The node

A node is a deterministic, closed state machine: `INode.Step(Input) → IReadOnlyList<Effect>`. It
holds mutable state, but everything it sees arrives as an input or through its construction
context, and everything it does leaves as an effect. Same construction, same inputs → same effects,
byte for byte.

A node is built by a `NodeFactory` from a `NodeContext`: its `NodeId`, its peers, an injected
`IRandomSource`, and the contents of its durable files as the disk holds them after recovery. There
is no other way in. Crash-and-restart is "throw the object away, call the factory again with what
the disk kept", so volatile state has no path to survive.

## 2. Inputs

| Input | Meaning |
|---|---|
| `Tick(Elapsed)` | Logical time passed for this node since its last tick, in the node's own units. The node has no clock; it counts ticks. A paused node receives one `Tick` covering the pause on resume — never the missed ticks one by one. |
| `Receive(From, Payload)` | A message arrived. `Payload` is bytes. |
| `ClientRequest(RequestId, Payload)` | A client command or query arrived (phase 8). |

## 3. Effects

Effects are an **ordered list**. The order is part of the contract (§4).

| Effect | Meaning |
|---|---|
| `Send(To, Payload)` | Send bytes to a peer. |
| `PersistAppend(File, Data)` | Append bytes to a file. |
| `PersistWriteAt(File, Offset, Data)` | Write bytes at an offset, creating the file if needed (snapshot chunks, Figure 13). Not atomic. |
| `PersistRename(File, To)` | Atomically replace `To` with `File` (atomic snapshot replacement, spec §8). |
| `PersistDelete(File)` | Remove a file. |
| `ClientResponse(RequestId, Payload)` | Answer a client. |
| `Emit(Name, Fields)` | A structured event for the trace (spec §9). No effect on the world. |

Messages are bytes, not objects (decided at the phase-1 review): the simulator can then model size
and byte-level corruption, and the TCP host reuses the same encoding. The codec's canonical-form
question — two encodings of one message must not both decode — is decided when Core takes the codec
in phase 3 (register row).

## 4. The persist barrier

**A `Send` or `ClientResponse` is released only after every persist effect that the same node
emitted before it — in this step or any earlier step — is durable.** Per node, not per step: a
send in step 2 waits for a persist still in flight from step 1. This is what "durable before any
message is sent, or any decision is made, that depends on it" (spec §8) means at the interface; the
node never waits, the world holds the message back. Consequences:

- A candidate's own vote is persisted before its RequestVote leaves (spec §8, amendment A7), by
  emitting the persist first.
- A leader may count its own entry toward a majority as soon as it appends it: every
  AppendEntries carrying the entry, and so every acknowledgement of it, follows the persist.
- The simulator can violate the barrier deliberately (a positive-control mode), and phase 3's
  invariants must then go red.

Durability, as the simulator models it: each persist completes after a (possibly slow) disk
latency; completed writes are durable. On a crash, writes still pending are lost; the fault modes
refine that — a torn final write (a prefix of one pending write survives), reordered completion (an
arbitrary subset of pending writes survives), and the positive control, a disk that loses writes it
had completed. `PersistRename` is atomic: after a crash either the old or the new file exists.

## 5. Crash, restart, pause

- **Crash**: the node object is discarded; pending writes meet their fate (§4); messages addressed
  to the node while it is down are lost.
- **Restart**: the factory is called with the recovered files.
- **Pause**: the object is kept; no ticks and no deliveries reach it; messages addressed to it are
  held and delivered in arrival order on resume, after one `Tick` covering the pause. A paused
  leader resumes believing it is leader.

## 6. Time

The simulator owns one logical clock. Each node is ticked at its own integer rate (clock skew as a
ratio, no floating point), and receives the elapsed amount in `Tick`. Election timeouts are counted
in ticks and drawn from the injected randomness.

## 7. Randomness

`IRandomSource` is the node's only randomness, injected at construction. The simulator derives each
node's source from the run's seed by purpose (spec §7), so removing one fault from a schedule does not
shift any other draw.

## 8. The state machine

`IStateMachine` (`Apply`, `Snapshot`, `Restore`) is injected into a Raft node (phase 3 and later); the
key-value store implements it. Pure, like the node.

## 9. Traceability

One row per item of `docs/design/figure2-checklist.md`. **Carried by** names `Raft.Core` members;
`Raft.Architecture.Tests.TraceabilityTests` requires every checklist id to have exactly one row and
every named member to exist in the built assembly. **Source** says where the rule comes from — any
source other than the figure the item is listed under is marked *outside*. **Stressed by** names the
simulator faults (phases 1's fault kinds) that exercise the rule.

| Id | Carried by | Source | Stressed by |
|---|---|---|---|
| F2-01 | `Raft.Core.PersistAppend`, `Raft.Core.PersistWriteAt`, `Raft.Core.Send` | Figure 2, and *outside*: spec §8 — the barrier covers every message that depends on persisted state, not only RPC responses (the candidate's vote for itself is sent, not a response) | crash-pending, crash-torn, crash-reordered, slow-disk, barrier-violation |
| F2-02 | `Raft.Core.PersistAppend`, `Raft.Core.NodeContext.Files` | Figure 2 | crash-pending, crash-lose-synced |
| F2-03 | `Raft.Core.PersistAppend`, `Raft.Core.NodeContext.Files` | Figure 2 | crash-pending, crash-lose-synced |
| F2-04 | `Raft.Core.PersistAppend`, `Raft.Core.NodeContext.Files` | Figure 2 | crash-pending, crash-torn, crash-reordered |
| F2-05 | `Raft.Core.INode.Step` | Figure 2 (volatile: rebuilt after restart) | crash-pending |
| F2-06 | `Raft.Core.INode.Step`, `Raft.Core.IStateMachine.Apply` | Figure 2 (volatile: replayed after restart) | crash-pending |
| F2-07 | `Raft.Core.INode.Step` | Figure 2 | partition, drop |
| F2-08 | `Raft.Core.INode.Step` | Figure 2 | partition, drop, duplicate |
| F2-09 | `Raft.Core.Send`, `Raft.Core.Receive` | Figure 2 | drop, duplicate, delay, reorder, partition |
| F2-10 | `Raft.Core.Send`, `Raft.Core.Receive` | Figure 2 | drop, duplicate, reorder |
| F2-11 | `Raft.Core.Receive`, `Raft.Core.Send` | Figure 2 | delay, reorder, pause |
| F2-12 | `Raft.Core.Receive`, `Raft.Core.Send` | Figure 2 | drop, reorder, crash-pending |
| F2-13 | `Raft.Core.Receive`, `Raft.Core.PersistAppend`, `Raft.Core.PersistWriteAt` | Figure 2, and *outside*: §5.3 — truncate only on an actual conflict, so a stale or duplicated AppendEntries must not truncate | duplicate, reorder, delay |
| F2-14 | `Raft.Core.Receive`, `Raft.Core.PersistAppend` | Figure 2 | duplicate, reorder |
| F2-15 | `Raft.Core.Receive` | Figure 2 | reorder, duplicate |
| F2-16 | `Raft.Core.Send`, `Raft.Core.Receive` | Figure 2 | drop, duplicate, delay, partition |
| F2-17 | `Raft.Core.Send`, `Raft.Core.Receive` | Figure 2 | drop, duplicate, reorder |
| F2-18 | `Raft.Core.Receive`, `Raft.Core.Send` | Figure 2 | delay, pause |
| F2-19 | `Raft.Core.Receive`, `Raft.Core.PersistAppend`, `Raft.Core.Send` | Figure 2; the vote is persisted before the reply by effect order (§4) | crash-pending, crash-lose-synced, barrier-violation |
| F2-20 | `Raft.Core.IStateMachine.Apply` | Figure 2 | crash-pending |
| F2-21 | `Raft.Core.Receive`, `Raft.Core.PersistAppend` | Figure 2, and *outside*: spec §8 — the new term is durable before anything that depends on it is sent | crash-pending, pause, partition |
| F2-22 | `Raft.Core.Receive`, `Raft.Core.Send` | Figure 2 | drop, partition |
| F2-23 | `Raft.Core.Tick`, `Raft.Core.IRandomSource` | Figure 2 | partition, pause, skew, drop |
| F2-24 | `Raft.Core.Tick`, `Raft.Core.PersistAppend`, `Raft.Core.Send`, `Raft.Core.IRandomSource` | Figure 2, and *outside*: spec §8 (amendment A7) — the vote for self is durable before any RequestVote leaves | crash-pending, barrier-violation, skew |
| F2-25 | `Raft.Core.Receive` | Figure 2 | duplicate, partition |
| F2-26 | `Raft.Core.Receive` | Figure 2 | delay, reorder |
| F2-27 | `Raft.Core.Tick`, `Raft.Core.IRandomSource` | Figure 2 | partition, skew |
| F2-28 | `Raft.Core.Tick`, `Raft.Core.Send` | Figure 2 | pause, skew, partition |
| F2-29 | `Raft.Core.ClientRequest`, `Raft.Core.PersistAppend`, `Raft.Core.ClientResponse`, `Raft.Core.IStateMachine.Apply` | Figure 2 | crash-pending, partition, pause |
| F2-30 | `Raft.Core.Send` | Figure 2 | drop, delay |
| F2-31 | `Raft.Core.Receive` | Figure 2 | duplicate, reorder |
| F2-32 | `Raft.Core.Receive`, `Raft.Core.Send` | Figure 2 | drop, crash-pending |
| F2-33 | `Raft.Core.Receive` | Figure 2, and *outside*: the leader counts itself only for entries it has persisted — guaranteed by the barrier (§4), since no acknowledgement can precede the persist | crash-pending, crash-lose-synced, barrier-violation |
| F13-01 | `Raft.Core.Send`, `Raft.Core.Receive` | Figure 13 | drop, reorder, duplicate |
| F13-02 | `Raft.Core.Send` | Figure 13 | drop |
| F13-03 | `Raft.Core.Receive`, `Raft.Core.Send` | Figure 13 | delay |
| F13-04 | `Raft.Core.PersistWriteAt` | Figure 13 | crash-torn |
| F13-05 | `Raft.Core.PersistWriteAt` | Figure 13 | crash-torn, crash-reordered |
| F13-06 | `Raft.Core.Send` | Figure 13 | drop |
| F13-07 | `Raft.Core.PersistRename`, `Raft.Core.PersistDelete` | Figure 13, and *outside*: spec §8 — the snapshot is replaced atomically (temporary file, rename) | crash-pending, crash-torn |
| F13-08 | `Raft.Core.PersistAppend`, `Raft.Core.PersistRename` | Figure 13 | crash-pending |
| F13-09 | `Raft.Core.PersistDelete` | Figure 13 | crash-pending |
| F13-10 | `Raft.Core.IStateMachine.Restore` | Figure 13 | crash-pending |
| S6-01 | `Raft.Core.Receive`, `Raft.Core.INode.Step` | §6 | reorder, crash-pending |
| S6-02 | `Raft.Core.Receive` | §6 | partition |
| S6-03 | `Raft.Core.NodeContext.Peers`, `Raft.Core.Send` | §6 | partition, delay |
| S6-04 | `Raft.Core.INode.Step` | §6 | partition |
| S6-05 | `Raft.Core.Tick`, `Raft.Core.Receive` | §6 (implemented with election, spec §5) | partition, pause, skew |
| S8-01 | `Raft.Core.ClientRequest` | §8 | duplicate, drop |
| S8-02 | `Raft.Core.PersistAppend` | §8 | crash-pending |
| S8-03 | `Raft.Core.ClientRequest`, `Raft.Core.ClientResponse` | §8 | pause, partition |
| S8-04 | `Raft.Core.Send`, `Raft.Core.Receive`, `Raft.Core.ClientResponse` | §8 | pause, partition, skew |
