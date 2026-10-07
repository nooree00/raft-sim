# Phase 11 — task breakdown

Replication's cost, and linearizability-checking cost (spec §11 phase 11, amended by P11-00). Format
as in `docs/phases/P10/breakdown.md`, parsed by `gates breakdown`; outcomes will say `(evidence)` or
`(forcing)`.

**Status: proposed, for review.** Nothing below is implemented.

**Done when** (spec §11, as P11-00 proposes to amend it):
- **the resend defect fixed and the curve re-measured:** the leader sends each entry to each follower
  a bounded number of times whatever the backlog, every invariant and both soaks still hold, and the
  offered-load curve of phase 10 is measured again on the same machine against the design's own model
  (C_design, L);
- **the group commit decided from that curve,** by a rule written here before the measurement;
- **the original deliverable:** a structural quantity of a key's sub-history separates the histories
  the WGL checker decides within its budget from those it cannot, or the spec states the limit
  without one.

Carried forward:
- **phase 10's acceptance:** both register rows in, **the resend fix first**. Group commit before it
  optimises the wrong path; whether it is worth doing is decided from the new curve, not assumed;
- **phase 10's diagnosis, as a lesson for the checks:** the defect broke no invariant. Every replica
  agreed, every history was linearizable, and the cluster completed almost nothing. Eight phases of
  correctness work could not see it, because nothing measured the work done per committed entry.
  The simulator could not have shown it either: the soak's clients are closed loop, three per
  execution with one operation each in flight, so at most three entries are ever unacknowledged and
  a resend costs at most three times a send. P11-01 adds the check that would have seen it;
- **the 200,000 figure was ungrounded** (phase 10 acceptance, findings): every number this phase
  compares against is a property of the design as built, or is labelled as another design's;
- **phase 8's finding on the structural row:** the undecided keys it was built on were a
  representation artifact, and neither soak has one now. The question stands; its evidence is gone,
  so P11-07 needs histories that are hard on their own terms;
- **S-bench-2 fired once over its bound in phase 10 and was left alone.** If it fires again it is a
  finding to read, not a bound to widen;
- spec §10: a rule tied to a mechanical act holds; every configured limit at its largest legitimate
  size. The fix adds a limit (entries in flight per follower), so P11-02 tests it at its largest.

## Ordering

The spec amendment first, so the phase's deliverable is the one being built (P11-00). Then the check
that would have seen the defect, written and shown red on today's code before any fix (P11-01). Then
the fix, tests first (P11-02), and its correctness at scale (P11-03). Then the target restated from
the design's own model, before any new number (P11-04), and the re-measurement (P11-05). Then the
group-commit decision from that curve (P11-06). Then the original deliverable, which depends on
nothing above (P11-07). Then the cost in CI (P11-08).

**Blocking set:** P11-00 to P11-07 (the done criterion as amended). P11-08 blocks nothing.

## Decisions for review

1. **Phase 11's deliverable is amended to put replication's cost first** (P11-00). The spec's phase
   11 is today only the structural measure. Phase 10's acceptance puts both register rows in, the
   resends first. The amendment adds them to the row, keeps the structural measure, and records in §2
   that the exclusion of performance work is lifted for these two rows only, because phase 10
   produced the baseline §2 asked for.
2. **A cost check in the simulator, a counter derived from the effects** (P11-01). Spec §9: every
   counter must be derivable or it lies. The leader's entries sent are read from its `Send` effects
   (each `AppendEntries` decoded, its entry count summed), committed entries from the commit index:
   no counter in `RaftNode`. The check: under a workload that keeps many writes outstanding (an
   open-loop simulated client, 32 operations in flight, no faults), entries sent to each follower per
   committed entry stay at most 2. That is one send each, plus room for heartbeat retransmission of
   an append still in flight. It runs in the scale project, as a test with its own sabotage, not in
   the soak: the soak's job is invariants under faults, and a cost bound under faults would measure
   the faults.
3. **The fix: one append in flight per follower, carrying everything up to `MaxEntriesPerAppend`**
   (P11-02). Two designs were considered:
   - **(a), recommended: stop and wait, per follower.** The leader records, per follower, that an
     `AppendEntries` with entries is outstanding and the last index it carried. A client write sends
     nothing to a follower with one outstanding. The response (success or rejection) clears it and
     sends the next batch, everything from the new next index up to 64 entries. A heartbeat to a
     follower with one outstanding re-sends it, which recovers a lost message within one heartbeat
     interval. Entries per follower per committed entry then fall towards 1 / batch under load. The
     cost is one round trip of pipelining: a follower receives at most one batch per round trip.
     With the loopback round trip at 47 µs against a 160-µs sync, the follower's sync, not the round
     trip, is still the slower step.
   - **(b), optimistic pipelining** (etcd's replicate state): advance the next index on every send,
     fall back to probing on a rejection. It pipelines more, and needs more states and more ways to
     be wrong (a lost message is found only by a later rejection).

   (a) fixes the defect with the least new state; (b) is a later optimisation if the re-measured
   curve shows the round trip, not the sync, capping the cluster. Recommended: (a).
4. **The target restated from the design's own model, before any new number** (P11-04). Phase 10's
   latency criterion stands (a median within 1.5 × L and a 99th percentile within 3 × the inputs'
   tail, at C_design / 2). The throughput criterion becomes the design's own: sustained at least
   C_design / 2 (3,125 writes a second), with C_disk kept only as what a group commit would reach,
   labelled so. The inputs (S, R) are measured again on the day, five repetitions each, because
   P11-05 compares within one machine and one session.
5. **The group-commit rule, written now** (P11-06). From P11-05's curve, at the highest sustained
   rate: if the leader's sync is busy at least 80% of the time (syncs a second × the median sync time
   ≥ 0.8), the sync caps the cluster and a group commit is built in this phase, as tasks added to
   this breakdown by amendment and approved first. If under 80%, the row is closed with the
   measurement by a spec change: the cap is elsewhere, and the measurement names where.
6. **The structural measure on histories hard on their own terms** (P11-07). No undecided key exists
   in either soak since phase 8. Proposed: a generator of single-key histories whose difficulty is
   controlled directly, with *w* writes left indeterminate and overlapping one another and *r* reads
   observing them (the shapes the register row names). Each is run through the WGL checker at the
   soak's budget, with the register row's prediction, recorded at phase 6, tested on them. If the
   width separates decided from undecided with the predicted margin, the spec states the threshold;
   if not, the spec states the limit without one (the deliverable allows either). The histories must
   be ones a correct store could produce (each is checked linearizable by the brute-force oracle where
   small enough), or the measure explains a checker on impossible inputs.

## Tasks

### P11-00 — The spec amended: phase 11's deliverable

- **Task:** Decision 1. Spec §11's phase-11 row gains the resend fix, the re-measured curve, and the group-commit decision by P11-06's rule, ahead of the structural measure it keeps; §2 records the exclusion of performance work lifted for those two register rows, because phase 10 measured the baseline. In the commit with the amendment, the register's two rows cite it.
- **Vacuity:** An amendment the breakdown gate does not read changes nothing that is checked. Guarded by the done criterion being checked at the phase report against the amended row, by the reviewer.
- **Sabotage:** ; manual: the reviewer reads the amended row against this breakdown's done criterion
- **Verifiable here:** partial — the text here, its approval the reviewer's
- **Prediction:** The amendment needs a second change beyond §11 and §2: spec §8's known limit on the barrier, or §4's description of the leader's sends, describes "a send per write" in words the fix makes false. **Observable:** a sentence in §4 or §8 that the fix contradicts, found when the amendment is written.
- **Outcome:** pending

### P11-01 — The check that would have seen the defect, red on today's code

- **Task:** Decision 2. A simulated open-loop client that keeps a fixed number of operations in flight. A reading of the leader's entries sent per follower, from its `Send` effects, against committed entries, from the commit index. A test in `Raft.Scale.Tests`: with 32 in flight and no faults, entries sent to each follower per committed entry stay at most 2. It is written and shown failing on today's code before P11-02 starts, and the failing number is recorded.
- **Vacuity:** A workload that never builds a backlog passes any implementation. Guarded by the test also asserting the backlog it measures (at least 16 entries appended and not committed at some instant), and by S-cost-1.
- **Sabotage:** S-cost-1, S-cost-2
- **Verifiable here:** yes — in the simulator, deterministic
- **Prediction:** On today's code, with 32 in flight, the leader sends each follower at least 8 entries per committed entry, and the ratio grows with the number in flight (at least 3 times higher at 32 than at 8). Each write and each response resends the whole unacknowledged window, so the work per entry grows with the window. With the soak's three closed-loop clients it stays under 3, which is why the soak could not see it. **Observable:** the ratio at 3, 8 and 32 in flight on the commit before P11-02.
- **Outcome:** pending

### P11-02 — The fix: one append in flight per follower

- **Task:** Decision 3 (a). Tests first in `Raft.Core.Tests`, against hand-built traces, before the implementation: two client writes before any response send the second entry nothing until the response, then one append carrying it; a rejection backs off and sends at once, as today; a lost append is sent again by the next heartbeat; a snapshot to a follower behind the log is unaffected; a leader that loses office drops what it had in flight. Then the change in `RaftNode`. The limit (entries in flight) is tested at its largest, `MaxEntriesPerAppend`, with exactly that many and one more appended.
- **Vacuity:** A flag that is set and never cleared stops replication, and a flag never set changes nothing. The first is caught by the liveness checks and both soaks, the second by P11-01's bound. Guarded by S-repl-10 (the flag never cleared on a response) and S-repl-11 (never set).
- **Sabotage:** S-repl-10, S-repl-11, S-repl-12; shared: S-cost-1
- **Verifiable here:** yes — in Core's tests and the simulator
- **Prediction:** The first existing test to fail is a read test, not a replication test. A read's round (P8-05) is confirmed by append responses carrying that round or a later one. With an append in flight, a write no longer sends a new append carrying the new round, so a read waits for the outstanding response, which carries the older round, and then for the next send. A test that expects a read answered within one round trip of its arrival fails. **Observable:** the first failing test in `Raft.Core.Tests` and the scale suite after the change, before any test is changed.
- **Outcome:** pending

### P11-03 — Correctness at scale after the fix

- **Task:** Both soaks (10,000 executions each), the harness, and the membership and compaction suites, on the fixed node; the soak's distribution compared with phase 10's (same seeds), dimension by dimension, and every dimension that moves by more than 5 points explained.
- **Vacuity:** A fix that changes what the generator reaches can pass the soak by reaching less. Guarded by the distribution comparison, and by the soak's own floors.
- **Sabotage:** ; shared: S-repl-10, S-repl-11; manual: the distribution compared seed for seed with phase 10's
- **Verifiable here:** partial — the soaks run in CI (10,000 each); locally a 1,000-execution sample before the push
- **Prediction:** Both soaks stay green, and the distribution's "append rejected" dimension falls by more than half. Today most rejections follow a resend arriving after a newer append has already moved the follower on. With one append in flight, a rejection needs a real log mismatch (a new leader, a crash). **Observable:** the rejection dimension's share in the baseline soak, phase 10's head against the fix, on the same seeds.
- **Outcome:** pending

### P11-04 — The target restated from the design's own model

- **Task:** Decision 4. `docs/design/performance-target.md` rewritten before any new end-to-end number: S and R measured again (five repetitions each, records), the latency criterion as in phase 10, the throughput criterion at C_design / 2, C_disk labelled as a group commit's capability. Committed before P11-05's first record.
- **Vacuity:** A target written after the curve fits the curve. Guarded by the commit order (the target's commit precedes every P11-05 record's commit field), which the report checks by citing both.
- **Sabotage:** ; manual: the reviewer compares the target's commit with the first P11-05 record's
- **Verifiable here:** partial — the inputs measured here; the commit order is checked by the reviewer
- **Prediction:** S and R move by under 10% from phase 10's (160 µs and 47 µs medians), so the targets' numbers change by under 10%: the same disk and kernel. **Observable:** the new records' medians against phase 10's.
- **Outcome:** pending

### P11-05 — The curve re-measured against the model

- **Task:** Phase 10's P10-04 measurement repeated on the fixed node: the open-loop generator in process and in Compose, five repetitions per rate, at phase 10's rates and on up to C_design and 1.5 × C_design (625 to 9,375 writes a second), with the leader's syncs, messages and bytes per committed write, and the barrier's share, in every record. The phase-10 head and the fix interleaved in one session at three of the rates, so the difference is measured within one machine.
- **Vacuity:** A re-measurement with a different generator or machine compares two setups. Guarded by the same tool, the interleaved A/B at three rates, and every record's configuration.
- **Sabotage:** ; shared: S-bench-1, S-bench-2; manual: the A/B pairs read against each other, record by record
- **Verifiable here:** yes — this container, in process and in Compose
- **Prediction:** The collapse goes and the sync becomes the cap. In process the highest sustained rate rises to between 2,000 and 5,000 writes a second (from 625 to 1,000). Past it, completed writes stay within 20% of that rate instead of falling to nothing, because each write's cost no longer grows with the backlog. Messages per committed write stay under 4 at every rate up to the knee. The median at 3,125 a second becomes finite and under 10 ms. **Observable:** the curve's completed rate and messages per write, rate by rate, against phase 10's records.
- **Outcome:** pending

### P11-06 — The group commit, decided by the rule

- **Task:** Decision 5. From P11-05's records at the highest sustained rate: the leader's sync busy fraction (syncs a second × median sync time). At 0.8 or more, tasks for a group commit are added to this breakdown by an amendment approved first; under 0.8, the row is closed by a spec change that names the measured cap.
- **Vacuity:** A busy fraction computed from the model's S rather than the measured sync time measures the model. Guarded by computing it from the host's own sync counter and the run's own sync timings, both in the record.
- **Sabotage:** ; manual: the reviewer recomputes the fraction from the cited records
- **Verifiable here:** partial — computed here from the records; recomputed by the reviewer
- **Prediction:** Under 0.8: the leader's sync is busy about half the time at the new knee, and the cap is elsewhere, in the host's single loop thread: it does the sync, the codec and the event log one input at a time. So the row closes, and the measurement points at the loop. **Observable:** the busy fraction at the highest sustained rate in process.
- **Outcome:** pending

### P11-07 — The structural measure on histories hard on their own terms

- **Task:** Decision 6. A generator of single-key histories with *w* overlapping indeterminate writes and *r* observing reads, each history one a correct store could produce (the brute-force oracle confirms the small ones). The WGL checker at the soak's budget on each, with the states it used. The three candidates the register row fixed at phase 6 (the concurrency width, the overlapping indeterminate writes, the distinct values a read could see) computed for each history, and the row's prediction tested: every undecided history's width at least 4 above the largest width decided in under 1,000,000 states. The spec states the threshold, or the limit without one.
- **Vacuity:** A generator whose histories are all decided, or all undecided, separates nothing and confirms any threshold. Guarded by requiring both classes, at least 20 histories each, before any threshold is read, and by S-struct-1 (the generator's indeterminate writes all made to complete).
- **Sabotage:** S-struct-1, S-struct-2
- **Verifiable here:** yes — the checker and the generator run here
- **Prediction:** The width does not separate with a margin of 4. Histories of equal width split between decided and undecided by how the reads constrain the indeterminate writes, so the overlap of indeterminate writes with observing reads is the better separator, and the phase-6 prediction is wrong in its margin. **Observable:** the largest decided width against the smallest undecided width, over the generated set.
- **Outcome:** pending

### P11-08 — The slow host tests in their own project, then the phase's cost in CI

- **Task:** First, before the rest of the phase (reviewer, at phase 10's acceptance push): `BenchControlTests` and `FsyncFailureTests` move from `Raft.Host.Tests` into a project of their own, `Raft.Host.Slow.Tests`. Every test entry runs its target's whole project, so each of the ten host entries that targets neither was paying for the bench controls and the fsync tests (phase 10 lengthened the project to about 127 s locally). With S-hostdisk-1 at its true cost (330 s; its recorded 64.2 s was found stale by the per-entry outlier catch), the largest-manifest plan modelled 603 s against its 600-s bound. The move removes that cost at its source, as P4-11 and P5-07 did, rather than redistributing it with a smaller shard size, which P10-00 measured as useless. The five entries that target the moved tests (S-bench-1 to S-bench-3, S-fsync-1, S-fsync-2) move with them; the costs, the layering table, the test baselines and the register's evidence names follow. Then, at the phase's end: the harness shards, the soaks and the per-commit matrix on the phase's first-attempt runs against phase 10's, with the per-commit matrix's job count per push and whether every job was given a runner (phase 10's report push had 34 that never ran).
- **Vacuity:** A move that drops a test passes with less. Guarded by the test baseline (the two projects' counts summing to the old one, 33) and by every moved entry still caught. For the cost part, a run with a job that never ran compares less work: only jobs with steps are counted, and any never-run job is stated.
- **Sabotage:** ; shared: S-bench-1, S-bench-2, S-bench-3, S-fsync-1, S-fsync-2, S-hostdisk-1; manual: the comparison checked against each run's job list
- **Verifiable here:** partial — the move and the local costs here; CI's numbers only in CI
- **Prediction:** The move takes the largest-manifest plan under its bound with a margin, not just under it: the slowest modelled shard at 336 entries falls below 570 s (5% under the 600-s bound, phase 7's margin), and S-hostdisk-1's local time falls below half of its 272.7 s, because most of what it paid for was the moved tests running with the persist barrier sabotaged. The `Raft.Host.Tests` run alone falls below 60 s locally. **Observable:** the largest-manifest test's modelled slowest shard, S-hostdisk-1's `--only` time, and the host project's test time, after the move.
- **Outcome:** pending

### P11-09 — A test entry runs its target's class, not its target's whole project

- **Task:** The reviewer's decision after P11-08's first measurement. The largest-manifest test prices each of its placeholder entries at the costliest recorded entry, and 24 of the 26 `Raft.Scale.Tests` entries cost 139 to 335 s for one structural reason: every test entry runs its target's whole project, and the Scale suite takes about 367 s locally. A patched entry now runs only its target's class (`--filter-class`), in `RunOne` and in a control's run. The arguments are built by one function (`EntryTestArgs`), and a guard (`BeyondClass`) fails an entry whose results include a test outside its target's class. Every test entry's cost is then measured again, the largest-manifest test re-run, and the worst recorded cost reported before and after, since it is the placeholder price the test turns on. **What it costs:** "neighbours also red" now shows siblings in the target's class only; cross-class collateral is no longer seen. It was informative twice: a sabotage failing three tests instead of one showed a guard's real scope, and it made S-lin-4's degradation visible (phase 8).
- **Vacuity:** The feature creates a new way to under-run: an entry whose filter matches nothing runs no test, and an entry run whole again costs the old price unseen. The first is the existing target-not-run outcome. The second is guarded by `BeyondClass` and by S-harness-5 (the filter dropped from the arguments) and S-harness-6 (the guard disabled).
- **Sabotage:** S-harness-5, S-harness-6
- **Verifiable here:** yes — the harness and its tests run locally
- **Prediction:** The Scale entries' costs fall together, which is the evidence that the class was the cause: at least 20 of the 24 drop to under a third of their recorded cost. The exceptions are entries whose target's class itself runs the soak's sample (`SoakTests`, the linearizability and known-limit classes over the soak's histories), which stay above 100 s, because there the class is most of the suite. The worst recorded cost after the change is one of those, not S-lin-7, and the largest-manifest model falls below 570 s. **Observable:** every test entry's `--only` time before and after, grouped by target class; the worst recorded cost; the model's slowest shard at 336 entries.
- **Outcome:** pending

## Sabotage ids

New series: S-cost (P11-01), S-struct (P11-07); S-repl-10..12 follow S-repl-9, S-harness-5..6 follow S-harness-4. Each id's
`sabotage/<id>/` entry lands in the same commit as the check it proves and is run on that commit
before it is pushed. The touched-file stage runs before every push; the shards run in CI.

- **S-cost-1:** the check's bound read from the follower's sends instead of the leader's, which counts nothing.
- **S-cost-2:** the backlog assertion removed, so a workload with one operation in flight passes.
- **S-repl-10:** the in-flight mark never cleared on a response (replication stops; liveness red).
- **S-repl-11:** the in-flight mark never set (the resends return; P11-01 red).
- **S-repl-12:** a heartbeat does not re-send an outstanding append (a lost append is never recovered; the lost-append test red).
- **S-struct-1:** every indeterminate write completes (no undecided histories; the both-classes guard red).
- **S-harness-5:** an entry's test arguments without the class filter (the whole project runs again; its argument test red).
- **S-harness-6:** the guard on results outside the target's class never fires (its test red).
- **S-struct-2:** the width counts completed operations only (the measure differs from the one predicted; its test red).

## Register rows

Promised here: replication resends (first; P11-02), the group commit (P11-06, by its rule), the
structural measure (P11-07). Each closes with its test or by a spec change. The rows promised to
phase 0 (the person's walk, P0) stay the person's.
