# Phase 14 — report

**Status: for review.**

The group commit, against restated criteria (spec §11 phase 14, P14-00's row): P14-00 to P14-08 as
approved, with the amendments each outcome records. Every number in this report's tables cites its
record (`m:`) or its CI run (`run:`). This report certifies the commit that contains it; `gates
reports` checks that commit's CI run on the next push. **It is pushed after a first push of the
phase's work** (`3dd776c`, run:38527995236), whose run found two faults, each fixed in its own commit
here: the per-commit matrix counted retired entries (P14-01's mechanism missed a reader), and P14-05's
observer found three unlisted tests on its first reading (under P14-05 and deviations).

## First: the group commit moved the cap, not the latency at the operating point (P14-03, P14-04)

The followers already grouped their commits (0.38 syncs a write at 3,125 writes a second, 0.13 at
4,000) while the leader synced once a write at every rate, and moving its serial work off its loop
had not reduced it (phase 13). So the leader's loop now takes every input already queued, up to 64,
and executes their effect lists as one batch with one sync per file (P14-03). Interleaved in process
against the reverted host (`a00e4df`), five runs a side:

| | The reverted host | The group commit | Records |
|---|---|---|---|
| Median at 3,125 writes a second | 4,119 µs (3,302 to 6,670) | 1,973 µs (1,724 to 2,130) | m:p14-04-ab-base-3125-1 to -5, m:p14-04-ab-fix-3125-1 to -5 |
| 99th percentile at 3,125 | 52.9 ms (30.8 to 65.3) | 9.2 ms (8.9 to 14.2) | m:p14-04-ab-base-3125-1 to -5, m:p14-04-ab-fix-3125-1 to -5 |
| The restated criteria at 3,125 (1,198 µs; 5,051 µs) | met in no run | met in no run | m:p14-04-ab-base-3125-1 to -5, m:p14-04-ab-fix-3125-1 to -5 |
| The leader's syncs per write at 3,125 | 1.00 | 0.77 (0.73 to 0.82) | m:p14-04-ab-base-3125-1 to -5, m:p14-04-ab-fix-3125-1 to -5 |
| The leader's loop busy at 3,125 | 0.98 | 0.96 | m:p14-04-ab-base-3125-1 to -5, m:p14-04-ab-fix-3125-1 to -5 |
| The leader's execution per write at 3,125 | 303 µs | 290 µs | m:p14-04-ab-base-3125-1 to -5, m:p14-04-ab-fix-3125-1 to -5 |
| Highest rate sustained in every run | 3,125 (3,500 in none) | 6,250, the curve's highest | m:p14-04-ab-base-3500-1 to -5, m:p14-04-ab-fix-6250-1 to -5 |
| Median at 6,250 | 5.08 s (collapsed) | 2,314 µs, 0.33 syncs a write | m:p14-04-ab-base-6250-1 to -5, m:p14-04-ab-fix-6250-1 to -5 |

**The capacity moved; the latency at 3,125 halved but stays near 2 ms.** With no timer, the loop
batches only what has queued while it was busy, so it runs saturated (0.95 to 0.98 busy at every
rate from 3,125 to 6,250) and its batches grow with the queue: 1.9 inputs at 3,125, 3.8 at 6,250,
each sync slower than one write's (274 to 313 µs against 250 to 256). The median stays between 2.0
and 2.3 ms from 3,125 to 6,250 while syncs per write fall from 0.77 to 0.33; the reverted host
collapses past 3,125. At 3,125 the serial work per write fell 4%; I had predicted the loop 0.55 to
0.80 busy there. Below
the knee the group commit costs nothing at 625 and 100 (batches of one). It costs the median 10 to
17% at 1,250 and 2,000 while lowering the 99th percentile there; that is measured, not explained.

**Both criteria missed, each read by the rule stated before the measurement** (P14-04):
- **The tail, (iii): the machine's pauses.** In each traced run at 3,125 the writes over 5,051 µs
  put 0.05 to 0.09 of their excess over the median write in the persists and 0.07 to 0.10 in the
  loop's queues; the generator's lateness held 0.54 to 0.65 and the connection wait 0.11 to 0.20
  (m:p14-04-traced-local-3125-1 to -5). I expected (i), the disk's own tail. **But the rule read the
  trace's tail.** Traced, the 99th percentile was 41.6 to 51.7 ms and the generator's lateness at
  its 99th percentile 31 to 40 ms; untraced, 8.9 to 14.2 ms and 0.9 to 2.9 ms. The generator shares
  the process with the traced hosts. The untraced tail, the one the criterion is about, is not
  decomposed by this instrument (a finding; a decision below).
- **The median, (ii): the queue at the leader.** The traced segment medians at 3,125 less those at
  1,250, the floor's rate, come to 723 µs: the loop's queues 0.65 (commit-respond +266 µs,
  leader-queue-answer +111, leader-queue +95), leader-to-send +265 (0.31 with the rest), the persists
  0.04 (m:p14-04-traced-local-1250-1 to -5, m:p14-04-traced-local-3125-1 to -5). Commit-respond and
  leader-to-send each grew by about one leader sync. That is what a batch holding every output until
  its one sync adds to an output whose own list had no persist. The records fit that reading; they
  do not test it.

## Second: two premises closed by mechanism, and what the first reading of one found (P14-05, P14-06)

The registry's first run (P13-01) found two inputs nothing checked; each now has an observer and a
currency line in `ci/inputs.txt`, and both register rows are closed.

- **The broad targets against the harness's own red neighbours (P14-05).** Each head shard's notice
  names the tests red beside an entry's target and how many entries turned each red. build-collect
  sums them and fails a test red beside at least half of the entries that ran it, and at least four,
  that `ci/broad-targets.txt` does not list. **Changed before its first run:** GitHub keeps 4,096
  characters of an annotation's message (run:38517402423's whole-run entry times, P13-01's, is that
  long exactly; the log holds the rest), and a notice listing every test that ran would pass it.
  A list of reds alone would lose the runs of a test red in no entry of a shard. So the entries that
  ran a test are the manifest's (an entry runs its target's whole class, P11-09), the reds go in
  numbered parts under the cap, and a shard missing a part or with a part at the cap fails.
  **Its first reading** (run:38527995236) **found none of the twelve listed tests and three focused
  ones instead,** each red beside exactly half of a small sample: 2 of 4, 3 of 6, 2 of 4. Seven listed
  tests ran beside fewer than four entries, and the five that ran beside four or more were red beside
  under half (the coverage aggregates 3 of 10 and 3 of 11). Since P11-09 a test is seen only beside its own
  class's entries, so the rule measures how many of them reach it, not breadth. The three are listed,
  as the rule asks (`a2b074a`), and the two entries targeting them now state their reasons (a decision
  below).
- **The margins tables tied to the commits they measured (P14-06).** Each row names its measurement
  commit (the header's, or a `# at <commit>: <checks>` line), and `gates margins --currency` fails a
  phase report when a row's control's sources changed after it. Applied to phase 13's report at
  `0abc3e4`, it passes the three hand-off rows (measured at `7f4b02c`) and fails the other eight
  bench rows (measured at `3a1056b`, before `7f4b02c` changed the host): phase 13's report would have
  needed its audit again. Applied to this phase before P14-07 it noted all eleven bench rows stale
  (the host changed at `3c07607`); after P14-07, none of eighteen.

## The result of the phase

### The spec (P14-00)

§11's phase-14 row leads with the followers' batching and has its done criterion; §2's paragraph
leads the same way; §4 says the host runs each effect list on its loop, its syncs inline, and from
P14-03 a batch of lists with one sync. Node-interface §4 needed no change, and none of
`src/Raft.Core`, `src/Raft.Simulation` or `src/Raft.Checker` changed through the phase.

### The revert, and a guarded retirement (P14-01)

The effect thread is gone: the loop executes each effect list again, as at `d1e6d43`, keeping
P13-02's role log, P13-03's loop timers, the election rule's tick allowance and the hand-off control's
slept delay. S-loop-1 to S-loop-3 test code that no longer exists, so they are retired by a mechanism:
`retired: <commit> <reason>` in an entry's `sabotage.txt`, read by every gate that walks the
manifest; only a task with a final outcome may name a retired entry; and retiring an entry whose
target method still exists fails (S-retire-1). **One reader was missed:** the per-commit matrix
reads each commit's manifest from git and counted retired entries, so three commits got thirteen
shards where their own harness makes twelve (run:38527995236). Fixed at `fa59fcd` (S-retire-2).

Interleaved against phase 13's head (`0abc3e4`), five runs a side (m:p14-01-ab-base-625-1 to -5,
m:p14-01-ab-fix-625-1 to -5, the same at 1,250 and 3,125): below the knee the reverted host is
faster at every rate (625: 1,155 against 1,279 µs; 1,250: 949 against 1,237), one hand-off fewer. At 3,125 both hosts went past capacity on
that day (3,278 against 2,745 µs at the median), and the highest rate sustained in every run was
2,000 on both. The day's syncs were slower than phase 13's (249 µs at 3,125 against 234).

### The criteria restated from floors measured end to end (P14-02)

From P14-01's reverted-side records, before P14-03's first commit (`docs/design/performance-target.md`):

| Figure | Value | Records |
|---|---|---|
| The median's floor (the lowest rate's median of five, at 1,250) | 949 µs (879 to 1,186) | m:p14-01-ab-fix-1250-1 to -5 |
| The tail's floor (at 100) | 4,802 µs (4,043 to 5,258) | m:p14-01-ab-fix-100-1 to -5 |
| The allowance: one leader sync at 3,125 | 249 µs (248 to 255) | m:p14-01-ab-fix-3125-1 to -5 |
| **The median criterion at 3,125** | **1,198 µs** | m:p14-01-ab-fix-1250-1 to -5, m:p14-01-ab-fix-3125-1 to -5 |
| **The 99th-percentile criterion at 3,125** | **5,051 µs** | m:p14-01-ab-fix-100-1 to -5, m:p14-01-ab-fix-3125-1 to -5 |

### The group commit (P14-03)

`DiskExecutor.ExecuteBatch` first writes the persists in emitted order, without a sync. It then syncs
each file once, in the order it was first written, and the directory after a create. Then every send,
response and event goes out in emitted order. A rename, delete or truncate is a boundary: the writes
before it are synced first, and it keeps its own syncs. A failed write or sync throws before anything
held is released. A batch of one list runs as before. The control holds the leader at 0.070 syncs a
write under 64 closed-loop clients here, 0.061 to 0.075 over the audit's fifteen runs, against 1.00
with S-group-1. The soaks are identical to the count (27,910 elections, 1,335,746 entries committed,
the same ten hardest histories; the membership soak's 828,867 entries and ten hardest the same), and
the Compose run with a killed leader and the checker is green (run:38527995236).

| Sabotage | What it breaks | Caught by | Run |
|---|---|---|---|
| S-group-1 | the loop's batch limited to one input | the control: 1.00 syncs a write against the bound 0.5 | run:38527995236 |
| S-group-2 | outputs released before the batch's sync | the recording file system: a send with two files unsynced | run:38527995236 |
| S-group-3 | files synced in reverse first-write order | the recorded order: entries.log synced before term.log | run:38527995236 |
| S-group-4 | responses released after a failed sync | no response after the failure | run:38527995236 |
| S-group-5 | the flush before a rename removed | the rename before the earlier write's sync | run:38527995236 |

### The curve (P14-04)

Interleaved in process against the reverted host, untraced, five runs a side; each cell the median of
five (sustained: completed within 5% of offered, the 99th percentile under 100 ms).

| Rate | Reverted host: median; 99th percentile; sustained | Group commit: median; 99th percentile; sustained | Group commit: leader syncs a write; loop busy; inputs a batch | Records |
|---|---|---|---|---|
| 100 | 1,673 µs; 4,300 µs; 5 of 5 | 1,710 µs; 4,069 µs; 5 of 5 | 1.00; 0.08; 1.08 | m:p14-04-ab-base-100-1 to -5, m:p14-04-ab-fix-100-1 to -5 |
| 625 | 1,189 µs; 5,658 µs; 5 of 5 | 1,202 µs; 5,321 µs; 5 of 5 | 0.99; 0.33; 1.13 | m:p14-04-ab-base-625-1 to -5, m:p14-04-ab-fix-625-1 to -5 |
| 1,250 | 1,113 µs; 9,617 µs; 5 of 5 | 1,230 µs; 6,245 µs; 5 of 5 | 0.98; 0.53; 1.31 | m:p14-04-ab-base-1250-1 to -5, m:p14-04-ab-fix-1250-1 to -5 |
| 2,000 | 1,101 µs; 14.4 ms; 5 of 5 | 1,292 µs; 8,959 µs; 5 of 5 | 0.95; 0.82; 1.37 | m:p14-04-ab-base-2000-1 to -5, m:p14-04-ab-fix-2000-1 to -5 |
| 3,125 | 4,119 µs; 52.9 ms; 5 of 5 | 1,973 µs; 9,239 µs; 5 of 5 | 0.77; 0.96; 1.88 | m:p14-04-ab-base-3125-1 to -5, m:p14-04-ab-fix-3125-1 to -5 |
| 3,500 | 97.6 ms; 420.8 ms; 0 of 5 | 2,089 µs; 11.2 ms; 5 of 5 | 0.67; 0.98; 2.15 | m:p14-04-ab-base-3500-1 to -5, m:p14-04-ab-fix-3500-1 to -5 |
| 4,000 | 1.19 s; 1.42 s; 0 of 5 | 2,127 µs; 10.9 ms; 4 of 5 | 0.59; 0.98; 2.42 | m:p14-04-ab-base-4000-1 to -5, m:p14-04-ab-fix-4000-1 to -5 |
| 5,000 | 2.98 s; 4.04 s; 0 of 5 | 2,210 µs; 11.9 ms; 5 of 5 | 0.45; 0.98; 2.97 | m:p14-04-ab-base-5000-1 to -5, m:p14-04-ab-fix-5000-1 to -5 |
| 6,250 | 5.08 s; 7.17 s; 0 of 5 | 2,314 µs; 13.0 ms; 5 of 5 | 0.33; 0.98; 3.84 | m:p14-04-ab-base-6250-1 to -5, m:p14-04-ab-fix-6250-1 to -5 |

The one run at 4,000 that missed had a single stall (its 99th percentile 618 ms, completed within
0.1% of offered). The followers sync 0.29 times a write at 3,125 against phase 13's 0.38; the
reverted host's followers synced 0.17 there, its leader past capacity sending fewer, larger appends.

### The broad targets (P14-05)

| Listed test (12) | Entries that ran it | Red beside | Judged | Run |
|---|---|---|---|---|
| The two coverage aggregates | 10, 11 | 3, 3 | yes: under half | run:38527995236 |
| The WGL classifier; the ambient allowlist | 5, 5 | 1, 1 | yes: under half | run:38527995236 |
| The removed-servers aggregate | 4 | 1 | yes: under half | run:38527995236 |
| The other seven | 0 to 3 | 0 to 2 | no: fewer than four | run:38527995236 |

Every shard's notice fit one part, the largest 3,017 characters; 148 tests were red beside some
entry in the run (run:38527995236).

### The margins' commits (P14-06) and the audit (P14-07)

The audit at `3dd776c` (`docs/phases/P14/margins-bench.tsv`): fifteen runs a side for the host's
bench controls, five alone, five beside three busy processes, five beside the Membership and Scale
suites; the replication cost check, seeded, one run a side. `gates margins` passes every row with
the audit's 1.5 on both sides, the closest the 32-in-flight backlog guard (1.62) and the barrier per
list's floor (1.71), as in phases 12 and 13. The hand-off control's other segments stay at 0.025 to
0.055 of the delay unpatched, the effect threads' coupling gone. The group commit's row
(`group-syncs`) keeps 6.68 unpatched and 2.00 with S-group-1. `gates margins --currency` finds none
of the eighteen rows stale.

### The phase's cost in CI (P14-08)

Measured on the phase's one pushed run before this report, `3dd776c` (run:38527995236), every job a
first attempt, against phase 13's last run, `381085d` (run:38517402423). This report's own push is
not in them.

| | Phase 14 | Phase 13's last run | Runs |
|---|---|---|---|
| Harness shards, slowest and fastest step | 367 and 80 s (13 shards, 343 entries) | 341 and 87 s (13 shards, 338) | run:38527995236, run:38517402423 |
| Gates step | 5 s | 5 s | run:38527995236, run:38517402423 |
| Tests step | 331 s | 399 s | run:38527995236, run:38517402423 |
| Baseline soak step (the soak's own time) | 794 s (793) | 797 s (796) | run:38527995236, run:38517402423 |
| Membership soak step (the soak's own time) | 906 s (905) | 473 s (471) | run:38527995236, run:38517402423 |
| Compose step | 98 s | 92 s | run:38527995236, run:38517402423 |
| Per-commit jobs (commits by shards), all given a runner | 130 (10 by 13) | 26 (2 by 13) | run:38527995236, run:38517402423 |
| Longest per-commit check step | 706 s | 689 s | run:38527995236, run:38517402423 |

- **Every job was given a runner and ran its steps.**
- **Eight entries were added in that run and three retired** (S-retire-1, S-group-1 to S-group-5,
  S-broad-1, S-margin-2; S-loop-1 to S-loop-3), a net five; S-retire-2 makes nine and six.
- **The membership soak's own time was 905 s against 471** on identical work (the same 828,867 entries and
  ten hardest histories), the highest yet; its runner spread is now 1.92.
- **39 per-commit jobs failed**, every shard of `a00e4df`, `f28f4ac` and `de6d93d`, each with "the
  manifest has 336 harness entries, which is 12 shard(s) of 28, not 13": the matrix counted the
  retired entries. The seven other commits passed on every shard. **The head's build job failed** on
  P14-05's three unlisted tests; every other head job passed.
- **The local run before the push** took about 35 minutes: the report rule, preflight, build, gates
  and tests (about 8), the host sabotages with the secret scan and the README walk (about 20), and
  the touched entries (41 in 293 s).

## Decisions for you

1. **What the median's rule names next.** By the rule stated before the measurement, the median's
   gap at 3,125 is the queue at the leader, commit-respond and leader-to-send its largest parts, each
   about one leader sync. One reading, untested: a batch holds every output until its one sync, so a
   response whose own list had no persist waits for a later list's sync. Releasing the outputs that
   precede the batch's first persist at once is within node-interface §4; outputs after it must wait.
   Whether that is phase 15's subject, measured before it is built, is yours (a register row).
2. **The tail's instrument.** The tail rule read the trace's own tail. By the rule, (iii) proposes no
   remedy; but what the untraced tail is made of is not known. A generator in a process of its own,
   or a trace that keeps a sample, would let the rule read the tail the criterion is about (a
   register row).
3. **The broad-target rule.** Under class-scoped runs it cannot see breadth; it found three focused
   tests at exactly half of small samples. I listed them, as the rule asks, which made two entries
   state their reasons, a strengthening. Whether the rule should stay (its threshold, its minimum of
   four), change, or give way to something that does see breadth, such as a periodic whole-project
   run of the broad candidates, is yours.
4. **The operating point.** The group commit moved the sustained rate from 3,125 to beyond 6,250 and
   left the median at 3,125 near 2 ms; both criteria are missed there. The operating point (half of
   C_design) is a declared convention (P13-08), yours to keep or move.
5. **The cost at 1,250 and 2,000:** the group commit's median is 10 to 17% above the reverted host's
   there, its 99th percentile lower. Unexplained; no row, unless you want one.

## Deviations and choices

- **P14-05's transport changed before its first run** (above): the entries that ran a test come
  from the manifest, not the notice, because of GitHub's 4,096-character cap. The rule and the policy
  are the amended design's; the counting's source is not. Its tests cover the parts, the cap and a
  theory's cases counted once.
- **P14-05's first finding was acted on as the rule says** (`a2b074a`): three lines in
  `ci/broad-targets.txt`, and `reason:` lines for S-hist-7 ("the oracle rejects the twin") and
  S-member-2 ("Not Leader"), each caught for it on the committed tree.
- **S-retire-2 was added to P14-01 after its outcome was written**, for the reader the retirement
  missed; the outcome is unchanged and the sabotage column names both.
- **The tail rule's code has a test and no sabotage**; the breakdown named none for P14-04. The rule
  decides an outcome, so it is tested on hand-made segments whose shares are known.
- **A traced run at 1,250 was added to P14-04** (planned: 625 and 3,125), so the median's rule could
  read the segments against the floor's own rate.
- **P14-06's premise was checked at `0abc3e4` with the hand-off rows' override in the form P14-06
  reads** (`# at 7f4b02c: …`); at `0abc3e4` the line was prose, which the observer reads as no
  override, failing all eleven bench rows.
- **The Docker daemon stopped once** between the audit and the next gate run, and was restarted;
  nothing was lost.

## Done criteria

- **The effect thread reverted, the host running each effect list on its loop (§4), the three
  entries retired by a recorded mechanism that refuses an entry whose target still exists, the
  reverted host measured against phase 13's head:** P14-01; met, with the missed reader fixed.
- **Both latency criteria restated from floors measured end to end on the reverted host after the
  revert, each plus one declared allowance:** P14-02; met.
- **A group commit in the host, Core and the simulator unchanged, the persist barrier, the persists'
  order, the stop on a failed write or sync, and every invariant, soak, Compose and checker result
  unchanged:** P14-03; met (run:38527995236).
- **The curve measured again, interleaved against the reverted host in process: the criteria at
  3,125 met, or the gap attributed segment by segment beside the floors, a missed tail read by the
  rule stated before the measurement:** P14-04; not met, attributed: the median by the rule's (ii),
  the tail by its (iii), with the caveat that the rule read the traced tail.
- **Compose as a wiring and integration check, its comparisons to 2,000 writes a second:** the
  Compose job green with a killed leader and the checker (run:38527995236); no Compose comparison
  was taken this phase.
- **Every bench control's margins measured again after the host's changes:** P14-07; met.
- **Every row promised to phase 14 closed:** the serial work per write (P14-03, P14-04), the broad
  targets (P14-05), the margins tables (P14-06) and the target's figures (P14-02), each closed.
  Two rows are opened by P14-04's rules; their homes are yours.

## Predictions

**Evidence 9, forcing 0.** Right 4, partly 4, wrong 1.
Each outcome is in the breakdown.

- **P14-00, right.**
- **P14-01, partly:** below the knee as predicted; the knee moved below 3,125 for both hosts that day.
- **P14-02, partly:** the median's floor, allowance and criterion in range; the tail's floor and
  criterion above their predicted ranges.
- **P14-03, right.**
- **P14-04, partly:** the tail missed and 625 unchanged, as predicted; the median, the syncs per
  write and the loop's busy fraction wrong; the sustained rate past the predicted; the tail rule
  read (iii), not (i).
- **P14-05, wrong:** none of the twelve listed found, and the three found are focused tests, not
  aggregates.
- **P14-06, right.**
- **P14-07, right.**
- **P14-08, partly:** the shards under 400 s and every job given a runner; the entry count right only
  by S-retire-2; the membership soak outside its earlier spread.

## What exists

- **P14-01:** `retired:` in `sabotage.txt`, `SabotageSpec.LoadLive`, `SabotageSpec.TargetExists`, the
  breakdown gate's check of retired entries, the matrix's skip (`ShardPlan.AtCommit`); S-retire-1,
  S-retire-2.
- **P14-03:** `DiskExecutor.ExecuteBatch`, `NodeHost.BatchLimit`, `NodeHost.LoopBatches`, the
  records' `leader_loop_inputs_per_batch`; `GroupCommitTests`; four batch tests in
  `DiskExecutorTests`; S-group-1 to S-group-5.
- **P14-04:** `Decomposition.TailShares`, `--tail-over` and `RAFT_BENCH_TAIL_OVER`, the records'
  `tail_*` fields.
- **P14-05:** `BuildCollect.CheckBroad`, `RanBeside`, `NeighbourNotice`; `Sabotage.RedNeighbours`,
  `NeighbourNotices`; `Currency.Listed`, the observer broad-run; S-broad-1.
- **P14-06:** `Margins.RowCommits`, `CheckCurrency`, `NewestBenchTable`, `gates margins --currency`
  in `ci-gates.sh`, the observer margins-commit; S-margin-2.
- **P14-07:** the group commit's row in `scripts/margins.sh`; `docs/phases/P14/margins-bench.tsv`.

**Test projects** (at the start of phase 14 in brackets): Architecture 52 (52), Gates 226 (207),
Checker 173 (173), Simulation 110 (110), Core 852 (852), Scale 26 (26), Budget 3 (3), Membership 3
(3), Host.Tests 40 (38), Host.Slow.Tests 10 (9).

## Register

Closed: the leader's serial work per write (P14-03, P14-04), `ci/broad-targets.txt`'s premise
(P14-05), `docs/phases/P12/margins.tsv`'s premise (P14-06), and the target's figures (P14-02).
Opened: the median's miss at 3,125 as the rule's (ii) reads it, and the tail's miss with the
instrument that read it (P14-04). Their homes are yours at acceptance.

## Findings added this phase

In `docs/findings.md` under phase 14, in order:
- a group commit with no timer runs its loop saturated, so it moves the cap, not the latency at its
  edge;
- a rule stated before the measurement reads the instrument's tail if the instrument has one;
- a channel's capacity is a premise;
- a mechanism that changes what an entry is must reach every reader, including those reading
  history;
- under class-scoped runs, a breadth rule measures reach within a class;
- the first application of a currency check found its phase's predecessor stale.

## Still the person's

- The cold walk of the README (the P0 row).
- The decisions above: the median's next subject, the tail's instrument, the broad-target rule, the
  operating point.
- Adding `compose` to branch protection, as at phase 9.

## Commits never verified in CI

Four pushed commits of the phase never had a green verdict of their own, all in run:38527995236.
**All four failed; none never ran**: every (commit, shard) job was given a runner and ran its checks,
130 per-commit jobs and the head's thirteen shards, and the seven other commits passed on every
shard. Rewriting them would force-push, which `each-commit-list` refuses by design; as accepted at
phase 10 and since, unless you say otherwise. The causes, read from the jobs:

| Commit | Run | Result | Jobs | Cause |
|---|---|---|---|---|
| `a00e4df` | run:38527995236 | failed | all 13 shards | the matrix gave it 13 shards, its harness made 12 of its 336 live entries: `ShardPlan.AtCommit` counted the three retired entries; fixed at `fa59fcd` |
| `f28f4ac` | run:38527995236 | failed | all 13 shards | the same; documentation and records only |
| `de6d93d` | run:38527995236 | failed | all 13 shards | the same; documentation only |
| `3dd776c` | run:38527995236 | failed | the head's build job | build-collect: P14-05's observer found three unlisted tests on its first reading; listed at `a2b074a`. Every other head job, the soaks, Compose and the thirteen shards among them, passed |
