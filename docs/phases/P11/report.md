# Phase 11 — report

**Status: accepted** (reviewer, on run 37741556031, the report's head `47a9d91`). Edits at
acceptance, by the reviewer's direction:
- **the group-commit gate corrected, not its answer:** the rule measured at the highest sustained
  rate, and the behaviour that matters is at the rate where the tail breaks. Read there, the sync
  is at the gate (75 to 80%), not under it. Still not built: a group commit trades latency for
  throughput, and latency is about ten times its target with nothing yet aimed at it. Latency comes
  first; the corrected gate is then applied to the new curve (spec §2, the register's new row,
  spec §11's phase 12). That the premise was wrong, and how, is recorded in the findings, as more
  reusable than the decision;
- **P11-07's negative close is the answer,** and spec §6 now says plainly that a budget in states
  is not a budget in memory, since every known limit's curve was quoted in states;
- `install-kept-the-suffix`'s declaration is recorded as a consequence of the fix, which made a
  rare path rarer, not a property of the generator;
- the refresh rule keeps my wording (a change and its refresh are one commit), and the 90% barrier
  guard is the CRDT project's §13.38 again: a remembered figure with no boundary attached;
- **found at acceptance:** `docs/phases/status.md` had not been kept since phase 7, so the register
  gate's check on rows promised to completed phases could not fire, and the resends row was still
  marked open although this report says it closed. Both corrected; P12-01 makes the check
  mechanical.

Decisions at acceptance: `install-kept-the-suffix` below the soak floor, accepted (the fix's
consequence); `retry-deduplicated-by-a-restored-table` rare in the sample only, accepted, as at
phase 6; no group commit, latency first; both bench controls accepted, stricter being the right
direction, and every other control's margin to be checked rather than waiting for one to survive
(P12-08). Phase 12 is latency.

Replication's cost, then linearizability-checking cost (spec §11 phase 11,
as P11-00 amends it): P11-00 to P11-09, as approved, with decision 3 as recommended (one append in
flight per follower). P11-08's first half and P11-09 were done before the rest, at the reviewer's
direction. Every number in this report's tables cites its record (`m:`) or its CI run (`run:`),
which `gates measurements` checks.

This report certifies the commit that contains it. `gates reports` checks that commit's CI run on
the next push. **It is pushed with three fixes** that the phase's main push's run (run:37720359149)
found after that push, on lines and seeds the touched-file stage could not select; they are under
deviations.

## The result of the phase

### The resend defect is fixed, and a check now fails if it comes back (P11-01, P11-02)

**The check came first, and failed** (P11-01, the phase's most important task). `ReplicationCostTests`
runs three nodes with no faults and many operations in flight against the leader, and bounds the
entries the leader sends each follower per committed entry by 2, reading only the leader's own sends.
On the code before the fix: 5.99 entries per committed entry with 3 in flight, 41.52 with 8 and
1,629.10 with 32 (5,493,327 entries sent to one follower for 3,372 committed). That is phase 10's
"about 200 messages per committed write" from the real host (208 appends per committed entry at 32
in flight), reproduced in the simulator for the first time. It guards itself against a workload that
never builds a backlog (the backlog it measured is asserted, S-cost-2) and against reading the wrong
node's sends (S-cost-1).

**What it covers, and what it does not.** It bounds entries sent per committed entry, so it catches
amplification of replication traffic, the class of phase 10's defect. It does not catch a
performance pathology that has no invariant signature and sends no more entries: a slow apply, a
lock held across a sync, a queue that grows without bound, a timer firing with nothing to send.
Nothing in the suite guards those; the only check on them is a measurement like phase 10's.

**The fix** (P11-02): one append in flight per follower. A write or an answer sends nothing to a
follower with an append outstanding; a read sends such a follower an empty append carrying its
round, so a read's quorum is not held up behind a large batch; the heartbeat timer re-sends the
outstanding window, which recovers a lost append or a lost answer within one interval. An answer
clears the mark only when it reaches what the outstanding append carried. After it: 1.19, 1.07 and
1.07 entries per follower per committed entry at 3, 8 and 32 in flight, and 0.68 appends per
committed entry at 32 against 208. Seven Core tests were written first; three failed on the unchanged
node. S-repl-10 to S-repl-12 prove the mark is cleared, set, and re-sent.

**What the fix broke: constructions, never an invariant**, each listed for the reviewer under
deviations: three Core tests and the isolated-follower scale test relied on each write sending the
window; the soak sample's `retry-deduplicated-by-a-restored-table` fell under its absolute minimum
in the 300-execution sample; the read control's pinned seeds moved; the barrier control counted one
barrier list per write. Every assertion is unchanged.

### The curve, measured again: the knee from 625–1,000 to 3,125–4,000 writes a second (P11-04, P11-05)

The inputs first (P11-04): the median sync 159 µs, the loopback round trip 44 µs, under 10% from
phase 10's, so the model stands: L = 362 µs and C_design = 6,278 writes a second (one sync per
write). The target, restated before any phase-11 end-to-end record, keeps the latency criterion (a
median within 1.5 L, 543 µs, and a 99th percentile within 2.47 ms at 3,125 a second) and replaces
the ungrounded 200,000 with the design's own capacity: a sustained rate of at least C_design / 2 =
3,139 a second.

Measured (P11-05), the open-loop generator at each rate for 10 s after a 3-s warm-up, five
repetitions each, in one process (three hosts on loopback) and in Compose (three containers):

| Offered (writes/s) | In process: completed/s, the five runs | In process: median; 99th percentile | Compose: completed/s | Compose: median; 99th percentile | Records |
|---|---|---|---|---|---|
| 625 | 624.7 to 624.9 | 2.9 to 3.1 ms; 5.5 to 7.9 ms | 624.7 to 624.9 | 3.5 to 3.6 ms; 6.2 to 7.8 ms | m:p11-05-local-625-1 to -5, m:p11-05-compose-625-1 to -5 |
| 1,250 | 1,249.6 | 3.1 to 3.3 ms; 6.5 to 8.8 ms | 1,249.1 to 1,249.7 | 3.8 to 3.9 ms; 7.3 to 11.4 ms | m:p11-05-local-1250-1 to -5, m:p11-05-compose-1250-1 to -5 |
| 2,000 | 1,998.6 to 1,999.6 | 3.7 to 3.8 ms; 8.4 to 9.7 ms | 1,998.1 to 1,999.0 | 4.6 to 4.8 ms; 9.4 to 13.0 ms | m:p11-05-local-2000-1 to -5, m:p11-05-compose-2000-1 to -5 |
| 3,125 (the criterion's load) | 3,118.9 to 3,123.9 | 5.3 to 5.6 ms; 16.6 to 34.8 ms | 3,116.7 to 3,123.9 | 6.1 to 7.0 ms; 11.2 to 38.2 ms | m:p11-05-local-3125-1 to -5, m:p11-05-compose-3125-1 to -5 |
| 4,000 | 3,994.6 to 3,998.3 | 6.4 to 14.7 ms; 19.7 to 287.0 ms | 3,890.1 to 3,995.7 | 12.6 to 469.5 ms; 349.8 to 599.8 ms | m:p11-05-local-4000-1 to -5, m:p11-05-compose-4000-1 to -5 |
| 5,000 | 4,204.6 to 4,547.6 | 0.59 to 0.99 s; 0.74 to 1.31 s | 3,466.6 to 3,776.4 | 1.57 to 1.90 s; 1.88 to 2.46 s | m:p11-05-local-5000-1 to -5, m:p11-05-compose-5000-1 to -5 |
| 9,375 | 2,214.3 to 2,374.7 | 6.8 to 7.0 s; 9.9 to 10.3 s | 1,510.7 to 1,803.9 | 7.4 to 7.9 s; 10.7 to 11.0 s | m:p11-05-local-9375-1 to -5, m:p11-05-compose-9375-1 to -5 |

The A/B, interleaved in one session on one machine, phase 10's head against the fix:

| Offered (writes/s) | Phase 10's head: completed/s | The fix: completed/s | Records |
|---|---|---|---|
| 625 | 624.9 in all five | 624.7 to 624.9 | m:p11-05-ab-base-625-1 to -5, m:p11-05-ab-fix-625-1 to -5 |
| 1,250 | 4.4 to 1,246.7 (collapsed in 4 of 5) | 1,249.6 in all five | m:p11-05-ab-base-1250-1 to -5, m:p11-05-ab-fix-1250-1 to -5 |
| 3,125 | 0 in all five | 3,122 | m:p11-05-ab-base-3125-1 to -5, m:p11-05-ab-fix-3125-1 to -5 |

- **Throughput: met in process, missed in Compose, at the curve's resolution.** Everything offered
  completes up to 3,125 a second in both; 4,000 holds its 99th percentile in 3 of 5 in-process runs
  and in none in Compose. The knee moved six times, inside the predicted 2,000 to 5,000.
- **Latency: missed by about ten times.** The median at 3,125 is 5.3 to 5.6 ms in process against
  543 µs; the 99th percentile 17 to 35 ms against 2.47 ms. Below the knee the median is about 3 ms,
  about eight times L, as in phase 10: the fix did not change the latency at low load, and nothing in
  this phase was aimed at it.
- **Past the knee the cluster degrades, it does not collapse:** 4,205 to 4,548 complete at 5,000
  offered, but 2,214 to 2,375 at 9,375. The leader sends 1.64 messages per committed write at most,
  and syncs once per write (1.002) at every rate.

### The group commit: not built, by the rule written first; and the sync is the cap (P11-06)

The rule, written before the measurement: build a group commit in this phase only if the leader's
sync is at least 80% busy at the highest rate sustained. The host now times each of its syncs:

| Offered (writes/s) | Leader's sync busy fraction | Median sync under load | 99th percentile latency | Records |
|---|---|---|---|---|
| 3,125 (sustained in every run) | 0.61 to 0.67 | 195 to 216 µs | 15.2 to 37.7 ms | m:p11-06-local-3125-1 to -5 |
| 4,000 (the 99th percentile breaks) | 0.75 to 0.80 | 188 to 200 µs | 61.4 to 536.9 ms | m:p11-06-local-4000-1 to -5 |

**Under 80% at the highest sustained rate, so no group commit was built** and the row is closed by
a spec change (§2, `4b2a4ee`). **But the rule's premise is what the measurement contradicts.** The
rule said that under 80% the cap would be elsewhere; at 4,000, where the tail breaks, the sync is
0.75 to 0.80 busy and each sync takes 188 to 216 µs under load against 159 µs idle. A single server
near 80% utilisation is where a queue's tail explodes: the sync is the binding resource. The spec
and the register say so. **Put to the reviewer:** the rule decided "not in phase 11" correctly by its
own terms; a group commit is what would move this cap, and whether a later phase builds it is yours.
**Corrected at acceptance** (reviewer): the rule, not the answer. The gate is read at the rate where
the tail breaks, here 4,000 writes a second, where the sync is at it (75 to 80%). The group commit
waits behind latency (phase 12), and the corrected gate is applied to the curve phase 12 produces.

### Correctness at scale after the fix (P11-03)

Both soaks at 1,000 executions on the same seeds, before and after the fix (the 10,000 run in CI):
every dimension within 5 points but one, and the same entries committed per execution (133 against
132, and 82 against 82), because the soaks' three clients with think time never build the backlog
the fix acts on. The one that moved is `install-kept-the-suffix`: 7.4 to 2.8% in the baseline and
2.6 to 0.7% with membership changes, under the 1% floor. Under the resends, chains of appends crossed
a snapshot's chunks and delivered entries past its index; now a follower being sent a snapshot gets
none meanwhile. **Declared below the soak floor**, with both measurements and the test that exercises
the path directly; it must still clear the absolute minimum. Put to the reviewer.

### The structural measure (P11-07)

**No structural quantity fixed in advance separates the histories the checker decides from those
it cannot, and the spec now states the limit without a threshold (§6).** Neither soak has had an
undecided key since phase 8, so the question was asked of generated single-key histories, every one
built from a linearizable store, with a share of their writes lost: 54 histories of 8 to 20 clients,
checked at 1,000,000 states, then the 20 not decided there taken to a long search of 16,000,000
states, narrowest first, until five stayed undecided.

- **The width fails, the way it was predicted to and by much more.** Histories of width 41, 43 and
  45 are undecided at 16,000,000 states; widths up to 53 were decided in under 1,000,000, and 55, 59
  and 63 by 12,048,677. Phase 6 predicted every undecided history at least 4 wider than the widest
  decided one.
- **Neither other candidate does better.** Over the final classes (47 decided, 5 not), the best
  single threshold on the width, the lost writes, the read choices, or the client count misplaces 3
  of 52.
- **What drives the search is concurrency, not indeterminacy.** All five undecided histories have
  twenty clients. A sabotage's first version showed it most plainly: with no write lost at all,
  twenty-client histories were not decided in 1,000,000 states, while six clients took 286 states;
  three clients with thirteen lost writes took 174. That is an observation on one grid, not a
  threshold.
- **The budget is memory, and a state's size grows with the history.** The search keys a state by a
  character per operation, so the soak's 32,000,000 states, 7.2 to 12.5 GB on its keys of a few
  dozen operations, were killed for memory on a 16-GB machine for these 240-operation histories,
  twice; 16,000,000 took 9.3 GB. The five are undecided at the soak budget's memory, not its state
  count, which the spec now says.

### The harness: every entry runs its target's class (P11-08, P11-09)

Moving the slow host tests to their own project (P11-08) cut S-hostdisk-1 from 272.7 to 21.4 s and
`Raft.Host.Tests` from about 127 to 22 s locally, and left the largest-manifest model at 605 s, over
its 600-s bound: every placeholder is priced at the costliest recorded entry, then S-lin-7 at
334.9 s, and 24 of the 26 Scale entries cost 139 to 335 s for one reason, each ran its target's
whole project. Running each entry's target class only (P11-09): 23 of those 24 fell to under a
third, most to under a tenth; the worst recorded cost from 334.9 to 92.2 s; the model at 336 entries
from 605 to 250 s, and 317 s after the costs were refreshed from GitHub at this report. **What it costs:** "neighbours also red" names siblings in the target's class
only. It was informative twice (a guard's real scope learnt from three red tests; S-lin-4's
degradation in phase 8); a reader who needs cross-class collateral runs the project by hand.

## Done criteria

- **The resend defect fixed, shown failing before the fix:** P11-01 (the check, red on the code
  before), P11-02 (the fix, the check green, seven Core tests first). S-cost-1, S-cost-2, S-repl-10
  to S-repl-12.
- **Every invariant and both soaks still hold:** P11-03 (1,000 each locally, before and after);
  10,000 each in CI, both green in run:37720359149, with no undecided search.
- **The offered-load curve measured again against the design's own model:** P11-04, P11-05.
- **The group commit decided by the rule written first:** P11-06, not built; the row closed by
  `4b2a4ee`.
- **The structural measure, or the limit stated without one:** P11-07, no threshold; the limit stated in §6.
- **The slow host tests in their own project, every entry run by class:** P11-08, P11-09; S-harness-5,
  S-harness-6.

## Predictions

**Evidence 9, forcing 1.** On evidence: right 1, partly 6, wrong 2. Each outcome is in the breakdown.

- **P11-00, wrong:** no replication sentence needed changing; §2's exclusion and §8's barrier
  sentence did.
- **P11-01, partly:** the check failed by far more than predicted (1,629 entries per committed entry
  at 32 in flight), and also with three in flight, where I said it would pass.
- **P11-02, wrong (forcing):** no read test failed first, because writing the prediction made reads
  their own send mode.
- **P11-03, wrong:** one soak would have failed, on a dimension I did not name
  (`install-kept-the-suffix`); the one I named did not move.
- **P11-04, right:** the inputs moved by under 10%.
- **P11-05, partly:** the knee moved six times, inside the predicted range; past it the cluster
  degraded by more than 20%.
- **P11-06, partly:** under 80%, as predicted; and the sync is the cap, against the rule's premise.
- **P11-07, partly:** the width does not separate, as predicted; the read choices are no better.
- **P11-08, partly:** the move cut its two numbers as predicted and left the plan over its bound.
- **P11-09, partly:** the class was the cause; the predicted exceptions were wrong.

## What exists

- **P11-01:** `ReplicationCostTests` (the leader's sends decoded; entries per follower per committed
  entry; the backlog and the single election asserted). S-cost-1, S-cost-2.
- **P11-02:** `RaftNode`'s in-flight mark per follower and three send modes (replicate, probe,
  resend); seven `ReplicationTests`. S-repl-10, S-repl-11, S-repl-12.
- **P11-03:** `install-kept-the-suffix` declared below the soak floor; `retry-deduplicated-by-a-restored-table`
  rare in the sample.
- **P11-04:** `docs/design/performance-target.md`, phase 11's section.
- **P11-05:** `scripts/bench.sh load-ab` (phase 10's head against the current, interleaved), task
  ids in records.
- **P11-06:** the host's sync timing (`CountingFileSystem`), `leader_sync_us` and `leader_sync_busy`
  in a load record; the §2 statement.
- **P11-07:** `StructuralMeasure` (the generator and the three candidates), `StructuralMeasureTests`.
  S-struct-1, S-struct-2.
- **P11-08:** `Raft.Host.Slow.Tests`.
- **P11-09:** `EntryTestArgs` (`--filter-class`), the `BeyondClass` guard. S-harness-5, S-harness-6.

**Test projects** (at the start of phase 11 in brackets): Architecture 52 (50), Gates 160 (158),
Checker 173 (170), Simulation 110 (110), Core 852 (845), Scale 26 (23), Budget 3 (3), Membership 3
(3), Host.Tests 26 (33), Host.Slow.Tests 7 (none; the seven moved from Host.Tests).

## Deviations and choices for the reviewer

- **Constructions changed by the fix, assertions never** (P11-02): `ALeaderPersistsAClientsEntryBeforeSendingIt`
  and two compaction tests deliver the answers they need before the next write; the isolated-follower
  test uses twenty closed-loop clients, not fifteen (fifteen fell 766 entries behind instead of
  1,000); the read control's pinned seeds are 33, 84 and 1289, from the measurement run again (7 of
  3,000, as before); the barrier control asks for barrier lists on 90% of writes (a write sent from
  an answer's effect list has none; 39 of 40 measured), which GitHub broke, and is now one list per
write by construction (below). For the reviewer to check each.
- **`retry-deduplicated-by-a-restored-table` rare in the 300-execution sample** (P11-02): 4 to 2 of
  300, under the absolute minimum, while the sample committed the same entries per execution; the
  10,000-execution soak still holds it to the minimum.
- **`install-kept-the-suffix` below the soak floor** (P11-03), above.
- **P11-06's rule against its premise**, above: the group commit, reopened or not, is yours.
- **S-struct-1 changed** (P11-07): the breakdown's version (every write made to complete) was
  caught, but by the separation assertion, because twenty-client histories stay undecided without
  any lost write. It now builds every history with three clients, which the both-classes guard
  catches; the breakdown's sabotage list says so.
- **P11-07's long search is 16,000,000 states, not the soak's 32,000,000, and covers the narrowest
  histories, not all:** 32,000,000 states of a 240-operation history do not fit 16 GB (killed twice),
  and 16,000,000 take about the memory the soak's budget takes on its own keys. The search went
  narrowest first until five stayed undecided; the two widest were never taken. The breakdown asked
  for every history at the soak's budget; the refutation needs only the narrowest.
- **An unpushed commit reworded, and ten records relabelled.** The local run before this push found
  P11-05's records commit without a `Task:` trailer (it was labelled documentation, and records are
  not). It was reworded before the push, which changed the ids of the five commits after it. P11-06's
  ten records named one of them, `3b7f035`, as the commit measured; they now name `2cf7557`, the same
  tree under its new id (the commit's message says so, and `git diff 3b7f035 2cf7557` is empty). The
  spec commit the register cites is `4b2a4ee`, formerly `3ce182e`. Nothing pushed was rewritten.
  The same local run found S-ran-2 and S-reg-5 stale against P11-07's baseline and register edits;
  each was regenerated in the commit that made it stale.
- **The touched-file stage** (each fraction reported, as the reviewer asked). The plans printed
  before anything ran, at each push of the phase: 11 of 311 entries (3.5%, run in 142 s) at
  `f6a3d77`; none of 311 at `58bed2b`; **67 of 318 (21.1%) at `254cd0a`, estimated 15.3 minutes,
  over the 11-minute threshold, run with `--accept-estimate`** (it took 381 s); none of 318 at
  this report's push, whose three fixes change no file a patch or control names, so S-soak-6,
  S-bench-1, S-bench-2 and S-bench-3 were run by hand on their commits (all caught). The push that went over carried the replication fix, the soak declarations,
  the host's sync timing and P11-07, whose patches and controls name `RaftNode`, the soak files, the
  test baseline and the register; I accepted it rather than split the push, because every commit in
  it had to reach CI together for the report's numbers, and the per-commit matrix checks each one.
- **Three fixes after the main push, pushed with this report.** Run 37720359149 was red on two of
  the head's shards and on twelve of its thirteen commits (the table below):
  - *The recorded costs, refreshed from GitHub's medians.* S-iface-1's and S-disk-3's lines, which
    P11-09's local refresh had left five to seven times too high, failed the staleness check, and one
    shard's total was over twice its run. Every line is now the median over the 13 jobs of that run
    that ran the entry; the file's total did not move (4,488 s recorded, 4,494 s measured), twenty
    lines moved by more than a factor of 2. S-sess-3 tripled at the resend fix itself (about 90 s
    before `af62a4a`, 183 to 273 s after), a cost change that commit should have carried; it is now
    the costliest entry (249.5 s), which prices the largest-manifest test's placeholders: the model at
    336 entries is 317 s, against the 600-s bound.
  - *The Budget tests' hardest seeds, re-picked.* The resend fix changed every soak history, and phase
    8's hardest seed (5908, 2,477 states) became easy enough that S-soak-6's 1,000-state budget decided
    it: S-soak-6 survived from `af62a4a` on. The baseline soak at 10,000 executions on the fix, run
    locally, names 9532 (3,702 states), 6673 (2,371) and 9824 (890); the tests and S-soak-6 name them.
  - *The stall control judged at the 97th percentile, not the 99th* (a P10-03 control). S-bench-1
    survived 2 of 13 times: its coordinated omission leaves one delayed write per stall, 4 or 5 of
    about 400, so at the 99th percentile whether it was caught turned on how many stalls fell in the
    window. Measured locally, unpatched: 31 to 32 writes over 150 ms, a 97th percentile of 235 to 248
    ms; with S-bench-1: 4 writes, 3.0 to 3.9 ms. The check on the real generator is stricter (12
    delayed writes needed, not 4), and the sabotage would need 12. For the reviewer, since it changes
    an approved control.
- **The barrier control's guard, by construction** (a P10-05 control, changed at P11-02). Since
  the fix, a write that arrives while every follower has an append outstanding is sent from an
  answer's effect list, which holds no persist, so open loop the share of writes with a barrier list
  is the machine's timing: 39 of 40 locally, 28 of 40 on GitHub (run:37737168056), against the 90% I
  had set from the local figure. The control now drives one closed-loop client: each write is sent
  after the previous one committed, so the follower whose answer committed it has nothing
  outstanding, and every write's list sends behind its persist. The guard is one list per write
  again, as before P11-02, after a short settling run: in one of five local runs without it the first
  write arrived while both followers still had the new leader's no-op outstanding (90 lists for 91
  writes). Measured locally with it: 94 or 95 lists for as many writes in eight runs, 20.7 to 21.1 ms
  a list against 20.1 to 20.2 ms slept; with S-bench-3, 0 lists in four.
- **S-bench-2's baseline missed its floor once** (1.497 against 1.5, at `3e3b7b3`, one of 13 runs),
  as its upper bound did once in phase 10; left unchanged, as the reviewer ruled then.
- **Latency is still about ten times the target**, and nothing in this phase aimed at it.

## The new work's cost in CI (P11-08)

Measured on the phase's three pushed runs, every job a first attempt: `f6a3d77` (run:37693717232),
`58bed2b` (run:37701188082) and `254cd0a` (run:37720359149). Compared with phase 10's three:
run:37569743638, run:37584673088 and run:37594669723. This report's own push is not in them.

| | Phase 11 (three runs) | Phase 10 (three runs) | Runs |
|---|---|---|---|
| Harness shards (12; 11 in phase 10), slowest and fastest step | 320 and 180 s; 314 and 136 s; 361 and 138 s | 580 and 360 s; 590 and 328 s; 634 and 295 s | run:37693717232, run:37701188082, run:37720359149, run:37569743638, run:37584673088, run:37594669723 |
| Gates step | 4, 3 and 3 s | 4, 3 and 4 s | run:37693717232, run:37701188082, run:37720359149, run:37569743638, run:37584673088, run:37594669723 |
| Tests step | 329, 324 and 264 s | 252, 192 and 288 s | run:37693717232, run:37701188082, run:37720359149, run:37569743638, run:37584673088, run:37594669723 |
| Baseline soak job | 897, 494 and 834 s | 491, 640 and 874 s | run:37693717232, run:37701188082, run:37720359149, run:37569743638, run:37584673088, run:37594669723 |
| Membership soak job | 952, 957 and 828 s | 651, 991 and 925 s | run:37693717232, run:37701188082, run:37720359149, run:37569743638, run:37584673088, run:37594669723 |
| Compose job | 96, 92 and 100 s | 98, 105 and 105 s | run:37693717232, run:37701188082, run:37720359149, run:37569743638, run:37584673088, run:37594669723 |
| Per-commit jobs (commits by shards), all given a runner | 84, 12 and 144 | 11, 66 and 55 | run:37693717232, run:37701188082, run:37720359149, run:37569743638, run:37584673088, run:37594669723 |
| Longest per-commit job | 829, 511 and 774 s | 716, 848 and 949 s | run:37693717232, run:37701188082, run:37720359149, run:37569743638, run:37584673088, run:37594669723 |

- **The shards:** the slowest is 220 to 320 s under phase 10's in every run, P11-09's class filter;
  the spread between fastest and slowest stayed 1.8 to 2.6, run noise (reviewer: not chased).
- **The tests step** grew by the phase's new tests (the replication cost check, the structural
  measure's small grid); **the soaks** moved within the runner's spread, both times.
- **Every job of the three runs was given a runner** (240 per-commit jobs, 36 shards): none never
  ran, against 34 at phase 10's report push.
- **The local run before the main push** (`254cd0a`): about 31 minutes, the report rule, preflight,
  build, gates and tests (about 6.5), the host sabotages with the secret scan and the README walk
  (about 17.5), and the 67 touched entries (381 s).

## Register

- **Closed:** replication resends (P11-02, `ReplicationCostTests`); the group commit (P11-06, by
  `4b2a4ee`, not selected by its rule, the sync named as the cap); the structural measure (P11-07,
  done, no threshold; the limit stated in §6).
- **Opened at acceptance:** commit latency, then the group commit by the corrected gate (phase 12).
  The resends row, still marked open when this report said it closed, was closed at acceptance with
  its test.

## Findings added this phase

In `docs/findings.md` under a new Phase 11 heading, which also takes the three written during
phase 10's acceptance push and P11-08 (the staleness check's first outlier, a worst-case test's
lever being a class of entries, a push's commits checked with their own tooling):
- a fix that changes when messages are sent breaks constructions, not invariants, and each one is
  named;
- the defect fed a rare safe path, and the distribution's floor is what saw it go;
- a decision rule's premise is a prediction too, and it can be refuted while the threshold decides;
- the checker's budget is memory, and a state's size grows with the history;
- a sabotage caught by the wrong assertion taught more than the measurement it guarded;
- the commit-ordering rule corrected: a change and the refresh it makes necessary are one commit
  (refreshing first fails the same way when the change is larger than the staleness factor); the
  line keeps the reviewer's wording, "a refresh must precede what makes it stale", as its title;
- a change to the node moves facts derived from the soak in entries that never name the node (the
  third instance of the ordering finding: S-soak-6's seed and S-sess-3's cost);
- a sabotage whose effect sits at its check's threshold is caught by chance;
- at acceptance: the gate's premise, and how it was wrong (read where the system breaks, not where
  it holds); the 90% guard as §13.38; a gate that reads a hand-kept file goes silent when the file
  is not kept.

## Still the person's

- Adding `soak-membership` and `compose` beside `soak` in branch protection.
- Deleting `prerewrite-b96fc4b`, the probe branches and the sabotage branch.
- The cold walk of the README.
- P0.

## Commits never verified in CI

Twenty-two pushed commits of the phase never had a green verdict of their own: eight of its first
push (run:37693717232), twelve of its main push (run:37720359149), and one of each of the report's
first two pushes (run:37732711680, run:37737168056). **Every one failed; none never
ran**: every (commit, shard) job of both runs was given a runner and ran its checks, 84 and 144
per-commit jobs and each head's twelve shards (and 36 more in the report's first push). One commit of
the main push, `d00c396`, passed on every shard. **Accepted as never verified** (reviewer, at phase 10's acceptance), as in phases 7, 9
and 10: rewriting them would force-push, which `each-commit-list` refuses by design. The causes,
read from each job's annotations:

| Commit | Run | Result | Jobs | Cause |
|---|---|---|---|---|
| `e74ca16` | run:37693717232 | failed | shard 10 | S-hostdisk-1's cost line (64.2 s recorded, 347.5 s run): the line phase 10's acceptance push found stale, corrected one commit later; documentation only |
| `f0a081d` | run:37693717232 | failed | shards 1, 5, 7, 8 | the largest-manifest test (603 s modelled against the 600-s bound), in the Gates tests and so in every entry whose baseline runs them (S-ci-1, S-count-1, S-count-2) and in S-shard-7's |
| `fba257c` | run:37693717232 | failed | shards 1, 2, 4, 9, 11 | the same test (607 s); S-hostdisk-1's corrected line (330 s), now 20 s once the slow tests had moved; S-kill-1's line (75.2 s recorded, 12.6 s run) |
| `91b6fd2` | run:37693717232 | failed | shards 1, 2, 4, 9, 11 | the same as `fba257c`; documentation only |
| `772a379` | run:37693717232 | failed | all 12 shards | every Scale entry's cost line, five to thirty times its new class-filtered time, and every shard's total beyond the shard factor: the change the next commit's refresh was for |
| `002d71d` | run:37693717232 | failed | shards 6, 12 | S-shard-7 survived (the largest manifest now fits even dealt by id); S-rng-1's refreshed line (61.8 s, from an outlier local run; 11.6 s) |
| `79e6775` | run:37693717232 | failed | shards 6, 12 | the same; documentation only |
| `f6a3d77` (head) | run:37693717232 | failed | sabotage 6/12, 12/12 | the same two, both fixed at `decc1a8` |
| `3ca06cd` | run:37720359149 | failed | shard 11 | S-disk-3's cost line (73.1 s recorded, 14.2 s run), left wrong by P11-09's local refresh; documentation only |
| `70a82da` | run:37720359149 | failed | shard 4 | S-bench-1 survived (the stall control at its 99th percentile; the code is phase 10's head's, where it was caught); documentation only |
| `af62a4a` | run:37720359149 | failed | shards 5, 12 | S-soak-6 survived (the resend fix made phase 8's hardest seed easy); S-iface-1's cost line (36.2 s recorded, about 4 s run) |
| `472d6ff` | run:37720359149 | failed | shards 5, 12 | the same two |
| `ef7923b` | run:37720359149 | failed | shards 5, 12 | the same two |
| `3e3b7b3` | run:37720359149 | failed | shards 3, 5, 9, 11, 12 | the same two; S-disk-3's line; one shard's total over twice its run; S-bench-2's unpatched control at 1.497, under its floor of 1.5; documentation only |
| `074f65a` | run:37720359149 | failed | shards 5, 12 | S-soak-6 and S-iface-1, as at `af62a4a` |
| `2cf7557` | run:37720359149 | failed | shards 4, 5, 12 | the same two; S-bench-1 survived |
| `4b2a4ee` | run:37720359149 | failed | shards 5, 11, 12 | the same two; S-disk-3's line |
| `6adc1cd` | run:37720359149 | failed | shards 5, 12 | S-soak-6 and S-iface-1; documentation only |
| `309a999` | run:37720359149 | failed | shards 7, 11, 12 | the same two; S-disk-3's line |
| `254cd0a` (head) | run:37720359149 | failed | sabotage 7/12, 12/12 | S-soak-6 and S-iface-1, fixed at `4363885` and `2e91f6a`; documentation only |
| `2e91f6a` | run:37732711680 | failed | shard 11 | S-soak-6 survived: the cost refresh comes before the seeds' re-pick (`4363885`), which then passed on every shard, as did `c9c6fc5` |
| `4e5e0c5` (head) | run:37737168056 | failed | sabotage 8/12 | S-bench-3's unpatched barrier control: 28 barrier lists for 40 writes, under the 90% I set at P11-02 from one local measurement (39 of 40); the guard is now one list per write by construction, in this commit; documentation only |

**Four of the eight are my commit ordering, not the code.** `f0a081d` corrected S-hostdisk-1's line
to its true cost, which took the largest-manifest model to 603 s, over its bound, and so failed the
Gates tests at that commit and the next two; only `772a379` and `002d71d` brought it back under.
`fba257c` moved the slow tests, which made S-hostdisk-1's and S-kill-1's lines five times too high;
`002d71d` refreshed them. `772a379` made every Scale line five to thirty times stale at the commit
that added the class filter. In each,
the commit that changed a cost and the commit that refreshed the lines were different commits, and
every commit between ran its own harness against the other's data. **A refresh cannot simply come
first either:** a line refreshed before a change of more than five times is stale against the old
harness by the same factor. The change and its refresh must be one commit. The other three, `002d71d` to the head, are two
defects of mine fixed at `decc1a8`: S-shard-7 aimed at a test the class filter had made unable to
fail, and S-rng-1's refreshed line taken from an outlier local run. `e74ca16` is phase 10's stale
line, found by that phase's acceptance push.

**This report's commit moved.** Its first push (`8851761`, run:37732711680) was green on every job
of its head, and red on one per-commit job: `2e91f6a`, the cost refresh, ordered before the seeds'
re-pick, so S-soak-6 still survived there. Either order left one of the two red; only one commit
carrying both would not have. A run with a red per-commit job does not certify its head (`gates
reports` requires the run, and its `each-commit` job, to succeed), so the report was re-committed at
the next push's head, alone in its push (`4e5e0c5`, run:37737168056). **And moved again:** that
run was red on one of the head's shards, S-bench-3's baseline (above), a guard of mine from P11-02
that set a threshold where the design's premise had changed. The fix, the guard by construction, is
in this commit with the report, alone in its push, so the run that checks the fix certifies the
report.

**The main push's twelve are three defects of mine and two controls.** S-iface-1's and S-disk-3's
lines were wrong since P11-09's local refresh (`2e91f6a` refreshed every line from that run's
GitHub times); S-soak-6's seed went stale at the resend fix itself (`4363885` re-picked the seeds
from the soak on the fix), the third instance of the ordering finding; S-bench-1 surviving twice
showed its control judged at a threshold its sabotage sits on (`c9c6fc5`); S-bench-2's one baseline
miss is left as the reviewer ruled for its other bound.
The finding is in
`docs/findings.md`. Every commit of the first push is contained in `58bed2b`, whose run
(run:37701188082) is green in full, and every commit of the main push in this report's commit, whose
run certifies it; that does not make them green on their own.
