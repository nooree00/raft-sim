# Phase 1 — task breakdown

The simulator, before any Raft (spec §7, §11 phase 1). Format as in
`docs/phases/P0/breakdown.md`, parsed by `gates breakdown`; outcomes will say `(evidence)` or
`(forcing)`.

**Done when** (spec §11, as amended at review): a seeded execution of three
trivial-protocol nodes reproduces byte-identically across separate processes; every fault in §7 is
injectable and proven to fire by its effect; the node↔world interface contract is written and
traced against every input and output Figure 2 needs; the generated distribution is measured and no
dimension is zero; a planted failure shrinks to its minimal schedule.

## Ordering

P1-01 → P1-02 → P1-03 → P1-04, then the fault families P1-05, P1-06, P1-07 (any order), then
P1-08 → P1-09 → P1-10 → P1-11. P1-12 (the register row promised to P1) is independent and goes
first if phase 1 runs long, because it closes a detection gap that affects every later commit.

**Blocking set:** P1-01 through P1-10 and P1-12. **Deferrable if phase 1 runs long:** P1-11, the
shrinker, to phase 3 — it must exist before phase 3's 10,000-execution runs produce failures worth
shrinking, and not later. Moving it is a spec change, argued first.

## Decisions (all approved at review)

1. **No Serilog** — dropped from spec §3 entirely, not deferred. Spec §3 names it for the drivers, but the simulator's trace is its
   own canonical format (P1-08), because byte-identity across processes is the requirement and a
   logging library's formatting is not something to hold byte-identical. Proposal: defer Serilog to
   phase 9 (the TCP host), where a person reads logs, or drop it.
2. **Messages are bytes.** `Send` carries a serialized payload, not an object. The simulator can
   then model message size (snapshots are large), duplicate and corrupt at the byte level, and write
   canonical traces; and the TCP host (phase 9) reuses the same encoding. Cost: Core owns a message
   codec from phase 3.
3. **A tool project for cross-process runs**, `tools/Raft.SimRun` (→ Simulation, Core), which also
   holds the trivial protocol. The simulator never references the protocol it tests.
4. **Spec §11 phase 1 row amended** to add the coverage and shrinking criteria above; §7 already
   requires both, the phase table omitted them.

## Tasks

### P1-01 — Node↔world interface contract and Figure 2 traceability table

- **Task:** `docs/design/node-interface.md`: the node as a deterministic, closed state machine; its inputs (start from durable state, tick, receive, client request), its ordered effects (persist, send, client response, structured event), the persist barrier (a node's send is released only after every persist the node emitted earlier is durable — per node, not per step), the injected randomness, and what "crash" and "pause" mean at the interface. Then a table with one row per element of the paper's Figure 2 — and Figure 13 (InstallSnapshot), §6 (configuration entries) and §8 (client interaction) — naming the interface member that carries it, the source text, and the simulator fault that stresses it. `docs/design/figure2-checklist.md` transcribes Figure 2's items in the paper's own words; a test requires one table row per checklist item, and that every member a row names exists in the built Raft.Core assembly.
- **Vacuity:** A table that lists Figure 2's items against members that do not exist, or cannot express them — a paper checkbox. Guarded by the reflection check (a row naming a member that does not exist fails) and by the checklist taken from the paper rather than from my memory of it. Residual: a member that exists but cannot actually carry its rule is found only when phase 3 uses it.
- **Sabotage:** S-trace-1, S-trace-2
- **Verifiable here:** yes — the table test runs locally; the checklist is checked against `docs/references/raft-extended.pdf`.
- **Prediction:** Figure 2 alone is insufficient for at least three rows, which must cite text outside it: persisting the candidate's own vote before it sends RequestVote (§5.2/§8, spec A7), the leader counting its own entry toward a majority only once durable, and the election timer being reset on granting a vote (Figure 2 states it only inside the candidate-conversion rule). **Observable:** the table's source column cites text outside Figure 2 for three or more rows.
- **Outcome:** pending

### P1-02 — Core interface types

- **Task:** In Raft.Core: `NodeId`, the input and effect types of P1-01, the node interface, the injected randomness interface, and a byte-level persist record abstraction. No implementation of any protocol. The ambient-dependency scan (P0-04) must still pass with the allowlist unchanged except for additions justified in the commit.
- **Vacuity:** Types that compile but that no test constructs, so the contract is unexercised until phase 3. Guarded by P1-04, which drives the trivial protocol through exactly these types, and by the P1-01 reflection check.
- **Sabotage:** S-core-1
- **Verifiable here:** yes — local build and the architecture tests.
- **Prediction:** Expressing effects as a sealed hierarchy of records makes the compiler emit references to `System.Runtime.CompilerServices` types not yet on the allowlist (`IsExternalInit` is there; I expect `RequiredMemberAttribute` / `SetsRequiredMembersAttribute` or `NullableAttribute` on generic effect payloads). **Observable:** the ambient scan fails on its first run after this task, listing a CompilerServices type that is not on the allowlist.
- **Outcome:** pending

### P1-03 — Deterministic randomness streams

- **Task:** In Raft.Simulation: a hand-written PRNG (not `System.Random`, whose algorithm is not a compatibility promise) and stream derivation by hashing (seed, purpose, index) with a stable, hand-written hash — never `string.GetHashCode`. Tests: golden values for known seeds committed as constants; the same (seed, purpose) gives the same stream; drawing more from one purpose does not change another purpose's draws; streams for purposes differing in one character are uncorrelated.
- **Vacuity:** A stream compared only with itself in the same process passes even when it depends on per-process state; guarded by committed golden values, which a per-process hash cannot reproduce.
- **Sabotage:** S-rng-1, S-rng-2
- **Verifiable here:** yes — local.
- **Prediction:** With a weak mixer (for instance XOR-combining the component hashes) streams whose purposes differ in one character come out correlated. **Observable:** the independence test on the first mixer I write measures a correlation above 0.1 between the first 1,000 draws of two such purposes.
- **Outcome:** pending

### P1-04 — Trivial protocol and the simulation runner

- **Task:** `tools/Raft.SimRun` (→ Simulation, Core): an echo-plus-persisted-counter protocol implementing the Core node interface — each node periodically sends numbered messages to its peers, echoes what it receives, and persists a counter before acknowledging it — and a CLI that runs a schedule and writes a trace. In Raft.Simulation: the runner — a logical clock, an event queue ordered by (time, sequence number), per-node inputs and effects, with no faults yet. Tests: three nodes run for a simulated ten minutes; every sent message is delivered exactly once; every acknowledged counter value is durable.
- **Vacuity:** A protocol that sends nothing, so every later fault test acts on nothing — the reason do-nothing nodes were rejected. Guarded by asserting message and persist counts per node, derived from the trace.
- **Sabotage:** S-sim-1, S-sim-2
- **Verifiable here:** yes — local.
- **Prediction:** A ten-minute simulated run with ticks every simulated millisecond is dominated by tick dispatch, not messages. **Observable:** the first version takes more than one second of wall time for the ten-minute run, and a profile shows most steps are ticks that change nothing.
- **Outcome:** pending

### P1-05 — Network faults

- **Task:** Delay, reorder, duplicate, drop, asymmetric partition (A reaches B, B does not reach A) and healing, each proven to fire by its effect at the receiver: a dropped message never appears in the receiver's received log; a duplicate appears twice; a reordered pair arrives in the opposite order to sending; under an asymmetric partition A→B arrives and B→A does not; after healing, delivery resumes both ways.
- **Vacuity:** A fault counted where it is injected while the message is still delivered (a drop applied to a copy). Guarded: every assertion is on the receiver's state, never on an injection counter.
- **Sabotage:** S-net-1, S-net-2, S-net-3, S-net-4, S-net-5
- **Verifiable here:** yes — local.
- **Prediction:** .NET's `PriorityQueue` is not stable, so if two deliveries at the same logical time were ordered by the heap alone, removing an unrelated event would change their order — breaking the shrinker's precondition that removing one fault leaves other draws alone. **Observable:** a test that removes one unrelated message from a schedule and compares the relative delivery order of two same-time messages fails if the sequence-number tiebreak is removed (S-net-5 checks exactly this).
- **Outcome:** pending

### P1-06 — Disk faults and the persist barrier

- **Task:** A per-node simulated disk with a write buffer and fsync barrier: unsynced writes lost on crash, writes reordered before a barrier, a torn final write (a prefix of the last unsynced append), rename, slow disk, and the positive-control fault — fsynced data lost — which is off by default and exists to make phase 3's invariants go red. The simulator enforces the persist barrier and has a deliberate barrier-violation mode as a second positive control. Proven by effect: after a crash between write and fsync the counter reads the old value; a torn write leaves exactly a prefix; with a slow disk the send that follows a persist is delayed by at least the latency; an acknowledged counter value survives any crash — unless the barrier-violation mode is on, when that property must fail.
- **Vacuity:** Crashes that only happen when nothing is pending, so loss is never exercised. Guarded by tests that schedule the crash inside the write–fsync window explicitly, and by P1-10's coverage dimension "crash with unsynced writes".
- **Sabotage:** S-disk-1, S-disk-2, S-disk-3, S-disk-4
- **Verifiable here:** yes — local.
- **Prediction:** My first barrier holds back only the sends emitted in the same step as a persist, not sends from a later step that follow an earlier, still-pending persist. **Observable:** a test where step 1 persists on a slow disk and step 2 sends without persisting shows step 2's send released before step 1's persist is durable.
- **Outcome:** pending

### P1-07 — Node lifecycle and clock faults

- **Task:** Crash (discard the node object, rebuild it from its disk alone), restart timing, pause and resume (the object is kept; no ticks or deliveries while paused; the node resumes believing its old state), and clock skew (per-node tick rates as integer ratios, no floating point). Proven by effect: after a crash volatile state is gone and durable state is back; a paused node's state is unchanged across the pause and its backlog arrives on resume; two skewed nodes' tick counts diverge in the configured ratio.
- **Vacuity:** Pause implemented as crash-and-restart passes any test that looks only at durable state. Guarded by a test that a paused node keeps its volatile state, which a crash cannot.
- **Sabotage:** S-life-1, S-life-2, S-life-3
- **Verifiable here:** yes — local.
- **Prediction:** My first pause delivers the ticks missed during the pause one by one on resume, so a ten-minute pause costs 600,000 steps. **Observable:** a ten-minute simulated pause takes more than one second of wall time, and the node's step count jumps by the number of missed ticks rather than by one.
- **Outcome:** pending

### P1-08 — Determinism across processes, canonical trace, ambient scan of the simulator

- **Task:** A canonical, line-oriented trace (logical time, node, event kind, fields in a fixed order, per-node state digest) that a person can read. A test runs the same schedule in two separate processes through `Raft.SimRun` and compares trace bytes; a committed golden trace for one schedule guards against drift across SDK patches; two different seeds must give different traces. The P0-04 allowlist scan is extended to Raft.Simulation, with its own allowlist.
- **Vacuity:** A trace too coarse to differ (only counts) is trivially identical across processes. Guarded by the seed-sensitivity test and by the per-event content.
- **Sabotage:** S-det-1, S-det-2, S-det-3, S-det-4
- **Verifiable here:** yes — local; the separate processes run in the pinned image.
- **Prediction:** The first cross-process comparison passes, because `Dictionary` and `HashSet` enumeration order depends on insertion and removal history, not on hash codes, so per-process string-hash randomisation does not reach the trace; a sabotage that merely enumerates a `HashSet<string>` survives, and S-det-1 must order by `GetHashCode` explicitly to go red. **Observable:** first run green; a plain-`HashSet` variant of S-det-1 survives the harness.
- **Outcome:** pending

### P1-09 — Schedule generator, serialization and the reproduction round trip

- **Task:** Seed → generator → explicit fault schedule (data) → runner (spec §7 as amended). The schedule serializes to a stable text form with its generator-config hash and commit SHA; a failing run prints the reproduction artifact; a test takes that printed text, reruns it, and gets the same failing trace — the round trip exercised deliberately. In-run randomness comes only from P1-03 streams.
- **Vacuity:** A runner that parses a schedule field and ignores it passes every test that uses generated schedules. Guarded by a per-field effect test (a schedule containing only one fault shows that fault's effect in the trace) and by P1-10's effect-derived coverage.
- **Sabotage:** S-sched-1, S-sched-2, S-sched-3
- **Verifiable here:** yes — local.
- **Prediction:** Per-window fault probabilities compound over a long run, so my first generator puts a partition in nearly every schedule. **Observable:** P1-10's first report shows "had a partition" at 90% or more.
- **Outcome:** pending

### P1-10 — Coverage measured from what happened

- **Task:** For every generated execution, dimensions computed from the trace — the effect, never the schedule: had an asymmetric partition that blocked a message, delivered a duplicate, delivered a pair out of order, crashed with unsynced writes, left a torn record, paused a node, skewed clocks beyond a threshold. The report is printed per suite run; a dimension at 0% or 100% fails the build unless declared always-on, and two dimensions with identical counts fail as one measurement wearing two names unless explicitly allowed with a reason.
- **Vacuity:** Counting the schedule's intentions rather than the effects shows full coverage for faults the runner never applied. Guarded by deriving every dimension from trace events, and proven by S-cov-2 (the runner drops a fault the schedule still contains; coverage must fall to zero).
- **Sabotage:** S-cov-1, S-cov-2, S-cov-3
- **Verifiable here:** yes — local.
- **Prediction:** The trivial counter node persists on nearly every step, so almost every crash lands with unsynced writes. **Observable:** "crashed" and "crashed with unsynced writes" report identical counts in the first report, and the identical-counts rule fails the build.
- **Outcome:** pending

### P1-11 — Shrinker

- **Task:** Delta debugging over a failing schedule's fault events, then over each remaining event's parameters; every candidate is re-run through the runner; a candidate counts only if it fails with the same failure signature (which property, which node); the result is checked to be 1-minimal (removing any single remaining event makes it pass). Proven on a planted bug: a variant of the counter protocol that violates its property only when a crash with unsynced writes and a duplicate delivery coincide, hidden in a generated schedule of at least 100 events; the shrinker must return a schedule of at most a handful of events containing both. A second planted case, added at review: a bug whose cause is the *order* of two faults — it fails only when fault A precedes fault B, and passes if either is removed or the two are swapped. After shrinking, a swap probe tries each adjacent pair of the result in reverse order and reports the pairs whose order is part of the cause.
- **Vacuity:** A shrinker that accepts any failing candidate converges on whatever fails most easily — possibly a different bug, or a runner crash. Guarded by the failure-signature rule and a second planted bug with a different signature in the same schedule.
- **Sabotage:** S-shrink-1, S-shrink-2, S-shrink-3
- **Verifiable here:** yes — local.
- **Prediction:** Crash and restart are separate events, so removing a restart alone leaves a node down for good, which changes the failure signature; the shrinker cannot remove such pairs one event at a time and stalls above the minimum. **Observable:** the first shrink of the planted bug keeps extra restart or heal events — more than twice the minimal size — until paired events are shrunk as a unit. (Added at review, before implementation.) For the ordering case the reviewer expects a removal-only shrinker to stall; I predict it does not: removal preserves the relative order of the events it keeps, and a failing schedule necessarily contains the failing order, so removal reaches the two events in their failing order. What removal cannot do is say that the order is the cause — hence the swap probe. **Observable:** on the ordering case, removal alone yields exactly the two events in the failing order, and the swap probe marks that pair order-sensitive.
- **Outcome:** pending

### P1-12 — Every commit gets the gates and the harness (register row, promised to P1)

- **Task:** The each-commit job becomes a setup job that lists the push's non-head commits and a dynamic matrix with one job per commit, each running the preflight, the build, the gates, the tests and the sabotage harness from that commit's own scripts; a final collect job requires one result per listed commit. Commits from before a script existed are reported as pre-gate, not passed.
- **Vacuity:** A matrix built from an empty list runs zero jobs and reports green — the zero-job CI again. Guarded by the collect job (`gates each-commit-collect`), which compares the list with the results and fails on any difference, including an empty list when the push carried commits.
- **Sabotage:** S-each-3, S-each-4
- **Verifiable here:** partial — the collect logic is tested locally on fixtures; the matrix itself only in CI, read back through the GitHub tools.
- **Prediction:** On a new branch the range falls back to the merge-base with main and includes commits from before the gates existed; my first matrix fails on them instead of classifying them as pre-gate. **Observable:** the first run of the new job on a fresh branch is red on a phase-0 commit that has no `tools/Raft.Gates`.
- **Outcome:** pending

## Sabotage ids

Ids already used in phase 0 are not reused; S-each-3/4 continue S-each-1/2. Each id's
`sabotage/<id>/` entry lands in the same commit as the check it proves; `gates breakdown` requires
it once the task has its first commit.
