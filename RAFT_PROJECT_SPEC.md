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

## 3. Stack

- .NET 10, C# 14, nullable enabled, warnings as errors
- xUnit, and a hand-rolled generator and shrinker (see §7)
- Serilog for structured logs; no network I/O in the core
- Docker Compose for the multi-process phase only

Pin the SDK image to an exact patch version, not a floating tag, and set
`rollForward: latestPatch` in `global.json`. A floating `sdk:10.0` tag and a
`rollForward: latestFeature` are a pinned-looking configuration that pins
nothing — a different Roslyn feature band will reject code the gate accepted.

## 4. Architecture

```
src/
  Raft.Core/          the algorithm. No I/O, no clock, no threads, no ASP.NET
  Raft.Simulation/    deterministic network, seeded, reproducible
  Raft.Checker/       linearizability checker
  Raft.Kv/            the state machine, and a TCP transport
tests/
  Raft.Core.Tests/         Figure 3's properties as executable invariants
  Raft.Simulation.Tests/   the simulator's own faults, and its own guards
  Raft.Checker.Tests/      histories the checker must accept and must reject
  Raft.Kv.Tests/           integration, real sockets
```

Dependency direction is a partial order: `Kv → {Core, Simulation, Checker}`,
`Simulation → Core`, `Checker → nothing`. `Raft.Core` references the BCL only.
Enforce it with a test that reads the project graph, not by relying on the
compiler — and prove that test non-vacuous by adding a violating reference and
watching it go red.

**`Raft.Core` takes no ambient dependencies.** No `DateTime.Now`, no
`Task.Delay`, no `Random`, no threads. Time arrives as a tick the caller
advances; messages arrive as a call; randomness arrives as an injected source.
This is what makes §7 possible, and it is the single most important structural
decision in the project. A node is a pure function from (state, input) to
(state, outputs).

## 5. The algorithm

Implement in this order, because each is separately testable:

1. **Leader election** — terms, `RequestVote`, randomized election timeouts,
   the three states.
2. **Log replication** — `AppendEntries`, `nextIndex`/`matchIndex`, commit index
   advancement by majority.
3. **Persistence** — `currentTerm`, `votedFor` and the log survive a crash,
   written before any RPC responds.
4. **Membership changes** — joint consensus (§6 of the paper), not the
   single-server-at-a-time simplification.
5. **Log compaction** — snapshots, `InstallSnapshot`.
6. **Client interaction** — §8 of the paper: session-based deduplication so a
   retried command is not applied twice, and read-only queries that do not
   return stale data.

Items 4 to 6 are where most implementations stop, and they are where the
interesting bugs are. Do not defer them into a phase that never happens.

### Invariants (Figure 3)

These are the acceptance criteria for `Raft.Core`. Write them as executable
property tests over simulated executions before the implementation exists.

1. **Election Safety** — at most one leader per term.
2. **Leader Append-Only** — a leader never overwrites or deletes entries in its
   own log; it only appends.
3. **Log Matching** — if two logs contain an entry with the same index and term,
   the logs are identical in all entries up through that index.
4. **Leader Completeness** — if an entry is committed in a term, it is present
   in the log of every leader of every higher term.
5. **State Machine Safety** — if a server has applied an entry at a given index,
   no other server ever applies a different entry at that index.

Plus two the paper implies and which catch different bugs:

6. **Committed entries are durable** — an entry acknowledged as committed to a
   client is present after any combination of crashes of a minority.
7. **No spurious commit** — the commit index never advances past an entry
   replicated on fewer than a majority.

### For each invariant, before writing it

State the vacuity risk: how could this test pass while proving nothing? Write
the answer down next to the test. Then, after it passes, break the code on
purpose and confirm this test — not a neighbour — goes red. A test that does
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

A key-value store: `Get`, `Put`, `CompareAndSwap`, `Delete`.

Linearizability is the property that matters and it is not implied by any of
§5's invariants: a cluster can satisfy all seven and still return a stale read.
So the checker is a separate deliverable.

- Record a **history**: for each client operation, its invocation time, its
  response time, and its result. Concurrent operations overlap.
- The checker decides whether some sequential ordering of the operations,
  consistent with the real-time partial order, produces the observed results.
- Use the Wing & Gong / Lowe approach (linear search with memoisation over
  reachable states), not a naive permutation search, which will not terminate
  on a history of interesting length.

**The checker's own vacuity risk is the sharpest in the project.** A checker
that accepts everything passes every test you write against a correct
implementation. So `Raft.Checker.Tests` must contain hand-written histories
that are known non-linearizable — a stale read, a lost write, a
committed-then-vanished value — and the checker must reject each one. Write
those before the checker. If the checker cannot reject a history you know is
bad, nothing it accepts means anything.

## 7. The simulator

This is the deliverable that distinguishes the project.

- **A single seed determines everything.** Message delay, reordering,
  duplication, drops, partitions, crash timing, restart timing, clock skew,
  disk-write reordering and election timeout jitter all come from one seeded
  source. Rerunning a seed reproduces a failure exactly.
- **No wall-clock time.** The simulator advances a logical clock. A ten-minute
  execution runs in milliseconds, and nothing is flaky.
- **The seed is printed on every failure**, and the round trip — take the
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

Disk: a write that is acknowledged and then lost on crash, and a partial write.
Raft's correctness depends on `currentTerm` and `votedFor` being durable before
a vote is granted; a simulator that cannot express "the write returned and then
did not happen" cannot test that.

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

`currentTerm`, `votedFor`, and the log entries, written durably before any RPC
that depends on them responds. Snapshots for compaction.

Format is your choice, but: a length-prefixed append-only file with a checksum
per record, and recovery that truncates a torn final record rather than
refusing to start. A crash during a write is the normal case, not an edge case.

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
  of nodes, terms, and messages. Optional, and only after §11's phase 6.

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
| 0 | Repo, CI, gates, `AGENTS.md`, layering test | CI green on a clean clone with a real assertion in each test project, the layering test proven non-vacuous, and a secret scan |
| 1 | The simulator, before any Raft | A seeded execution of three do-nothing nodes reproduces byte-identically across runs; every fault in §7 is injectable and has a test proving it fires |
| 2 | Leader election | Figure 3 properties 1 and 5 hold across 10,000 seeded executions including partitions; distribution reported |
| 3 | Log replication + persistence | Properties 2, 3, 4, 6, 7 hold; a crash-during-write test passes; committed entries survive a minority crash |
| 4 | The checker | Rejects every hand-written non-linearizable history; accepts the histories a correct cluster produces |
| 5 | Membership changes + compaction | Joint consensus under partition preserves all seven properties; a compacted node and an uncompacted one agree |
| 6 | Client sessions + read-only queries | A retried command applies once; a read-only query never returns a value a later read contradicts |
| 7 | Real sockets, multi-process | Three processes in Compose, a real client, a killed leader, and the checker green on the resulting history |
| 8 | Measurement | Throughput and latency against a written target, with the build configuration recorded beside every number |

Phase 1 before phase 2 is deliberate and is the main structural difference from
how most people build this. Writing the simulator against nodes that do nothing
forces it to be a real deliverable rather than something shaped around the
implementation it is meant to test.

## 12. Working agreement

- **Plan before code.** At the start of each phase, produce a task breakdown
  with, for every task: the vacuity risk, the sabotage that will confirm it,
  and whether it can be verified in this environment. A breakdown missing any
  of these is malformed — enforce it with a script in CI, not by intent.
- **Predict before implementing.** Before each task, state a mechanism and an
  observable that you expect to fail. "I expect at least one failure" is not a
  prediction. "The resumption branch returns before the cap is checked" is.
  Record whether the prediction was right.
- **Tests first for `Raft.Core`.** §5's invariants are written and failing
  before the implementation exists.
- **Sabotage is a standing practice.** Any check that exists to catch
  divergence gets deliberately broken to confirm it fires. Do this from a
  committed tree. When a sabotage survives, the first hypothesis is that the
  test does not reach the code, not that the code is unreachable.
- **Small commits**, conventional messages, every commit green.
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
  the path with no coverage.
