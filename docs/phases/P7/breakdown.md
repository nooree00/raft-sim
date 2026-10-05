# Phase 7 — task breakdown

Log compaction: snapshots and `InstallSnapshot` (spec §11 phase 7, §5 item 5, §8). Format as in
`docs/phases/P6/breakdown.md`, parsed by `gates breakdown`; outcomes will say `(evidence)` or
`(forcing)`.

**Done when** (spec §11): a compacted node and an uncompacted one agree, and every log property
(§8) is accounted for. In addition, carried forward:

- spec §8: "anything that removes data participates in every invariant the data participated in".
  Before compaction exists, every property the log satisfies is enumerated, the four §8 names
  (referential, sequential, reconstructive, evidential) and every one this code depends on that §8
  does not name, each with what preserves it and the test that shows it;
- the invariants are restated where the spec already says how: Leader Append-Only over the logical
  log (snapshot `lastIncludedIndex`/`lastIncludedTerm` plus retained entries); State Machine Safety
  counting an installed snapshot as applying every entry it covers; and the rest checked against
  what a snapshot covers, not only what a log file holds. Tests first for `Raft.Core`: the checkers
  are made snapshot-aware against hand-built traces before any compaction code;
- the register rows promised to phase 7: the key-value snapshots (`KvStateMachine.Snapshot` and
  `Restore` throw today), the latest configuration carried in the snapshot (phase 6, decision 2),
  and the harness's fixed cost (re-promised at the phase-6 report: kept, the only structural
  headroom left);
- the reviewer's lesson from phase 6, applied to every new dimension: an effect is observed from
  the party it happens to (a follower that installed a snapshot, a node whose disk held a
  compaction when it crashed), never inferred from the mechanism that would cause it.

**Approved** (reviewer): P7-00 to P7-12; stop and report after P7-12. Decision 1, the rename as a
barrier held by the world, without pulling phase 10 forward: the barrier reuses the mechanism sends
already have, and disk acknowledgements change the node's interface, which deserves its own phase
rather than arriving as a fix for a model gap. **The barrier is a simplification, not the
contract:** a real file system can make a rename durable without the data, and the barrier assumes
it will not; spec §8 states it as a known limit with the phase-10 row named. Decisions 2 to 5 as
proposed; decision 5 is the ghost-id principle (the reviewer's A3) for the fourth time, after the
ghost entry ids (spec §5), the node-id-free election history (P3) and the configuration the
simulator records (phase 6 decision 6): the simulator records what a snapshot covers and never
reads it from the node. Decision 6, compaction in both soaks and the known limits re-recorded: a
third soak preserving KL-1 and KL-2 would preserve two measurements of a configuration that no
longer exists, and the stale check failing the soak until they are re-recorded is the machinery
doing its job. Both re-recordings go to the reviewer with their curves; if either history becomes
decidable under compaction, that is a result about compaction, reported as one. P7-01 uses a
branch again (the reviewer deletes it).

## Ordering

Tooling and the decision the rest depends on first: P7-00 (the rename barrier in the world's
contract, decision 1, with its positive control), then KL-2's curve on the GitHub runner (P7-01),
asked for at phase 6's acceptance. Then compaction in the order tests-first forces: P7-02 (the log's
properties enumerated, before anything removes data), P7-03 (the state machine's snapshot), P7-04
(the checkers over the logical log, against hand-built traces), P7-05 (the snapshot in the log
file, durable and recovered), P7-06 (compaction in `RaftNode`), P7-07 (`InstallSnapshot`), P7-08
(a compacted node and an uncompacted one agree: the done criterion), P7-09 (the simulator's
compaction dimensions), P7-10 (the positive control), P7-11 (the soaks with compaction, and their
known limits), P7-12 (the harness's fixed cost).

**Blocking set:** P7-00, P7-02, P7-03, P7-04, P7-05, P7-06, P7-07, P7-08, P7-10, P7-11. P7-12 is
blocking only as a register row (`gates register`); P7-01 completes KL-2's record and blocks
nothing.

## Decisions for review

1. **A rename is a barrier, held by the world** (node-interface §4, extended). Spec §8 replaces a
   snapshot by writing a temporary file, fsyncing it and renaming it over the old one. The node
   cannot fsync: it never learns what is durable (P6-15), and the disk model lets a crash keep any
   subset of the writes in flight. Today `SimDisk` treats a rename whose source was never written as
   a no-op, which happens to be safe; a real file system is laxer there (a rename can be durable
   while the temporary file's data is not, and leaves an empty file under the real name), so on
   this point the model is more lenient than reality. Proposed: the world holds a `PersistRename`
   until every persist the node emitted before it is durable, as it holds a `Send` today. That is
   the fsync before the rename, done where the barrier already lives; the node still never waits,
   and the model becomes stricter than or equal to a real file system for this operation. A
   simulator mode that releases a rename early is the positive control (P7-00). Alternative: disk
   acknowledgements as inputs to Core now (the phase-10 register row), a larger change to the
   interface for the one ordering compaction needs. Recommended: the barrier.
2. **The snapshot lives in the log file, replaced by one rename.** Compaction writes a new,
   uniquely named file holding the snapshot record (`lastIncludedIndex`, `lastIncludedTerm`, the
   configuration in effect at `lastIncludedIndex`, the state machine's bytes) followed by the
   retained entries, then renames it over `entries.log`. One rename, so after any crash the node
   has the old log (everything) or the new one (snapshot and suffix), never a snapshot without its
   log or a log missing what the snapshot did not cover. The alternative, a separate snapshot file
   plus a rewritten log, is two renames, and a crash can keep the second without the first.
   Recommended: one file. The term-vote file is unchanged.
3. **When to compact:** when the log holds more than `SnapshotThreshold` entries past the last
   snapshot (an option, default 1,000; the soaks set it low enough that compaction happens in most
   executions), and only up to the commit index applied. Never past what is applied.
4. **`InstallSnapshot` sends the snapshot in chunks** (Figure 13: offset, data, done), each written
   with `PersistWriteAt` into a uniquely named file, installed by the same one rename as decision 2
   once the last chunk arrives. Chunk size is an option with a limit test (§10: the largest
   legitimate snapshot, at the largest legitimate state, installs). Alternative: one message,
   bounded by a size limit (simpler; a large state could never be installed). Recommended: chunks.
5. **The ghost ids a snapshot covers are recorded by the simulator, not read from the node** (the
   reviewer's A3, as phase 6 decision 6): when a snapshot record is written, the simulator records
   which ghost ids the node's log held at indices up to `lastIncludedIndex`, and an install records
   the same for the receiver. The checkers never trust a node's claim about what it covered.
6. **The soaks get compaction, and their known limits are re-measured.** Compaction changes every
   execution's timing, so every client history changes and KL-1's and KL-2's digests stop
   matching. Proposed: compaction on in both soaks (the system as it runs), each soak's undecided
   searches re-measured and re-recorded with their curves, approved by the phase-7 report.
   Alternative: compaction in a third soak, the two existing ones unchanged (the known limits keep
   their histories, at the cost of a third 20-minute required job and soaks that no longer run the
   system as it is). Recommended: compaction in both, known limits re-recorded.

## Tasks

### P7-00 — The rename barrier, and its positive control

- **Task:** Decision 1: the world holds a `PersistRename` until every persist the node emitted before it is durable, in the simulator and in node-interface §4. A simulator control mode releases renames early; with it, a construction (a temporary file written, then renamed, then a crash keeping the rename and losing the write) must leave the node unable to recover what the write held.
- **Vacuity:** A rename that is never issued with a write still in flight never meets the barrier; guarded by the construction asserting the write was pending when the rename was issued. A barrier that holds renames and never releases them passes every safety check and loses every compaction; guarded by asserting the rename completes once the write does. Sabotages: S-barrier-1, the barrier skipped for renames (the construction recovers an empty file); S-barrier-2, renames held forever (the release assertion).
- **Sabotage:** S-barrier-1, S-barrier-2
- **Verifiable here:** yes — `SimDisk`, the simulator and constructions run locally
- **Prediction:** Without the barrier, the construction recovers nothing under the renamed name: today's rename of a missing source is a no-op, so the old file survives, and the case only goes red once the model also stops being lenient, which is decision 1's other half. So the control mode has to model the real hazard (a rename durable before its source's data: an empty file under the real name), and with the barrier it can never arise. **Observable:** the construction with and without the barrier, and with the lenient and the strict model.
- **Outcome:** pending

### P7-01 — KL-2's curve on the GitHub runner

- **Task:** Phase 6's acceptance: KL-1's spec text cites the enforcing runner's numbers and KL-2's does not. Measure KL-2's search on the GitHub runner at 8, 16, 32 and 40 million states (states, peak memory, time), as P5-05 measured KL-1, and record it in the spec and the register beside KL-1's. The mechanism needs the reviewer: P5 used a probe branch (`claude/blissful-goodall-358smj-probe`), which the session could not delete afterwards.
- **Vacuity:** A measurement of another history (the membership soak's seed 8741 under different code) is not KL-2's; guarded by the probe asserting the sub-history's digest before measuring.
- **Sabotage:** ; manual: the probe asserts KL-2's digest, and is run once with a digest one character off to see it refuse
- **Verifiable here:** no — the GitHub runner only
- **Prediction:** On the runner KL-2 stays undecided at every budget up to 40 million states, peaking below KL-1's 12.5 GB at 32 million (7.2 GB locally against KL-1's 12 GB locally), because its search memoises fewer distinct states per step (34 operations against 46). **Observable:** the probe's states, peak memory and verdict per budget.
- **Outcome:** pending

### P7-02 — The log's properties, enumerated before anything removes data

- **Task:** Spec §8's rule. A table of every property the log satisfies and the code that depends on it: the four §8 names, and every other this code relies on, found by reading every reader of the log (`RaftNode`, `LogStore`, `EntryLog.Recover`, the checkers' `LogHistory` and file views, the codec). For each: preserved by compaction or not, how, and the test. Each property gets a test against today's code before compaction exists, so that compaction's change to it is visible.
- **Vacuity:** An enumeration from §8's list alone finds §8's four and nothing else; guarded by deriving it from the readers of the log, each reader's every use listed. A property with a test that compaction cannot reach (no compacted log in it) says nothing; guarded by each test taking a log as input, so that P7-06 can feed it a compacted one. Sabotages: S-logprop-1, the boundary property (the first retained entry chains onto `lastIncludedTerm`) broken in recovery; S-logprop-2, the configuration lookup reading only retained entries.
- **Sabotage:** S-logprop-1, S-logprop-2
- **Verifiable here:** yes — unit tests
- **Prediction:** The enumeration finds at least three properties §8 does not name that compaction can break, and the first P7-06 breaks is the chain check: recovery takes a record only if its `previousTerm` matches the entry before it (P4-02's unchained-record rule), and the first retained entry's predecessor is in the snapshot, not the log, so a naive compaction makes recovery drop every retained entry as unchained. The other two I expect: the configuration lookup (`ConfigurationIndexAtOrBelow` scans the log, phase 6) and the checkers' incremental file view (`FileView` resumes from an offset, which a rename invalidates). **Observable:** the table's rows beyond §8's four; P7-06's first red test by name.
- **Outcome:** pending

### P7-03 — The state machine's snapshot

- **Task:** `KvStateMachine.Snapshot` and `Restore` (register row): canonical bytes, so equal states give equal snapshots; restore then apply equals apply throughout. Tests first, with a property test over random command sequences.
- **Vacuity:** A round trip over an empty or one-key state passes a serialisation that drops what it never saw; guarded by the property test over sequences using every command kind on every key, comparing every key's value and the bytes. Sabotages: S-kvsnap-1, deletes not represented (a deleted key returns after restore); S-kvsnap-2, dictionary order in the bytes (two equal states, two snapshots).
- **Sabotage:** S-kvsnap-1, S-kvsnap-2
- **Verifiable here:** yes — unit tests
- **Prediction:** The first implementation fails the canonical-bytes test, not the round trip: a dictionary's enumeration order depends on insertion history, so two states equal by content serialise differently. **Observable:** which of the two tests fails first.
- **Outcome:** pending

### P7-04 — The checkers over the logical log, against hand-built traces

- **Task:** Before any compaction code: the simulator records a snapshot's coverage (decision 5); Leader Append-Only over the logical log; State Machine Safety counting an install as applying every covered ghost id; Log Matching, Leader Completeness, committed-durable and no-spurious-commit reading what a node holds as its snapshot's coverage plus its retained entries; commitment in fact counting a snapshot that covers an entry as holding it. Hand-built traces: a correct compaction (accepted), a compaction past the applied index (rejected), an install of a snapshot covering a different entry at an index the receiver applied (rejected), a committed entry held only in snapshots on a quorum (still committed and durable).
- **Vacuity:** A checker that ignores snapshots accepts the correct trace and rejects nothing it should not, but also misses the bad ones; guarded by the two rejecting traces. A checker that counts a snapshot as holding everything below its index accepts a snapshot built from a different log; guarded by the coverage being ghost ids, not an index. Sabotages: S-compact-1, coverage taken from the index alone; S-compact-2, an install not counted as applying.
- **Sabotage:** S-compact-1, S-compact-2
- **Verifiable here:** yes — hand-built traces
- **Prediction:** Of today's checkers, committed-durable (invariant 6) is the one a correct compaction turns red first: it reads entries from the entry-log file view, and a compacted log no longer holds the entries its snapshot covers, so a correct node looks as if it lost committed entries. Leader Append-Only, stated over the physical log today, is the second. **Observable:** today's checkers against the correct-compaction trace, which one fails first.
- **Outcome:** pending

### P7-05 — The snapshot in the log file, durable and recovered

- **Task:** Decision 2: the snapshot record at the head of `entries.log`; compaction as one uniquely named file and one rename (behind the P7-00 barrier); recovery reads the snapshot and then the retained entries; the crash-during-write test extended to crash at every point of a compaction in every loss mode, over several cycles, each recovery a prefix of something written and never less than the last durable snapshot covered.
- **Vacuity:** A crash test whose crashes never land during a compaction says nothing about it; guarded by counting crashes with the compaction's file or rename in flight, required non-zero for each. Sabotages: S-snapfile-1, the snapshot written to `entries.log` directly, not renamed (a torn snapshot loses the log); S-snapfile-2, the temporary file's name reused (a stale file installed).
- **Sabotage:** S-snapfile-1, S-snapfile-2
- **Verifiable here:** yes — unit tests and the crash test
- **Prediction:** The crash test passes on the first run with the barrier, and its counts show the rename in flight at a crash in under 5% of the crash points, because the rename is held until the file it renames is durable and is pending only for the one write it waits behind. Without the barrier (the P7-00 control), at least one crash point recovers less than the last durable snapshot covered. **Observable:** the crash test's counts and verdicts with and without the barrier.
- **Outcome:** pending

### P7-06 — Compaction in `RaftNode`

- **Task:** Decision 3. `LogStore` with a base (`lastIncludedIndex`, `lastIncludedTerm`); every index read through it; the configuration at an index found in the snapshot when the index is at or below its base (register row); `matchIndex`, `nextIndex`, the election restriction (`lastLogIndex`/`lastLogTerm` with an empty retained log) and the AppendEntries consistency check at the boundary (`prevLogIndex == lastIncludedIndex`). The §10 limit test: the largest legitimate threshold, at the largest command size, compacts and recovers.
- **Vacuity:** A cluster that never reaches the threshold never compacts; guarded by constructions with a small threshold and a floor on compactions in the sample. A compaction at a leader only misses the follower paths; guarded by constructions compacting each role. Sabotages: S-compact-3, compaction past the applied index; S-compact-4, the consistency check at the boundary reading the retained log only (a follower whose first retained entry is the leader's next refuses forever).
- **Sabotage:** S-compact-3, S-compact-4
- **Verifiable here:** yes — `ManualCluster` and the simulator
- **Prediction:** The first construction to fail is the follower whose whole retained log is empty after compacting: its `lastLogTerm` is read from the retained entries (term 0 when there are none), so it grants votes to candidates with stale logs, which the election restriction forbids. **Observable:** the construction red by name, with the vote granted.
- **Outcome:** pending

### P7-07 — `InstallSnapshot`

- **Task:** Decision 4. The leader sends chunks when a follower's `nextIndex` is at or below its `lastIncludedIndex`; the follower writes them at their offsets to a uniquely named file, renames it into place on the last, restores the state machine, and keeps the log suffix past the snapshot if its entry at `lastIncludedIndex` has `lastIncludedTerm`, else discards the log (Figure 13). Codec messages for both directions, canonical as P3-02 requires.
- **Vacuity:** A follower that never falls behind the leader's snapshot never receives one; guarded by a construction isolating a follower across two compactions, and a floor on installs in the sample (P7-09). A chunked install whose chunks always arrive in order and once never meets reordering or duplication; guarded by constructions delivering chunks reordered, duplicated and with one lost. Sabotages: S-install-1, the suffix always discarded (a follower ahead of the snapshot loses committed entries it held); S-install-2, a chunk applied at the wrong offset.
- **Sabotage:** S-install-1, S-install-2
- **Verifiable here:** yes — constructions and the simulator
- **Prediction:** Duplicated chunks are harmless (a positioned write is idempotent) and lost ones stall until the leader resends; the case that breaks is a stale chunk from an earlier snapshot arriving after a newer snapshot's first chunk, written into the newer file because both are keyed by the follower alone. Keying the file by `lastIncludedIndex` is the fix I expect to need. **Observable:** the stale-chunk construction against the first implementation.
- **Outcome:** pending

### P7-08 — A compacted node and an uncompacted one agree (the done criterion)

- **Task:** In one cluster, nodes with compaction and nodes without (the option per node): every applied ghost id at every index identical, every state machine's bytes identical at equal applied indices, over constructions and the generated sample. Agreement is checked against the ghost ids and the state machine's canonical bytes, never by comparing one node's log to another's.
- **Vacuity:** Spec §10: "a test comparing two parties proves nothing if one party's answer came from the other", and an uncompacted follower's state comes from a leader that may be compacted. Guarded: the comparison is to the ghost ids recorded by the simulator, and a node's state is compared with a replay of the committed ghost ids from the empty state, not with another node. Sabotage: S-agree-1, a restored state machine that drops one key; the comparison must catch it in the construction.
- **Sabotage:** S-agree-1
- **Verifiable here:** yes — constructions and the simulator
- **Prediction:** The replay comparison finds no disagreement in the sample, and S-agree-1 is caught by it and by linearizability both, the replay first (it compares every key at every applied index, linearizability only keys a client read after the restore). **Observable:** the sample's disagreements (none) and S-agree-1's first failing check.
- **Outcome:** pending

### P7-09 — The simulator's compaction dimensions, observed from the party they happen to

- **Task:** Coverage dimensions, each an effect observed where it happens (phase 6's lesson): a follower installed a snapshot (the follower's disk took the rename of an installed snapshot), a node restarted from a snapshot (its recovered log begins with a snapshot record), a crash with a compaction's write or rename in flight (that node's disk), an install that kept the suffix and one that discarded it (the follower's log after), a compaction during joint consensus (the snapshot record's configuration is joint). Each with a floor in the sample and the soaks.
- **Vacuity:** A dimension counted from the leader's decision to send a snapshot measures the mechanism, not the effect (the isolation detector's error); guarded by each dimension reading the receiving node's disk. Sabotage: S-cov-13, installs counted when sent: in a sample with a follower partitioned during every install, the sent count stays up while the installed count is zero, and the sabotaged dimension misses it.
- **Sabotage:** S-cov-13
- **Verifiable here:** yes — the simulator
- **Prediction:** "An install that discarded the suffix" is the rarest and below the floor at first: it needs a follower holding entries past the snapshot that conflict with it, which takes a leader change between the follower's divergence and the snapshot, while most installs reach followers whose logs simply end before the snapshot's index. **Observable:** each dimension's count over the 300-execution samples.
- **Outcome:** pending

### P7-10 — The positive control: a compaction that discards an uncommitted entry

- **Task:** A variant of `RaftNode` that compacts up to its last log index, not its applied commit index, must turn State Machine Safety or committed-durable red: in a construction (a leader compacts an entry that a new leader then overwrites) and in the generated sample. The real node must not. This phase's positive control, in the role Figure 10 played for phase 6.
- **Vacuity:** A construction in which the compacted uncommitted entry is never overwritten passes with both nodes; guarded by asserting that a later leader's entry at that index has a different ghost id before asserting the verdict. Sabotage: S-compact-5, the variant in the real node.
- **Sabotage:** S-compact-5
- **Verifiable here:** yes — constructions and the simulator
- **Prediction:** The construction goes red on State Machine Safety (the snapshot counts as applying the overwritten entry, decision 5), and the sample catches the variant in more than 3% of executions, since phase 6's two wrong predictions were the same misjudgment of how often the generator's faults line up with a window, and here the window is every compaction that runs ahead of commitment. **Observable:** the construction's verdict for both nodes, and the variant's red executions over the 300-execution sample.
- **Outcome:** pending

### P7-11 — The soaks with compaction, and their known limits

- **Task:** Decision 6. Compaction on in both soaks, at a threshold that compacts in most executions; every invariant, linearizability, the effects and the P7-09 dimensions with their floors. KL-1 and KL-2 re-measured: each soak's undecided searches recorded with their local curves, approved by the phase-7 report, the old entries removed.
- **Vacuity:** A soak whose threshold is never reached checks no compaction; guarded by a floor on executions with at least one compaction, and one with an install. Sabotage: S-soak-10, compaction off in the membership soak: the compaction floor goes red.
- **Sabotage:** S-soak-10
- **Verifiable here:** partial — the soaks run locally; the CI jobs only in CI
- **Prediction:** Deterministic quantities only (P5's constraint): with compaction, both soaks' undecided searches change identity (no seed of KL-1 or KL-2 stays undecided with the same history) but not number (one each, give or take one), because compaction changes timing, not the workload's shape; and no invariant is violated in either. **Observable:** each soak's undecided list and verdict, against phase 6's.
- **Outcome:** pending

### P7-12 — The harness's fixed cost (register, kept at phase 7)

- **Task:** The register row: what remains is each scale shard's `Raft.Scale.Tests` baseline check, 86-131 s on GitHub, run by every worker that holds a target in that project, four workers on four cores at once. Measure whether running each project's baseline check once per shard, before the workers split, lowers the slowest worker's ready time; close the row with that change and its test, or argue it again with the measurement.
- **Vacuity:** A shared baseline that checks a different filter from the one a worker's entries need would pass while checking less; guarded by the shared run covering the union of the shard's targets and the harness asserting each worker's targets are in it. Sabotage: S-harness-4, a worker's targets left out of the shared baseline: the harness's assertion fails.
- **Sabotage:** S-harness-4
- **Verifiable here:** partial — the split locally; the effect on GitHub in CI
- **Prediction:** Within one CI run, a shard's ready time with the shared baseline is lower than its four-worker baseline by at least a third of the `Raft.Scale.Tests` baseline, because the four concurrent runs contend for four cores and the single run does not. **Observable:** the per-worker split lines in one run before and one after, each shard against itself.
- **Outcome:** pending

## Sabotage ids

New series: S-barrier (P7-00), S-logprop (P7-02), S-kvsnap (P7-03), S-compact (P7-04, P7-06,
P7-10), S-snapfile (P7-05), S-install (P7-07), S-agree (P7-08). S-cov-13 follows S-cov-12,
S-soak-10 follows S-soak-9, S-harness-4 follows S-harness-3. Each id's `sabotage/<id>/` entry
lands in the same commit as the check it proves, and is run on that commit before it is pushed.

## Register rows opened here

None yet. Session tables in the snapshot belong to phase 8 (spec §5 item 6) and will be opened
there.
