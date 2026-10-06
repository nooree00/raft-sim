# The node↔world interface

The contract between `Raft.Core` (a node) and whatever runs it — the simulator (`Raft.Simulation`)
and, in phase 9, the TCP host. Written before either side exists, and traced against every item of
the paper's Figure 2 (plus Figure 13, §6 and §8) in the table at the end. Spec §4 is the parent.

## 1. The node

A node is a deterministic, closed state machine: `INode.Handle(Input) → IReadOnlyList<Effect>`. It
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
| `PersistTruncate(File, Length)` | Shorten a file: truncate a log after a conflict (F2-13), drop a torn tail on recovery (spec §8). Added in P1-06, when the torn-write fault showed nothing else could shrink a file. |
| `PersistDelete(File)` | Remove a file. |
| `ClientResponse(RequestId, Payload)` | Answer a client. |
| `Emit(Name, Fields)` | A structured event for the trace (spec §9). No effect on the world. Field values may not contain a space, `=` or a newline, so a trace line splits one way only. |

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
- **A rename waits too** (phase 7, decision 1): the world holds a `PersistRename` until every persist
  the node emitted before it is durable, and a persist emitted after a rename waits until the rename
  is durable (P7-05: the disk applies a write by name, and a write meant for the renamed file, kept
  by a crash that lost the rename, landed on the old file and cut entries from it), so the node's
  writes reach the disk in the order it emitted them. This is the fsync before the
  rename (spec §8) done where the barrier already lives; the node still never waits. It is a
  simplification, not the contract: a real file system can make a rename durable without the data,
  and the barrier assumes the I/O layer prevents that (spec §8's known limit, the register's
  phase-10 row). The disk models a rename whose source data was lost as an empty file under the
  real name, and a positive-control mode releases renames early to show it (P7-00).

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

`IStateMachine` (`Apply`, `Snapshot`, `Restore`, and `IsQuery` and `Query` for reads answered without a log entry, P8-05) is injected into a Raft node (phase 3 and later); the
key-value store implements it. Pure, like the node.

## 8a. Messages and their wire form

Peers exchange `RequestVote`, `RequestVoteResponse`, `AppendEntries` and `AppendEntriesResponse`
(Figure 2), and `InstallSnapshot` and `InstallSnapshotResponse` (Figure 13, P7-07: a chunk of the
snapshot's record at an offset, and the bytes the follower holds from its start or that it holds
the snapshot whole) as bytes, through `MessageCodec` in Core: a type byte, fixed-width big-endian integers,
and a count or length before every variable part. `AppendEntries` and its response carry a read round
(P8-05): the leader's latest when it sent the request, echoed in the response, so an acknowledgement
confirms only the reads that arrived before its request left. Between hosts (P9-02, phase 9 decision 2) each message
travels in a frame: a 4-byte length, the message, and a CRC-32C of both. A frame whose checksum
fails is refused and its connection closed, which Raft sees as a lost message; this, not canonical
form, is what makes a corrupted message on a real network visible.

**The canonical rule (P3-02):** every byte string decodes to at most one message, and every
message has exactly one encoding. Decoding rejects trailing bytes, an unknown type, a boolean other
than 0 or 1, a negative term, index, count or length, and a node id below 1. Tested both ways:
`decode(encode(m)) = m` over generated messages, and `encode(decode(b)) = b` for every mutated
encoding that decodes (`MessageCodecTests`).

**What it does not do.** Canonical form removes the ambiguity of two encodings for one message. It
does not make corruption visible: 95% of single-bit flips of a valid `RequestVote` decode to a
different valid message (measured, P3-02). Detecting corruption would need a checksum. The
simulator injects no corruption (spec §7), so none is added now; a transport that can corrupt
bytes (phase 9's TCP host) must decide this again.

## 9. Traceability

One row per item of `docs/design/figure2-checklist.md`. **Carried by** names `Raft.Core` members;
`Raft.Architecture.Tests.TraceabilityTests` requires every checklist id to have exactly one row and
every named member to exist in the built assembly. **Source** says where the rule comes from — any
source other than the figure the item is listed under is marked *outside*. **Stressed by** names the
*effects* the rule depends on, as coverage dimensions (`Raft.Simulation.Coverage`), never fault names:
a fault name measures the injection, an effect measures the test (P2-02). A test in
`Raft.Simulation.Tests.CoverageTests` requires every name here to be a dimension that the generated
runs hit above the coverage floor; `control:` names are the positive controls, exercised by their
own tests and off in generated runs.

| Id | Carried by | Source | Stressed by |
|---|---|---|---|
| F2-01 | `Raft.Core.PersistAppend`, `Raft.Core.PersistWriteAt`, `Raft.Core.Send`, `Raft.Core.RaftNode.Handle` | Figure 2, and *outside*: spec §8 — the barrier covers every message that depends on persisted state, not only RPC responses (the candidate's vote for itself is sent, not a response) | unsynced-write-lost, partial-record-left-on-disk, writes-completed-out-of-order-at-crash, write-slower-than-normal-latency, control:send-before-its-persist-durable, all-down, restarted-from-disk |
| F2-02 | `Raft.Core.PersistAppend`, `Raft.Core.NodeContext.Files`, `Raft.Core.RaftNode.Handle` | Figure 2 | unsynced-write-lost, control:synced-write-lost, all-down, restarted-from-disk |
| F2-03 | `Raft.Core.PersistAppend`, `Raft.Core.NodeContext.Files`, `Raft.Core.RaftNode.Handle` | Figure 2 | unsynced-write-lost, control:synced-write-lost, all-down, restarted-from-disk |
| F2-04 | `Raft.Core.PersistAppend`, `Raft.Core.NodeContext.Files` | Figure 2 | unsynced-write-lost, partial-record-left-on-disk, writes-completed-out-of-order-at-crash, all-down, restarted-from-disk |
| F2-05 | `Raft.Core.INode.Handle` | Figure 2 (volatile: rebuilt after restart) | unsynced-write-lost |
| F2-06 | `Raft.Core.INode.Handle`, `Raft.Core.IStateMachine.Apply` | Figure 2 (volatile: replayed after restart) | unsynced-write-lost |
| F2-07 | `Raft.Core.INode.Handle` | Figure 2 | one-way-reachability, node-isolated-for-a-timeout, lost-in-transit-to-a-live-receiver |
| F2-08 | `Raft.Core.INode.Handle` | Figure 2 | one-way-reachability, node-isolated-for-a-timeout, lost-in-transit-to-a-live-receiver, delivered-twice |
| F2-09 | `Raft.Core.Send`, `Raft.Core.Receive` | Figure 2 | lost-in-transit-to-a-live-receiver, delivered-twice, delivered-later-than-normal-delay, delivered-after-later-send, one-way-reachability, node-isolated-for-a-timeout |
| F2-10 | `Raft.Core.Send`, `Raft.Core.Receive` | Figure 2 | lost-in-transit-to-a-live-receiver, delivered-twice, delivered-after-later-send |
| F2-11 | `Raft.Core.Receive`, `Raft.Core.Send`, `Raft.Core.RaftNode.Handle` | Figure 2 | delivered-later-than-normal-delay, delivered-after-later-send, step-after-silence-longer-than-a-timeout |
| F2-12 | `Raft.Core.Receive`, `Raft.Core.Send` | Figure 2 | lost-in-transit-to-a-live-receiver, delivered-after-later-send, unsynced-write-lost |
| F2-13 | `Raft.Core.Receive`, `Raft.Core.PersistTruncate`, `Raft.Core.PersistAppend` | Figure 2, and *outside*: §5.3 — truncate only on an actual conflict, so a stale or duplicated AppendEntries must not truncate | delivered-twice, delivered-after-later-send, delivered-later-than-normal-delay |
| F2-14 | `Raft.Core.Receive`, `Raft.Core.PersistAppend` | Figure 2 | delivered-twice, delivered-after-later-send |
| F2-15 | `Raft.Core.Receive` | Figure 2 | delivered-after-later-send, delivered-twice |
| F2-16 | `Raft.Core.Send`, `Raft.Core.Receive`, `Raft.Core.RaftNode.Handle` | Figure 2 | lost-in-transit-to-a-live-receiver, delivered-twice, delivered-later-than-normal-delay, one-way-reachability, node-isolated-for-a-timeout |
| F2-17 | `Raft.Core.Send`, `Raft.Core.Receive`, `Raft.Core.RaftNode.Handle` | Figure 2 | lost-in-transit-to-a-live-receiver, delivered-twice, delivered-after-later-send |
| F2-18 | `Raft.Core.Receive`, `Raft.Core.Send`, `Raft.Core.RaftNode.Handle` | Figure 2 | delivered-later-than-normal-delay, step-after-silence-longer-than-a-timeout |
| F2-19 | `Raft.Core.Receive`, `Raft.Core.PersistAppend`, `Raft.Core.Send`, `Raft.Core.RaftNode.Handle` | Figure 2; the vote is persisted before the reply by effect order (§4) | unsynced-write-lost, control:synced-write-lost, control:send-before-its-persist-durable |
| F2-20 | `Raft.Core.IStateMachine.Apply` | Figure 2 | unsynced-write-lost |
| F2-21 | `Raft.Core.Receive`, `Raft.Core.PersistAppend`, `Raft.Core.RaftNode.Handle` | Figure 2, and *outside*: spec §8 — the new term is durable before anything that depends on it is sent | unsynced-write-lost, step-after-silence-longer-than-a-timeout, one-way-reachability, node-isolated-for-a-timeout |
| F2-22 | `Raft.Core.Receive`, `Raft.Core.Send`, `Raft.Core.RaftNode.Handle` | Figure 2 | lost-in-transit-to-a-live-receiver, one-way-reachability, node-isolated-for-a-timeout |
| F2-23 | `Raft.Core.Tick`, `Raft.Core.IRandomSource`, `Raft.Core.RaftNode.Handle` | Figure 2 | one-way-reachability, node-isolated-for-a-timeout, step-after-silence-longer-than-a-timeout, clock-rate-diverged, lost-in-transit-to-a-live-receiver |
| F2-24 | `Raft.Core.Tick`, `Raft.Core.PersistAppend`, `Raft.Core.Send`, `Raft.Core.IRandomSource`, `Raft.Core.RaftNode.Handle` | Figure 2, and *outside*: spec §8 (amendment A7) — the vote for self is durable before any RequestVote leaves | unsynced-write-lost, control:send-before-its-persist-durable, clock-rate-diverged |
| F2-25 | `Raft.Core.Receive`, `Raft.Core.RaftNode.Handle` | Figure 2 | delivered-twice, one-way-reachability, node-isolated-for-a-timeout, majority-down |
| F2-26 | `Raft.Core.Receive`, `Raft.Core.RaftNode.Handle` | Figure 2 | delivered-later-than-normal-delay, delivered-after-later-send |
| F2-27 | `Raft.Core.Tick`, `Raft.Core.IRandomSource`, `Raft.Core.RaftNode.Handle` | Figure 2 | one-way-reachability, node-isolated-for-a-timeout, clock-rate-diverged |
| F2-28 | `Raft.Core.Tick`, `Raft.Core.Send`, `Raft.Core.RaftNode.Handle` | Figure 2 | step-after-silence-longer-than-a-timeout, clock-rate-diverged, one-way-reachability, node-isolated-for-a-timeout |
| F2-29 | `Raft.Core.ClientRequest`, `Raft.Core.PersistAppend`, `Raft.Core.ClientResponse`, `Raft.Core.IStateMachine.Apply` | Figure 2 | unsynced-write-lost, one-way-reachability, node-isolated-for-a-timeout, step-after-silence-longer-than-a-timeout |
| F2-30 | `Raft.Core.Send` | Figure 2 | lost-in-transit-to-a-live-receiver, delivered-later-than-normal-delay |
| F2-31 | `Raft.Core.Receive` | Figure 2 | delivered-twice, delivered-after-later-send |
| F2-32 | `Raft.Core.Receive`, `Raft.Core.Send` | Figure 2 | lost-in-transit-to-a-live-receiver, unsynced-write-lost |
| F2-33 | `Raft.Core.Receive` | Figure 2, and *outside*: the leader counts itself only for entries it has persisted — guaranteed by the barrier (§4), since no acknowledgement can precede the persist | unsynced-write-lost, control:synced-write-lost, control:send-before-its-persist-durable, majority-down |
| F13-01 | `Raft.Core.Send`, `Raft.Core.Receive` | Figure 13 | lost-in-transit-to-a-live-receiver, delivered-after-later-send, delivered-twice |
| F13-02 | `Raft.Core.Send` | Figure 13 | lost-in-transit-to-a-live-receiver |
| F13-03 | `Raft.Core.Receive`, `Raft.Core.Send` | Figure 13 | delivered-later-than-normal-delay |
| F13-04 | `Raft.Core.PersistWriteAt` | Figure 13 | partial-record-left-on-disk |
| F13-05 | `Raft.Core.PersistWriteAt` | Figure 13 | partial-record-left-on-disk, writes-completed-out-of-order-at-crash |
| F13-06 | `Raft.Core.Send` | Figure 13 | lost-in-transit-to-a-live-receiver |
| F13-07 | `Raft.Core.PersistRename`, `Raft.Core.PersistDelete` | Figure 13, and *outside*: spec §8 — the snapshot is replaced atomically (temporary file, rename) | unsynced-write-lost, partial-record-left-on-disk |
| F13-08 | `Raft.Core.PersistAppend`, `Raft.Core.PersistRename` | Figure 13 | unsynced-write-lost |
| F13-09 | `Raft.Core.PersistDelete` | Figure 13 | unsynced-write-lost |
| F13-10 | `Raft.Core.IStateMachine.Restore` | Figure 13 | unsynced-write-lost |
| S6-01 | `Raft.Core.Receive`, `Raft.Core.INode.Handle` | §6 | delivered-after-later-send, unsynced-write-lost |
| S6-02 | `Raft.Core.Receive` | §6 | one-way-reachability, node-isolated-for-a-timeout |
| S6-03 | `Raft.Core.NodeContext.Peers`, `Raft.Core.Send` | §6 | one-way-reachability, node-isolated-for-a-timeout, delivered-later-than-normal-delay |
| S6-04 | `Raft.Core.INode.Handle` | §6 | one-way-reachability, node-isolated-for-a-timeout |
| S6-05 | `Raft.Core.Tick`, `Raft.Core.Receive` | §6 (implemented with election, spec §5) | one-way-reachability, node-isolated-for-a-timeout, step-after-silence-longer-than-a-timeout, clock-rate-diverged |
| S8-01 | `Raft.Core.ClientRequest` | §8 | delivered-twice, lost-in-transit-to-a-live-receiver |
| S8-02 | `Raft.Core.PersistAppend` | §8 | unsynced-write-lost |
| S8-03 | `Raft.Core.ClientRequest`, `Raft.Core.ClientResponse` | §8 | step-after-silence-longer-than-a-timeout, one-way-reachability, node-isolated-for-a-timeout |
| S8-04 | `Raft.Core.Send`, `Raft.Core.Receive`, `Raft.Core.ClientResponse` | §8 | step-after-silence-longer-than-a-timeout, one-way-reachability, node-isolated-for-a-timeout, clock-rate-diverged, majority-down |

### Implemented in phase 3 (leader election)

Rows whose carrier includes `Raft.Core.RaftNode.Handle`, and the test that shows each one
(`Raft.Core.Tests.ElectionTests`, hand-built inputs; `ElectionSimulationTests` and the invariant
checkers over simulated runs). Logs are empty in phase 3, so F2-19's log condition and F2-28's
log-carrying AppendEntries are phase 4's; only their election halves are claimed here.

- **F2-01**: `AFollowerThatHearsNothingStandsPersistingItsVoteBeforeAskingForVotes`, `AFollowerGrantsItsVotePersistingItBeforeReplying` (term and vote only; the log is phase 4's)
- **F2-02**: `ALeaderSeeingAHigherTermStepsDownPersistsTheTermAndStopsHeartbeats`, `AVoteSurvivesARestart`; invariant 9 over simulated runs
- **F2-03**: `AVoteSurvivesARestart`, `ASecondCandidateInTheSameTermIsDeniedAndNothingIsPersisted`; invariant 8 over simulated runs
- **F2-11**: `AnAppendEntriesFromAStaleTermIsRejectedWithTheCurrentTerm`
- **F2-16, F2-17**: `AFollowerThatHearsNothingStandsPersistingItsVoteBeforeAskingForVotes`, `AFollowerGrantsItsVotePersistingItBeforeReplying`
- **F2-18**: `AStaleRequestVoteIsDeniedWithTheCurrentTerm`
- **F2-19**: `ASecondCandidateInTheSameTermIsDeniedAndNothingIsPersisted`, `AVoteSurvivesARestart` (the votedFor half)
- **F2-21**: `ALeaderSeeingAHigherTermStepsDownPersistsTheTermAndStopsHeartbeats`
- **F2-22**: `AFollowerGrantsItsVotePersistingItBeforeReplying`, `HeartbeatsFromTheLeaderKeepAFollowerFromStanding`
- **F2-23**: `AFollowerThatHearsNothingStandsPersistingItsVoteBeforeAskingForVotes`, `HeartbeatsFromTheLeaderKeepAFollowerFromStanding`
- **F2-24**: `AFollowerThatHearsNothingStandsPersistingItsVoteBeforeAskingForVotes`, `TheElectionTimeoutIsDrawnFromItsRangeAndRedrawnEachElection`
- **F2-25**: `ACandidateWithAQuorumBecomesLeaderAndSendsHeartbeatsAtOnceAndThenEveryInterval`, `ADeniedOrStaleVoteDoesNotElect`; invariant 1 over simulated runs
- **F2-26**: `ACandidateHearingALeaderOfItsTermStepsDown`
- **F2-27**: `TheElectionTimeoutIsDrawnFromItsRangeAndRedrawnEachElection`
- **F2-28**: `ACandidateWithAQuorumBecomesLeaderAndSendsHeartbeatsAtOnceAndThenEveryInterval` (heartbeats; entries are phase 4's)
