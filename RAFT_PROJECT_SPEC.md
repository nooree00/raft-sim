# Raft, with a deterministic simulator that tries to break it

This document is the contract for the project. Every change must be justifiable
against it. If a requirement here is wrong or impossible, say so and propose a
change to this file rather than silently deviating.

---

## 1. Goal

An implementation of the Raft consensus algorithm, from the paper, with a
deterministic fault-injecting simulator as an equal deliverable. The simulator
is not test scaffolding — it is half the project, and the half that makes the
other half trustworthy.

The algorithm is Raft as specified in Ongaro & Ousterhout, *In Search of an
Understandable Consensus Algorithm (Extended Version)*, Stanford, May 2014
(`docs/references/raft-extended.pdf`). Figure 2 is the normative summary;
Figure 3 lists the safety properties.

Do not add a Raft library. Not `dotnet-raft`, not a port, not "temporarily to
unblock". The algorithm is the project.

## 2. Non-goals

Out of scope. Do not build these, and do not add abstractions in anticipation:

- Byzantine fault tolerance. Crash-recovery model only.
- A production RPC layer. The transport is an interface; the simulator and a
  simple TCP implementation are the only two.
- Multi-Raft / sharding.
- A user-facing application. The state machine is a key-value store because it
  is the simplest thing a linearizability checker can reason about.
- Performance optimisation (batching, pipelining, leader leases) until §11's
  final phase, and only against a measured baseline.
- PreVote and CheckQuorum (Ongaro's thesis). The paper's §6 disruption rule
  is in scope (§5); the thesis extensions are not. Excluded with its
  consequence measured (P4-06): under a one-way partition that never heals,
  a leader that can send but not hear keeps office and commits nothing. It
  was the leader in 49 of 200 seeds of that construction, and those seeds
  are counted, not asserted; invariant 11 asks for progress only after
  faults heal.

## 3. Stack

- .NET 10, C# 14, nullable enabled, warnings as errors
- xUnit, and a hand-rolled generator and shrinker (see §7)
- No logging library. `Raft.Core` emits structured events as *outputs* (§4,
  §9); the simulator writes them into its own canonical trace, which must be
  byte-identical across processes. A logging library is a phase-9 question, to
  be argued on its own merits.
- Docker Compose for the multi-process phase only

Pin the SDK image **by digest**, not by tag — a tag, even an exact-patch tag,
can be re-pushed — and set `global.json` to an exact SDK version with
`rollForward: latestPatch`. A floating `sdk:10.0` tag and a
`rollForward: latestFeature` are a pinned-looking configuration that pins
nothing — a different Roslyn feature band will reject code the gate accepted.
(Concretely: Ubuntu 24.04's archive ships SDK 10.0.1xx, Microsoft's image
10.0.4xx; `latestPatch` never crosses a feature band, so a mismatched host
fails loudly, which is the intent.)

Pinning also covers what the SDK does not: NuGet lock files with locked-mode
restore in CI, and a pinned `AnalysisLevel`, because a new SDK patch can add an
analyzer warning that warnings-as-errors turns into a broken build. CI runs
inside the pinned image, not on an SDK installed by a setup action.

## 4. Architecture

```
src/
  Raft.Core/          the algorithm. No I/O, no clock, no threads, no ASP.NET
  Raft.Simulation/    deterministic network, seeded, reproducible
  Raft.Checker/       linearizability checker
  Raft.Kv/            the key-value state machine (pure; no transport)
  Raft.Host/          TCP transport and process host
tools/
  Raft.Gates/         the §12 process gates, as a program (see §12)
tests/
  Raft.Architecture.Tests/ layering and ambient-dependency scans
  Raft.Gates.Tests/        the gates' own tests
  Raft.Core.Tests/         Figure 3's properties as executable invariants
  Raft.Simulation.Tests/   the simulator's own faults, and its own guards
  Raft.Checker.Tests/      histories the checker must accept and must reject
  Raft.Kv.Tests/           the state machine
  Raft.Host.Tests/         integration, real sockets
```

A test project is created in the phase that first gives it a real test, not
before; the test-count floor (§12) fails any project that runs zero tests.

Dependency direction is a partial order: `Core → nothing`,
`Checker → nothing`, `Kv → Core`, `Simulation → Core`, `Host → {Core, Kv}`,
`Gates → nothing`. Tests compose whatever they need. The simulator does not
ship in the product. `Raft.Core` references the BCL only and has no package
references. Enforce it with a test that reads the *resolved* project graph
(so a reference injected by `Directory.Build.props` is caught), not by relying
on the compiler, and that fails on any project not in its table (otherwise a
new project is unconstrained) — and prove that test non-vacuous by adding a
violating reference and watching it go red.

**`Raft.Core` takes no ambient dependencies.** No `DateTime.Now`, no
`Task.Delay`, no `Random`, no threads. Time arrives as a tick the caller
advances; messages arrive as a call; randomness arrives as an injected source.
This is what makes §7 possible, and it is the single most important structural
decision in the project. A node is a deterministic, closed state machine:
(state, input) → (state, outputs). "Pure" means deterministic and closed, not
immutable — copying the log on every step is O(n) and will not reach 10,000
executions. A crash is modelled by discarding the node object and
reconstructing it from its disk alone, so volatile state has no path to
survive.

What this decision does and does not remove:

- **Persistence is relocated, not removed.** Core returns an *ordered* list of
  effects. The contract: a `Send` is released only after every `Persist` that
  precedes it in the list is durable. Both drivers (simulator, TCP host) must
  honour it, and the simulator must be able to violate it deliberately, as a
  sabotage. (Disk acknowledgements as inputs are more faithful and more
  complex; that is a register item, not a phase-1 design.)
- **Logging is an output.** Core emits structured events; drivers record them
  (the simulator in its canonical trace).
- **The state machine is injected** as a pure interface (apply, snapshot,
  restore), which compaction and `InstallSnapshot` need.
- **The project graph cannot enforce this**, because `DateTime`, `Random`,
  `Thread` and `Task.Delay` are all BCL. A text scan is defeated by aliases,
  `Environment.TickCount`, `TimeProvider.System`, or per-process-randomized
  `string.GetHashCode`. So the compiled `Raft.Core` assembly's metadata is
  scanned against an **allowlist** that fails closed. Determinism is also
  checked **across separate processes**: .NET randomizes string hashing per
  process, which an in-process repeat cannot see.

## 5. The algorithm

Implement in this order, because each is separately testable:

1. **Leader election** — terms, `RequestVote`, randomized election timeouts,
   the three states.
2. **Log replication** — `AppendEntries`, `nextIndex`/`matchIndex`, commit index
   advancement by majority.
3. **Persistence** — `currentTerm`, `votedFor` and the log survive a crash,
   durable before any message is sent, or any decision is made, that depends
   on them (§8). `currentTerm` and `votedFor` are persisted from the election phase (phase 3), so
   that election can be tested under crashes.
4. **Membership changes** — joint consensus (§6 of the paper), not the
   single-server-at-a-time simplification. The paper's §6 disruption rule
   (ignore `RequestVote` within the minimum election timeout of hearing from a
   current leader) is implemented earlier, in the election phase (phase 3), because that phase
   injects asymmetric partitions and must be able to assert liveness under
   them (invariant 11).
5. **Log compaction** — snapshots, `InstallSnapshot`.
6. **Client interaction** — §8 of the paper: session-based deduplication so a
   retried command is not applied twice, and read-only queries that do not
   return stale data. The session table is part of the snapshot; otherwise a
   retry after compaction is applied again.

Items 4 to 6 are where most implementations stop, and they are where the
interesting bugs are. Do not defer them into a phase that never happens.

### Invariants (Figure 3)

These are the acceptance criteria for `Raft.Core`. Each is a checker over a
simulated execution's trace and durable state, observed by the simulator — not
a node's report about itself.

Two instruments make the statements checkable:

- **Ghost entry ids.** When a leader creates an entry, the simulator records a
  unique id for it. Entries are compared by id, never by command bytes (two
  distinct `Put x 1` entries compare equal by bytes).
- **Commitment in fact vs. claimed commitment.** *Claimed*: an index covered by
  any node's `commitIndex`. *In fact* (computed by the checker from durable
  disk contents): index *i* is committed once some leader of term *T* has an
  entry *of term T* at index ≥ *i* stored durably (fsynced) on a quorum of the
  configuration in effect — both quorums during joint consensus. Once committed
  in fact, always committed.

From Figure 3:

1. **Election Safety** — at most one leader is *elected* per term, over the
   whole execution. A history property: every (term, leader) pair is recorded
   for the entire run, across crashes. "At most one node is Leader in term T
   right now" misses a leader of T that crashed before a second leader of T
   appeared.
2. **Leader Append-Only** — over the *logical* log (snapshot
   `lastIncludedIndex`/`lastIncludedTerm` plus retained entries), a leader's
   log only grows during its tenure. Stated over the physical log, compaction
   would violate it by design.
3. **Log Matching** — if two logs contain an entry with the same index and
   term, the logs are identical in all entries up through that index. Its
   foundation is checked directly, against the ghost ids rather than node
   against node: **a leader creates at most one entry per (term, index)** —
   every (term, index) maps to exactly one ghost id.
4. **Leader Completeness** — an entry committed *in fact* is present in the
   log of every leader of every higher term.
5. **State Machine Safety** — if a server has applied an entry at a given
   index, no server ever applies a different entry (by ghost id) at that index.
   Recorded globally across restarts; installing a snapshot counts as applying
   every entry it covers.

Implied by the paper, and catching different or earlier bugs:

6. **Committed entries are durable** — every entry committed in fact survives
   *any* crash schedule, including all nodes crashing at once, provided fsynced
   writes are durable. The minority bound is about availability, not safety.
7. **No spurious commit** — claimed ⊆ in fact: every index a node claims
   committed is committed in fact. This embeds Figure 2's
   `log[N].term == currentTerm` rule. "Never past an entry on fewer than a
   majority" is **not** this invariant: in Figure 8(c) the term-2 entry *is*
   on a majority, and committing it by counting copies is precisely the bug.
8. **Vote Uniqueness** — a node grants at most one vote per term over its whole
   life, restarts included. A lost `votedFor` violates Election Safety only
   under a particular later interleaving; this fails at the first double vote.
9. **Term monotonicity** — a node's `currentTerm` never decreases, including
   across a restart. Catches an unpersisted `currentTerm` directly.
10. **Entry uniqueness** — the (term, index) → ghost id property of 3, stated
    separately because it is the one Log Matching is an induction over.
11. **Bounded liveness in a stable suffix** — after the last fault heals, with
    a majority up, a leader exists within K election timeouts and a submitted
    command commits. This is the anti-vacuity property for every invariant
    above: each of them has a degenerate execution (no leader, empty logs, one
    term) that satisfies it. Raft's liveness is probabilistic, so K is
    generous and a failing seed is investigated, not retried.

**Positive control.** The simulator's *fsynced-then-lost* disk fault (§7) is a
disk that lies, which Raft cannot survive: one node losing an fsynced
`votedFor` yields two leaders in a term. Injecting it **must** turn Election
Safety, Vote Uniqueness or Leader Completeness red. It is the simulator's
built-in sabotage, run as an expected-failure test, never as a fault the
implementation is expected to survive.

### For each invariant, before writing it

State the vacuity risk: how could this test pass while proving nothing? Write
the answer down next to the test. Before any Raft exists, prove each checker
against hand-built traces: it rejects a violating trace and accepts a valid
one. (A property test that fails because the implementation does not exist
yet fails for the wrong reason, and proves nothing.) Then, after it passes
against the implementation, break the code on purpose and confirm this test —
not a neighbour — goes red. A test that does
not fail when its subject is broken is not coverage; it is the appearance of
coverage, which is worse.

**Convergence-style traps to expect.** Election Safety is satisfied by an
implementation that never elects anyone. Log Matching is satisfied by empty
logs. Leader Completeness is satisfied if there is only ever one term. Every
one of these invariants has a degenerate execution that satisfies it, so each
test must also assert that the interesting thing happened: a leader was
elected, entries were committed, the term advanced, a partition healed. Assert
the mechanism, with a count, not just the absence of a violation.

## 6. The state machine and the checker

A key-value store: `Get`, `Put`, `Append`, `CompareAndSwap`, `Delete`.
`Append` exists because `Put` is idempotent: a duplicated `Put` is invisible,
so a deduplication test built on it is vacuous. Every operation touches exactly
one key; the checker relies on that (below), so any future multi-key operation
is a spec change.

Linearizability is the property that matters and it is not implied by any of
§5's invariants: a cluster can satisfy all seven and still return a stale read.
So the checker is a separate deliverable.

- Record a **history**: for each client operation, its invocation time, its
  response time, and its result. Concurrent operations overlap.
- An operation with no response (timeout, client crash, killed leader) is
  **indeterminate**: it may or may not have taken effect. The checker treats
  it as possibly taking effect at any point after its invocation, with no
  response constraint. Treating it as failed rejects correct histories;
  dropping it also rejects them, because a later read observes a value "never
  written".
- The checker decides whether some sequential ordering of the operations,
  consistent with the real-time partial order, produces the observed results.
- Use the Wing & Gong / Lowe approach (linear search with memoisation over
  reachable states), not a naive permutation search, which will not terminate
  on a history of interesting length.
- Decompose per key (Herlihy & Wing locality): a history is linearizable iff
  each per-key sub-history is. Valid only because every operation is
  single-key.
- A brute-force permutation oracle for small histories (≈8 operations) is kept
  alongside, as a differential reference for the WGL checker.
- **The checker's limit (measured in phase 5).** The WGL checker memoises every
  state it reaches, so memory is the binding constraint, and usable memory, not
  total: a budget that fits in memory and one that completes while swapping are
  different numbers, and the second is useless under a CI time limit. Cost is
  driven by how indeterminate writes overlap and interleave with the
  observations on one key, not by how many there are: measured over 60,000
  keys, no per-key count separates decidable histories from undecidable ones;
  one undecided key had zero observed indeterminate Appends, and the costliest
  decided key (865,895 states) had none at all. The budget is therefore set to
  what the enforcing runner can hold: 32,000,000 states per key, 12.5 GB
  measured on the GitHub runner (15.1 GB available), where time per state is
  still flat. 40,000,000 states needs 14.9 GB and is already slower; 48,000,000
  swaps and runs three times slower per state; 56,000,000 is killed. A history
  exceeding the budget fails the soak rather than being excluded. No sound
  reduction removes an indeterminate write observed by a completed read.
- **Recorded known limits.** A history the checker cannot decide within the
  budget fails the soak; there is no declaration and no floor. One exception
  exists, and it is measured, not granted: a *recorded known limit* is a single
  soak execution, named by its seed, the key whose search exhausts the budget,
  and a SHA-256 digest of that key's sub-history. It carries its
  states-and-memory curve, the open register row promised to resolve it, and the
  phase report that approved it; `gates register` checks that the row is open
  and that the report exists and names the entry. It applies only to that exact
  sub-history: any change to the workload, the simulator, Raft or the adapter
  changes the digest, and the entry stops matching and fails the soak. While
  recorded, the execution is still checked on every soak run and must still be
  undecided at the budget; an entry that becomes decidable is stale and fails
  the soak until it is removed. It never counts as accepted: the soak reports it
  as unverified, under its own name, and says that its linearizability is
  unknown. No rate, count or floor attaches to the list, and the soak's tests
  name its exact entries, so adding or replacing one is a visible edit to a
  reviewed set. Today it holds one entry: **KL-1**, seed 7723, key k3,
  undecided at 32,000,000 states (12.5 GB on the GitHub runner; still undecided
  at 48,000,000 while swapping), promised to the register's structural-measure
  row.

**The checker's own vacuity risk is the sharpest in the project.** A checker
that accepts everything passes every test you write against a correct
implementation. So `Raft.Checker.Tests` must contain hand-written histories
that are known non-linearizable — a stale read, a lost write, a
committed-then-vanished value — and the checker must reject each one. Write
those before the checker. If the checker cannot reject a history you know is
bad, nothing it accepts means anything.

Hand-written histories share their author's model of linearizability, so
they are backed by two sources that do not: the published examples from
Herlihy & Wing, whose verdicts come from outside the project, and histories
produced in the simulator by a deliberately broken non-Raft store (e.g.
asynchronous primary-backup reading from the backup).

## 7. The simulator

This is the deliverable that distinguishes the project.

- **A single seed determines everything.** Message delay, reordering,
  duplication, drops, partitions, crash timing, restart timing, clock skew,
  disk-write reordering and election timeout jitter all come from one seeded
  source. Rerunning a seed reproduces a failure exactly.
- **Seed → schedule → run.** The seed feeds a generator that produces an
  explicit fault schedule (data); a deterministic runner executes a schedule.
  The shrinker operates on schedules, so a shrunk failure no longer comes from
  its seed — the reproduction artifact is the serialized schedule plus the
  generator-config hash and commit SHA, not the seed alone. Random draws
  inside a run are derived by hashing (seed, purpose, index) rather than from
  one sequential stream, so removing one fault does not shift every later
  draw.
- **Byte-identical means across processes.** Determinism is checked by running
  a schedule in two separate processes and comparing trace bytes.
- **No wall-clock time.** The simulator advances a logical clock. A ten-minute
  execution runs in milliseconds, and nothing is flaky.
- **The seed and schedule are printed on every failure**, and the round trip — take the
  printed seed, reproduce locally — is exercised deliberately at least once. An
  untested recovery path is not a recovery path.
- **Shrinking.** On failure, minimise the fault schedule before reporting.
  A hundred-event trace that fails tells you less than the four events that
  matter. Hand-roll the shrinker; this is a core deliverable, not scaffolding.

### Faults the simulator must inject

Network: delay, reorder, duplicate, drop, asymmetric partition (A can reach B
but not the reverse), and partition healing.

Nodes: crash (lose volatile state, keep disk), restart, pause and resume (which
is not the same as crash — a paused leader still holds its term and comes back
believing it is leader), and slow disk.

Disk: a write that returned but was not fsynced, then lost on crash; writes
reordered before an fsync barrier; a partial (torn) write; rename (for atomic
snapshot replacement). Raft's correctness depends on `currentTerm` and
`votedFor` being durable before a vote is granted or requested; a simulator
that cannot express "the write returned and then did not happen" cannot test
that.

Two cases must be kept apart:

- **(a) Written, not fsynced, lost on crash.** Legitimate. Raft must survive it
  by fsyncing before it acts.
- **(b) Fsynced, then lost.** The disk lied. Raft cannot survive it — a single
  node losing an fsynced vote gives two leaders in a term. This fault exists as
  the positive control in §5, expected to go red, never as a fault the
  implementation must survive.

Faults are proven to fire by their effect in state (a dropped message is not
delivered, observed at the receiver), not by a counter at the injection site
(§9). Each coverage dimension below is defined as a function of the trace.

### The simulator's own coverage must be measured

**The generator's parameter space is the outer bound of what any property test
can find, and nothing inside the suite can see that bound.** So report the
distribution of what was actually generated, not the knobs that were set:
how many executions had a partition at all, how many had two concurrent
candidates, how many crashed a leader mid-replication, how many had a disk
loss after acknowledgement. A dimension that reports zero fails the build.

Read those numbers for relationships, not just for zeroes. Two dimensions with
identical counts are one measurement wearing two names. A rate of 100% usually
means the generator cannot produce the other case, not that the property
always holds. Expect the first measurement of any distribution to be wrong.

## 8. Persistence

`currentTerm`, `votedFor`, and the log entries, durable before any message is
sent, or any decision is made, that depends on them. Figure 2's "before
responding to RPCs", taken literally, is not enough: a candidate's vote for
itself is *sent*, not a response. (A becomes candidate in term 5, gets B's
vote, crashes before persisting; restarts in term 4; votes for C in term 5 —
two leaders.) The same holds for a leader counting its own entry toward a
majority. Snapshots for compaction.

Format is your choice, but: a length-prefixed append-only file with a checksum
per record, and recovery that truncates a torn **final** record rather than
refusing to start. A checksum failure in any earlier record is corruption,
not a torn write, and recovery refuses to start. A crash during a write is the
normal case, not an edge case. A snapshot is replaced atomically: write a
temporary file, fsync it, rename it over the old one, fsync the directory.

**Known limit: the rename barrier is a simplification, not the contract** (phase 7, decision 1).
The node cannot fsync: it never learns what is durable. The world holds a rename until every write
the node issued before it is durable, as it holds a message (node-interface §4), and the simulated
disk models a rename whose source data was lost as leaving an empty file under the real name. A
real file system can make a rename durable without the data; the barrier assumes the I/O layer
prevents that, which the node cannot check. It is not solved: disk acknowledgements as inputs to
Core, the register's phase-10 row, are what would let the node order its own writes.

**Anything that removes data participates in every invariant the data
participated in.** Log compaction discards entries the snapshot covers. Before
writing it, enumerate every property the log satisfies — referential (an index
resolves), sequential (indices are dense), reconstructive (replay produces the
state), evidential (the log is what `matchIndex` is checked against) — and say
which the compaction preserves and how. Satisfying the one that motivated the
removal says nothing about the others, and the others are the ones nobody
lists.

## 9. Observability

Not deferred. The simulator's failures are unreadable without it.

- A structured event per state transition, RPC sent, RPC received, commit index
  advance, and apply. The trace of a failing seed should be readable.
- **Every counter must be derivable from state, or it lies.** A counter placed
  next to an effect measures that control reached the line after it, not that
  the effect happened. Prefer a reading computed from the log and the state
  over an incrementing counter wherever one is possible, and say explicitly
  which counters have no state to derive from.
- A visualiser for a failing execution would be worth a day, late: a timeline
  of nodes, terms, and messages. Optional, and only after §11's phase 8.

## 10. What will go wrong, stated in advance

Carried from the previous project, because these recurred in every phase and
naming them in advance is cheaper than rediscovering them:

- **A guard written as a pattern match covers the instances of the pattern, not
  the property.** Ask of each check: what defeats this without matching it?
- **A test that invokes a mechanism directly proves the mechanism works, not
  that anything invokes it.** For every scheduled or triggered behaviour, one
  test where nobody calls anything.
- **A check attached to an action only covers principals that take that
  action.** For any mechanism keyed on an action, ask: who is the legitimate
  participant that never performs it? (In Raft: a follower that never receives
  a client request, a node that is partitioned and therefore sends nothing.)
- **A test comparing two parties proves nothing if one party's answer came from
  the other.** Especially acute here: nodes converge by design, so "the logs
  match" is satisfied by any deterministic rule, including a wrong one.
- **A green run on an intermittent fault proves the fault was absent**, not
  that the handling works. Assert the handling executed.
- **No test written for a limit notices that the limit is in the wrong place.**
  For every configured value — election timeout, heartbeat interval, snapshot
  threshold, max batch — one test that performs the largest legitimate thing
  and asserts it succeeds.
- **A rule that names a thing to be careful about will not hold. A rule that
  names a mechanical act tied to an unavoidable moment will.** Every process
  requirement in §11 is a script in CI or it is decoration.
- **A failure is evidence about the invocation before it is evidence about the
  code.** Check the environment first.
- **Duration is an assertion about whether the work happened.** A suite that
  gets dramatically faster without a change that explains it is doing less.
  Record each CI job's duration in the phase report.

## 11. Phases

Do not start a phase before the previous one is committed, green in CI on its
own commit, and reviewed. At the end of each, stop and report.

| Phase | Deliverable | Done when |
|---|---|---|
| 0 | Repo, CI, gates, `AGENTS.md`, layering test, known-bad histories | CI green on a clean clone; every test project that exists runs at least one real test (test-count floor); the layering and ambient-dependency tests proven non-vacuous; a secret scan; the known-bad and Herlihy–Wing histories classified by a brute-force oracle |
| 1 | The simulator, before any Raft | A seeded execution of three trivial-protocol nodes (echo plus a persisted counter — not do-nothing nodes, which give network faults nothing to act on) reproduces byte-identically across separate processes; every fault in §7 is injectable and has a test proving it fires, by its effect; the node↔world interface contract is written and traced against every input and output Figure 2 needs; the generated distribution is measured from what happened and no dimension is zero; a planted failure — including one whose cause is the order of two faults — shrinks to its minimal schedule |
| 2 | The checker's rejecting half | The WGL checker rejects every known-bad history, agrees with the brute-force oracle on random small histories, and rejects histories produced by a deliberately broken non-Raft store in the simulator |
| 3 | Leader election, with `currentTerm`/`votedFor` persistence and the §6 disruption rule | Invariants 1, 8, 9 and 11 hold across 10,000 seeded executions including partitions (asymmetric included) and crashes; the fsynced-then-lost positive control goes red; distribution reported |
| 4 | Log replication + log persistence | Invariants 2–7 and 10 hold; the election restriction (§5.4.1) re-tested now that logs are non-empty; a crash-during-write test passes; committed entries survive any crash schedule, including all nodes |
| 5 | The checker's accepting half | Accepts the histories a correct cluster produces, including indeterminate operations |
| 6 | Membership changes (joint consensus) | Joint consensus under partition preserves every invariant |
| 7 | Log compaction | A compacted node and an uncompacted one agree; every log property (§8) accounted for |
| 8 | Client sessions + read-only queries | A retried `Append` applies once; the checker accepts every history that includes reads |
| 9 | Real sockets, multi-process | Three processes in Compose, a real client, a killed leader, and the checker green on the resulting history |
| 10 | Measurement | Throughput and latency against a written target, with the build configuration recorded beside every number |
| 11 | Linearizability-checking cost | A structural quantity of a key's sub-history separates the histories the WGL checker decides within its budget from those it cannot, with a threshold that holds on every decided and every undecided key of the soak, or the spec states the limit without one; every recorded known limit explained by it or re-recorded |

Phase 1 before any Raft is deliberate and is the main structural difference
from how most people build this. Writing the simulator against nodes that are
not Raft forces it to be a real deliverable rather than something shaped
around the implementation it is meant to test. The risk it carries is the
node↔world interface, not the ordering — hence the Figure 2 trace in phase 1.
The checker's rejecting half follows immediately because it depends on
nothing and has the sharpest vacuity risk; there is no reason for it to wait
for Raft.

(The table was renumbered by the phase-0 amendment: the checker split in two,
membership and compaction split in two. Phase numbers elsewhere refer to it.
Phase 11 was added at phase 6's approval: a research question with a first-time
candidate measure, kept out of the membership phase so that joint consensus has
that phase's whole attention.)

## 12. Working agreement

- **Plan before code.** At the start of each phase, produce a task breakdown
  with, for every task: the vacuity risk, the sabotage that will confirm it,
  and whether it can be verified in this environment. A breakdown missing any
  of these is malformed — enforce it with a script in CI, not by intent.
- **Predict before implementing.** Before each task, state a mechanism and an
  observable that you expect to fail. "I expect at least one failure" is not a
  prediction. "The resumption branch returns before the cap is checked" is.
  Record whether the prediction was right.
- **Tests first for `Raft.Core`.** §5's invariant checkers are written before
  the implementation exists and proven against hand-built traces — each
  rejects a violating trace and accepts a valid one. "Failing because nothing
  exists" is not evidence.
- **Sabotage is a standing practice.** Any check that exists to catch
  divergence gets deliberately broken to confirm it fires. Do this from a
  committed tree. When a sabotage survives, the first hypothesis is that the
  test does not reach the code, not that the code is unreachable.
  The harness re-runs the test suite, and from phase 3 the suite checks the
  invariants on a fixed sample of executions. A harness verdict is therefore a
  claim about the sample, not about the invariant: a sabotage that breaks only
  at execution 4,000 is caught by the soak (10,000 executions, a required CI
  job) and not by the harness. The soak runs on every push and is never waived
  or shrunk for time. It is slow and peripheral, which makes it the first thing
  dropped when a phase runs long; skipping it is a change to this spec, argued
  first, not a judgement made in the moment.
- **Small commits**, conventional messages, every commit green. GitHub Actions
  runs only a push's head, so CI builds and fast-tests every commit in the
  pushed range. Every commit that changes more than documentation carries a
  `Task:` trailer naming a task in the current phase's breakdown.
- **Ask before adding a dependency.** The answer for anything consensus-shaped
  is no.
- **No stubs presented as done.** Unimplemented throws and is listed.
- **Report honestly.** If an invariant fails and you cannot fix it, show the
  minimised failing seed. Never weaken a test to make it green — argue for
  changing this spec first.
- **A phase report certifies the commit that contains it.** Re-verify CI
  against that commit, not the one before it.
- **Keep a register** of every deferred item with the phase it is promised to,
  and a script that fails when a row's status and the phase table disagree. A
  row that stops tracking reality is worse than no row.
- **Keep a findings log.** The bugs are less valuable than the reasons the
  tests did not catch them.
- **Walk it cold, once per phase.** Clone it fresh, follow the README in its
  own order, nothing pre-arranged, and record the step where it stops. CI never
  does what a person on a fresh clone does, and the path every person takes is
  the path with no coverage. The README's commands are also run by CI in a
  fresh container with an empty package cache; that catches rot, not the
  things a person does. The person's walk cannot be a script; the phase
  report's record of it is required by the report gate.

**Enforcement.** Every rule above that can be a script is one, in
`tools/Raft.Gates`, run by CI: the breakdown gate and prediction-order check,
the register gate, the sabotage harness, the test-count floor, the
CI-run verification for reports, the every-commit check, the preflight, and the
secret scan. `AGENTS.md` maps each rule to its enforcing script; a rule with no
script is listed there as such.
