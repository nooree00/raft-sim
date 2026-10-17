# Phase 15 — report

**Status: accepted** (reviewer, on run 38824440999, the report's head `00def9a`). **The margins check
catching my own `margins.sh` change is the phase's best result** (the reviewer): a currency observer
firing on its author's edit, correctly marking all eighteen rows stale, is what remembering cannot do;
phase 14 built it, phase 15 is where it paid. The two-cause separation is the substance: the batch's
holds 0.73, the pipeline's wait 0.27, the check failing on phase 14's code first, and separable only
because of the emission stamp, P11-01's shape against a hypothesis with a rival. Decided at
acceptance, by the reviewer's direction:
- **the median gap is a measured limit of the design** (decision 1): a single-threaded sync loop has a
  floor and the floor is above the criterion, stated in spec §8 with the number (`87197ba`); 280 µs on a
  criterion restated twice, with 0.71 of the remainder in one mechanism, does not justify overlapping
  the next batch with the sync;
- **the hand-offs are measured apart first, then the corrected rule** (decision 2, phase 16):
  client-to-leader and leader-to-send are the dominant terms of a reading the old rule could not
  express, and a rule governing a reading before its dominant terms are understood is how the first
  one went wrong;
- **the replication pipeline is not phase 16's subject** (decision 3): widening it reverses P11-02, the
  fix for the 200-times resend defect, for throughput past an operating point pinned at a third of the
  capacity; recorded and left, its row open and unhomed (P15-09, added here, lets the register hold it);
- **the 1.5-times gate stays per run** (decision 4): a set-wide gate discards valid runs because others
  were out, and per run produced the only readings trusted;
- **the rows:** the median gap closed by decision 1; the tail and hand-offs to phase 16; the limit past
  9,375 open and unhomed; S-fsync-1's row reworded to keeping each entry's harness output, not
  explaining this occurrence, and homed to phase 16 (the second unexplained harness result across both
  projects blocked by the same missing output);
- **a finding:** an instrument's cost model is itself a measurement.
Phase 16's row is in spec §11; its breakdown is not yet written.

The leader's queue, measured first (spec §11 phase 15, P15-00's row): P15-00 to P15-08 as approved,
with the amendments the breakdown's header lists, each made before the runs it concerns. Every number
in this report's tables cites its record (`m:`) or its CI run (`run:`). This report certifies the
commit that contains it; `gates reports` checks that commit's CI run on the next push. **It is pushed
after two pushes of the phase's work**: `ac1bc6e` (run:38795000252, the instrument, the capacity and
class reach) and `429c5b2` (run:38811415917, the remedy, its curve and the audit), both green on their
first attempt.

## First: a currency observer fired on its author's edit (P15-07)

After the first margins audit I changed `scripts/margins.sh`'s default output path. The script is among
every row's sources, so P14-06's observer marked all eighteen rows stale at the next check, before the
report, and the audit was run again at the head rather than relabelled. Nothing about the edit looked
like it touched the audit; an observer that reads the dependency from git catches what its author does
not remember. Phase 14 built the check after a stale audit had been accepted; this is the first time it
fired on a live change, and the change was its author's own.

## Second: two mechanisms, separated before building, and the remedy took the larger (P15-03, P15-05)

Phase 14 read commit-respond and leader-to-send each growing by about one leader sync at 3,125 writes
a second as a batch holding every output until its one sync. Drafting this phase found a second
mechanism with the same signature: the leader keeps one append in flight per follower (P11-02), so an
entry can wait a round trip for the append that carries it. A trace that stamps an output when it
leaves cannot tell the two apart. So the host now stamps each output when the node emits it, with what
it waits for: a persist of its own list (A), only an earlier list's (B), or none (C, which node-interface
§4 lets leave at once). The hypothesis was written as a check and run on phase 14's host first: it
failed (`write entries.log / sync entries.log / sync-dir / respond 7 / send n2 / send n3`, the response
and the send behind no persist leaving after the sync).

Generator separate, traced keeping one write in ten, five runs each:

| Leader-to-send and commit-respond, mean per write | 1,250 | 3,125 | growth | share | records |
|---|---|---|---|---|---|
| the two segments together | 230 µs | 838 µs | 608 µs | 1 | m:p15-03-s10-r1-local-1250-1 to -r5, m:p15-03-s10-r1-local-3125-1 to -r5 |
| held behind B or C (the batch's hold) | 141 µs | 587 µs | 446 µs | 0.73 | m:p15-03-s10-r1-local-1250-1 to -r5, m:p15-03-s10-r1-local-3125-1 to -r5 |
| before emission (the pipeline's wait) | 83 µs | 247 µs | 164 µs | 0.27 | m:p15-03-s10-r1-local-1250-1 to -r5, m:p15-03-s10-r1-local-3125-1 to -r5 |

The rule fixed before measuring admits a remedy at half; the holds are 0.73. At 1,250 and 2,000 the
same holds (141 and 238 µs a write) exceed the group commit's excess over the reverted host (117 and
191 µs): both observables met, the case that corroborates the batch's hold. Both mechanisms are real;
the hold is the larger.

The remedy (`c7618d2`): a batch's outputs before its first persist leave as the executor reaches
them, and the loop hands a batch's client requests to the node after its other inputs, so an answer's
outputs precede the requests' persists (B becomes C). Core and the simulator are unchanged; the check
lands green. Interleaved against the pre-remedy host, generator separate, untraced, five runs a side:

| Median | 100 | 625 | 1,250 | 2,000 | 3,125 | records |
|---|---|---|---|---|---|---|
| before the remedy | 1,767 µs | 1,193 µs | 1,276 µs | 1,415 µs | 1,682 µs | m:p15-05-ab-base-100-1 to -5 (and each rate's) |
| with it | 1,745 µs | 1,185 µs | 998 µs | 1,150 µs | 1,478 µs | m:p15-05-ab-fix-100-1 to -5 (and each rate's) |

At 3,125 the median falls 204 µs and still misses its criterion (1,198 µs); commit-respond's hold at
its median falls from 285 to 16 µs, and every response leaves as class C. The group commit's excess
over the reverted host at 1,250 and 2,000 is gone with it. **The 99th percentile does not fall** (8.8
ms against 7.9 at 3,125, m:p15-05-ab-fix-3125-1 to -5).

**What remains of the median's gap,** by P14-04's rule on the median against the floor's rate: the
traced segments' growth from 1,250 to 3,125 is 339 µs with the remedy (794 before), the loop's queues
0.71 (leader-queue +125, leader-queue-answer +117). Its reading checked against those segments: a
write or an answer arriving while the loop is in the previous batch's sync waits for it, their medians
153 and 164 µs, about half of the leader's 304-µs sync. That is the group commit's one sync at a time
on the loop. Of leader-to-send's and commit-respond's remaining growth, 0.68 is the pipeline's wait.
Both are rows; their homes are yours.

## Third: the rule fixed for the tail had no branch for what the tail is (P15-01, P15-04)

The load generator now runs in a process of its own. That took its lateness out of the traced tail
(37 ms at the 99th percentile in the hosts' process, 0.4 ms in its own) and left the untraced figures
where they were. It did not take the trace's own cost out of the hosts: fully traced, the 99th
percentile was still five times the untraced. Decision 3's contingency, a trace keeping one write in
N, was built twice. The first kept one write in ten and came to 2.5 times; keeping one in a million
cost as much (2.9 times), so the cost was per message (every client stamp kept, every message copied
and decoded), not per write kept. The second holds a request's stamps until its response and reads
each message's header in place: 1.57 and 1.40 times on aggregate in two sets of five, two and four
runs within 1.5 times.

| At 3,125, generator separate | median | 99th percentile | generator lateness, 99th | records |
|---|---|---|---|---|
| in process, untraced | 2,020 µs | 10.4 ms | 2.75 ms | m:p15-01-local-untraced-3125-1 to -5 |
| separate, untraced | 2,035 µs | 8.7 ms | 0.47 ms | m:p15-01-separate-untraced-3125-1 to -5 |
| in process, fully traced | 1,970 µs | 44.2 ms | 37.1 ms | m:p15-01-local-traced-3125-1 to -5 |
| separate, fully traced | 2,063 µs | 43.7 ms | 0.36 ms | m:p15-01-separate-traced-3125-1 to -5 |
| separate, first trace, one write in ten | 2,031 µs | 22.1 ms | 0.30 ms | m:p15-01-separate-sampled-3125-1 to -5 |
| separate, first trace, one in a million | 1,977 µs | 23.7 ms | 1.88 ms | m:p15-01-floor-s1000000-r1-local-3125-1 to -r3 |
| separate, untraced, beside the second | 1,922 µs | 8.3 ms | 0.50 ms | m:p15-01-trace2-untraced-r1-local-3125-1 to -r5 |
| separate, second trace, one write in ten | 2,064 µs | 13.0 ms | 0.49 ms | m:p15-01-trace2-s10-r1-local-3125-1 to -r5 |

**Superseded at phase 16 (P16-01, P16-02):** the composition in this paragraph was read through the
second sampled trace, which kept each sampled stamp as an object until the run ended; at 3,125 writes a
second its collections paused the hosts' process 125 to 133 ms a run against 38 to 46 untraced, behind a
loop 0.93 busy, and the pauses showed as client-to-leader's tail (1.9 to 2.3 ms at its 99th percentile).
With the stamps kept in arrays allocated at the trace's start, the traced tail is the untraced one and
client-to-leader holds 0.03 of the excess, its 99th percentile 0.2 to 0.4 ms; the loop's queues hold
0.40 (m:p16-02-gc-*, m:p16-02-gc2-*, m:p16-02-set2-*). The reading below, and decision 2's "in the
leader's hand-offs", measured the trace as much as the system. That P14-04's rule had no branch for what
it found stands; what it found does not. The tail measured apart, and the corrected rule's reading of it,
are in `docs/phases/P16/report.md`: beside one busy process, the reading this phase's composition was
taken to predict, the hosts' thread hand-offs did not grow; the leader's queue did, while the loop was
less busy (P16-04; m:p16-04-busy-s10-r1-local-3125-1 to -r5, m:p16-04-plain-s10-r1-local-3125-1 to -r5).

The tail rule read only the six traced runs within 1.5 times their set's untraced 99th percentile.
Their tail writes put 0.14 of their excess over the median write in the persists and 0.24 in the
loop's queues, so neither crosses half, and the rule's numbers select its third branch, "the machine's
pauses, the generator's lateness rising with it". The generator's lateness holds 0.03. The excess is
in the leader's hand-offs: client-to-leader 0.26 (the leader's client reader taking the line to the
loop), leader-to-send 0.22, leader-queue 0.15 (m:p15-01-trace2-s10-r3-local-3125-1,
m:p15-03-s10-r1-local-3125-1 to -r4). **No branch fits, so the rule is reported as wrong, not read
through its least-bad branch.** It named two places an excess could concentrate and called everything
else the machine. It should have said: (iii) only if the generator's lateness holds more than half;
(iv) otherwise the excess is in the hosts' hand-offs, named by its largest segments, which are the next
subject; and each branch's reading checked against the segments it names before the branch is
reported. The four traced runs beyond the threshold show the same composition, so the selection does
not decide it. With the remedy the composition is the same (m:p15-05-s10-r1-local-3125-1 to -r4).
A rule fixed in advance can be wrong in its branches as well as in its threshold, and the tell is an
answer that satisfies a branch while contradicting its description (the reviewer; a finding).

## Fourth: the capacity, and an operating point that no longer moves (P15-02)

With the generator separate, the highest rate sustained in every run is 9,375 writes a second; at
12,500 every run completes the offered rate with its 99th percentile at 403 to 674 ms. The operating
point is pinned at 3,125, absolute: a point tied to capacity moves under the remedy it judges.

| Generator separate, untraced | 6,250 | 7,500 | 9,375 | 12,500 | records |
|---|---|---|---|---|---|
| median | 2,359 µs | 2,498 µs | 2,575 µs | 12,954 µs | m:p15-02-cap-local-6250-1 to -5 (and each rate's) |
| 99th percentile | 14.7 ms | 19.7 ms | 15.1 ms | 482.5 ms | m:p15-02-cap-local-6250-1 to -5 (and each rate's) |
| the leader's loop, busy | 0.972 | 0.967 | 0.955 | 0.897 | m:p15-02-cap-local-6250-1 to -5 (and each rate's) |
| inputs per batch | 3.63 | 4.59 | 5.78 | 9.45 | m:p15-02-cap-local-6250-1 to -5 (and each rate's) |

Past the capacity the loop is less busy than at it, and the generator's lateness (5.6 to 25 ms) is
far below the latency: what limits the rate there is not named. The pipeline's one append in flight
per follower is the candidate, the second mechanism above.

## The result of the phase

### The spec (P15-00)

`2cc1fd9` gives §11's phase-15 row its done criterion and changes no other section;
`docs/design/node-interface.md` is unchanged through the phase, both parts of the remedy being within
§4 as written.

### The instrument (P15-01)

`bench cluster` and `bench load --cluster`; `IClusterProbe` with `LocalProbe` and `ControlProbe`; a
process told apart by a token each draws (S-proc-1: a pid is 1 in every container, so the first
separate runs read as one process and were discarded); the hosts' counters differenced over the run
(S-proc-2); the sampled trace (S-proc-3) and its second form (`e195d2e`, S-proc-4, S-proc-5).

### The capacity and the operating point (P15-02)

Declared in `docs/design/performance-target.md`, its last section.

### The mechanism and the remedy (P15-03, P15-05)

`OutputClasses` (the classes, the emission stamps, the counts), the decomposition's split, the
records' `held_*` and `split_*` fields (S-held-1, S-held-2). The executor's early release and the
loop's `ClientRequestsLast` (S-early-1 to S-early-4; S-group-2 and S-hostdisk-1 still caught).

### The tail (P15-04)

Above, under Third.

### Class reach (P15-06)

The broad-run observer is retired (S-broad-1 retired with its test); `ci/broad-targets.txt` is a
`decision` line, a judgement of breadth across classes, which no run shows since P11-09; class reach,
a test red beside at least half of its class's other entries and at least four, is observed by
build-collect as `class-reach` (S-reach-1), its targets needing a reason or a control (S-reach-2). On
its first run it found P14-05's three tests and no others (run:38795000252).

### The margin audit (P15-07)

`scripts/margins.sh` at the head after the remedy (`26362fb`, whose host is `c7618d2`'s), in the three
modes, five runs a side (`docs/phases/P15/margins-bench.tsv`, now the script's default): every row keeps
the audit's 1.5 on both sides, the tightest the cost backlog at 32 in flight (1.62 unpatched) and the
group commit's control (2.00 patched), and none of the eighteen rows is stale against its control's
sources. The group commit's control moved from 0.0672 to 0.0607 syncs a write with the remedy. No row
was added: this phase's new checks are exact, not timed. **P14-06's observer caught my own change:** a
first audit gave the same verdict, and then I changed the script's default output path; the script is
among every row's sources, so all eighteen rows read as stale, and the audit was run again rather than
relabelled.

### The phase's cost in CI (P15-08)

Both of the phase's runs were green on their first attempt, every job given a runner:

| | Phase 14's report run | The first push | The second push | runs |
|---|---|---|---|---|
| jobs (all with steps, none failed) | 61 | 139 | 100 | run:38542098523, run:38795000252, run:38811415917 |
| slowest head shard's harness step | 368 s | 344 s | 374 s | run:38542098523, run:38795000252, run:38811415917 |
| soak, soak-membership steps | 800 s, 884 s | 775 s, 797 s | 780 s, 771 s | run:38542098523, run:38795000252, run:38811415917 |
| Compose job | 111 s | 103 s | 105 s | run:38542098523, run:38795000252, run:38811415917 |

The soaks' effect counts are phase 14's line for line on both runs: Core and the simulator are
unchanged. The manifest goes from 344 entries to 356: thirteen added (S-proc-1 to S-proc-5, S-held-1
and S-held-2, S-early-1 to S-early-4, S-reach-1 and S-reach-2), S-broad-1 retired.

## Decisions for you

1. **The median's remaining gap at 3,125** (a row): the loop's queues, a write or an answer waiting
   for the batch in its sync. The candidates are overlapping the next batch's handling with the sync
   (what phase 13's effect thread did, reverted by P14-01 for not raising capacity, which was then the
   question), or accepting that one sync at a time on the loop puts the criterion out of reach at this
   rate. Which, if either, is phase 16's? **Decided:** neither is built; the criterion is out of reach
   with one sync at a time, a measured limit stated in spec §8.
2. **The tail at 3,125** (a row; *superseded at phase 16:* "in the leader's hand-offs" was largely the
   trace's cost, see the third section): in the leader's hand-offs, client-to-leader the largest. P14-04's
   rule is reported as wrong; the rule it should have been is above. Is that the rule for the next
   reading, or do you want the hand-offs measured apart first? **Decided:** the hand-offs measured apart
   first, then the corrected rule (phase 16).
3. **The rate's limit past 9,375** (a row): not the loop, not the generator; the replication pipeline
   is the candidate, and widening it reverses P11-02 and must keep P11-01's check holding. **Decided:**
   not phase 16's subject; recorded, left open and unhomed.
4. **The 1.5-times gate per run:** the done criterion reads it per traced run, so the tail was read
   from six runs of ten. The runs beyond it show the same composition here, but a per-run filter can
   select calm runs. Keep it per run, or require the set's aggregate within 1.5 times? **Decided:** per
   run.

## Deviations and choices

- **Processes told apart by a drawn token, not a pid** (P15-01): the task named process ids; inside a
  container every process is pid 1.
- **The hosts' counters through the cluster's control port** (`Counters|`), not `Stats|` on the client
  port: the counters are the cluster's, and its control port already serves `Trace|`.
- **Decision 3's contingency built twice** (P15-01), the second after measuring the first's floor.
- **The remedy's rule read as differences of means** (P15-03), fixed in the breakdown's header before
  the runs; the medians' growth reported beside.
- **S-held-1's guard** is the stamp's time in a unit test and the split on a hand-built trace, not a
  recording file system with a planted sync delay.
- **S-reach-2 added** (P15-06): the class-reach list became a file of its own, and the loader's rule
  had to read it.
- **Nineteen records relabelled** from `cfa4af0` to `e195d2e`, the same tree reworded to place its
  `Task:` trailer where the trailer gate reads it; the measurements gate's own remedy.
- **Three disturbed runs kept and read as nothing** (P15-01's second floor: elections and medians of
  seconds on a machine that had restarted).
- **No push while a measurement ran.** The local CI run's load would have been part of the curve it
  overlapped: the same reasoning that moved the generator out of the hosts' process and cut the
  trace's cost, a third time this phase. The measurement and the verification compete for this
  machine, so they cannot both be running; that is a property of measuring here at all (the
  reviewer), and a finding.
- **An unexplained harness result, kept:** in the local touched-file run before the remedy's push,
  S-fsync-1 reported `target-not-run` (its class's run returned no result for its target). Run alone it
  was caught for its reason, and the whole touched set again caught all twenty. The harness keeps no
  output of an entry's run when the target is missing, so the cause cannot be read: a register row,
  not a flake.

## Done criteria

- **The load generator in a process of its own for in-process clusters, and its effect on the tail
  measured; a tail read by P14-04's rule only from traced runs within 1.5 times the untraced at the
  same rate:** P15-01, P15-04; met, the rule reported as wrong where its branches did not fit.
- **The group commit's capacity measured, the operating point pinned at 3,125 writes a second,
  absolute, with its reason:** P15-02; met.
- **The queue's mechanism checked before any remedy: each output's wait split at its emission, every
  held output classed, the hypothesis a check shown failing on phase 14's code:** P15-03; met.
- **A remedy built only if the B and C holds account for half of the growth, a miss read by the rule
  stated before measuring:** P15-03; met (0.73, built).
- **The curve measured again, interleaved against phase 14's head: the criteria met, or the gap
  attributed segment by segment beside the floors:** P15-05; not met, attributed: the median by the
  loop's queues (the batch in its sync), the tail by the hosts' hand-offs.
- **The median above the reverted host's at 1,250 and 2,000 explained or recorded as a second thing:**
  P15-03, P15-05; explained by the same holds, and gone with the remedy.
- **The broad-run observer retired, the list a judgement, class reach checked under its own name:**
  P15-06; met.
- **Every bench control's margins measured again after the host's changes:** P15-07; met.
- **Every row promised to phase 15 closed:** five closed (one dropped by the spec's pinned operating
  point); of the four opened, one dropped at acceptance by spec §8, two homed to phase 16, one open
  and unhomed.

## Predictions

**Evidence 10, forcing 0.** Right 4, partly 5, wrong 1 (P15-09 added at acceptance).
Each outcome is in the breakdown.

- **P15-00, right.**
- **P15-01, partly:** the untraced figures and the traced generator's lateness as predicted; no trace
  within 1.5 times the untraced tail on aggregate, after two sampled traces.
- **P15-02, partly:** the capacity where predicted (9,375); past it the loop is not the limit.
- **P15-03, partly:** the check failed and the rule admitted the remedy, the gap explained by the same
  holds; more of the held responses needless than predicted, and B held about two syncs, not one.
- **P15-04, wrong:** the threshold held for six runs of ten, and the rule written in advance had no
  branch that fits: the excess is in the hosts' hand-offs.
- **P15-05, partly:** the holds gone and the median down by the predicted amount, the regression at
  1,250 and 2,000 gone; the 99th percentile not down, and the median at 1,250 past the reverted host's.
- **P15-06, right.**
- **P15-07, right.**
- **P15-08, partly:** no shard past 400 s, the soaks identical, every job given a runner; thirteen
  entries added, not nine.
- **P15-09, right** (added at acceptance): the home check alone changed; one unhomed row; the gate passes.

## What exists

- **P15-01:** `bench cluster`, `bench load --cluster`, `IClusterProbe`, `LocalProbe`, `ControlProbe`,
  `ClusterControl`, `HostCounters`, `ProcessToken`, `HandOffTrace`'s sample (`Take`, the held client
  stamps, the in-place header reads), `--trace-sample`, the records' `generator_own_process` and
  `trace_sample`; `ClusterProbeTests`, `SeparateGeneratorTests`, the sampled decomposition test;
  S-proc-1 to S-proc-5.
- **P15-02:** `docs/design/performance-target.md`'s last section.
- **P15-03:** `OutputClasses`, `HandOffTrace.Kind.Emitted*`, `AddAt`, `Decomposition.Result.Splits`,
  `NodeHost.HeldOutputs`, the records' `held_*` and `split_*`; `OutputClassesTests`, the split test;
  S-held-1, S-held-2.
- **P15-05:** `DiskExecutor.ExecuteBatch`'s early release, `NodeHost.ClientRequestsLast`; the check
  `AnOutputNoPersistPrecedesInItsBatchLeavesBeforeTheBatchsSync`,
  `OnlyTheOutputsBeforeABatchsFirstPersistLeaveBeforeItsSyncs`, `BatchOrderTests`; S-early-1 to
  S-early-4.
- **P15-06:** `ci/class-reach.txt`, the observer `class-reach`, `BuildCollect.CheckReach`,
  `SabotageSpec.ClassReachFile`; `BuildCollectReachTests`, `AClassReachTargetNeedsAReasonOrAControl`;
  S-reach-1, S-reach-2; S-broad-1 retired.
- **P15-07:** `docs/phases/P15/margins-bench.tsv`.
- **P15-09** (at acceptance): `Register.CheckHome`, `Register.Unhomed`; `RegisterHomeTests`; S-reg-8.

## Register

Five rows were promised to phase 15. The median at 3,125 (closed by P15-03 and P15-05), the tail
(P15-01 and P15-04), the median above the reverted host's (P15-03 and P15-05) and the broad-target
rule (P15-06) are closed; the operating point is dropped by the spec's phase-15 row (`2cc1fd9`).
Four rows were opened. At acceptance: the median's remaining gap (P15-05) is dropped by spec §8's
measured limit (`87197ba`); the tail in the hosts' hand-offs (P15-04) and the harness's per-entry output
(reworded from S-fsync-1's unexplained `target-not-run`) are homed to phase 16; the rate's limit past
capacity (P15-02) is open and unhomed, not to be pursued by reversing P11-02.

## Findings added this phase

In `docs/findings.md`, under phase 15: the second mechanism measured and smaller; a pid across
namespaces; an instrument's cost model itself a measurement (the reviewer, at acceptance); a currency
observer firing on its author's edit (the reviewer, at acceptance); a rule fixed in advance wrong in its
branches, the tell an answer that satisfies a branch while contradicting its description (the
reviewer); a busy loop under batching; the measurement and the verification competing for the
machine (the reviewer); a reworded commit's records.

## Still the person's

- The walk, cold, once per phase (phase 0's row): not scripted; not done by me.
- Branch protection's required checks, as before.

## Commits never verified in CI

None. Each commit of the first two pushes ran in its own per-commit job, all green
(run:38795000252, run:38811415917); the commits after `429c5b2` run in this report's run.
