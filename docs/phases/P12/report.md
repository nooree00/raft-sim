# Phase 12 — report

**Status: for review.**

Commit latency, then the group commit by the corrected gate (spec §11 phase 12, P12-00's row):
P12-00 to P12-09, as approved, with the amendments each task's outcome records. Every number in this
report's tables cites its record (`m:`) or its CI run (`run:`), which `gates measurements` checks.
This report certifies the commit that contains it; `gates reports` checks that commit's CI run on the
next push. **It is pushed with one fix** that the phase's second push's run (run:37830104156)
found after that push: the cost checks compared a shard run on one worker with lines timed beside
three (under P12-08 and deviations).

## First: a gate defeated by its input, the class (P12-01)

The register gate learns which phases are complete from `docs/phases/status.md`, a file kept by
hand. The hand stopped at phase 7. For four phases the gate's check on open rows promised to
completed phases could not fire, and it hid a row of mine: the resends row, done by P11-02 and
left marked open through phase 11's pushes. The gate was correct; its premise was stale, and
nothing reported the staleness.

It is the fourth instance across this project and the CRDT one of a gate defeated by its input
rather than its logic: the test-count baseline, the sabotage cost file, the stale patches, and now
the status file. **The class: any gate that reads a file kept by hand needs a check that the file is
current, and the gate itself cannot be that check, because it reads the file as true.**

What P12-01 did about it:

- **The status file, both ways.** `gates register` now fails when a report says its phase is
  accepted and the status file does not mark it complete, and when the status file marks a phase
  complete and no accepted report exists. The second direction is how a phase gets closed without
  evidence. P0 is the one exception (its open row is the person's walk). S-status-1 and S-status-2
  prove each direction. Run on phase 11's tree before its acceptance, the first direction fails on
  P8, P9 and P10; the resends row could only be caught at acceptance, which is when it was.
- **The other hand-kept inputs, audited.** Of the three you named, the test baseline and the patch
  set are compared with what they describe on every run, both ways: current by construction. The
  cost file is checked only beyond a factor of 5 (and 30 s) per entry and 3 per shard, so a line
  wrong by less depends on someone refreshing it: a row. The audit found three more inputs that
  nothing checks, each a row: the touched stage's rate per entry, the Budget tests' hardest seeds,
  and a measurement record's commit (checked for presence, not for naming a commit on the branch).
  Four rows in all.

The last one mattered twice this phase. Rewording six unpushed commits orphaned 150 records, and
amending one orphaned 40 (both under deviations). Each time I found it myself, not a gate.

## The target, stated before measuring and then measured (P12-00, P12-06)

Before any measurement, `docs/design/performance-target.md` said the target might be unreachable in
principle. L = 2S + R counts the leader's sync, the follower's sync and one round trip between them.
It has no term for the client's own round trip or for any software. At 1.5 L, about 140 µs is left
for both.

Measured this phase, the floors along a commit's path, each timed alone on the day (P12-06):

| Floor | Median | Record |
|---|---|---|
| A sync alone (the container's file system; the volume) | 182 µs; 168 µs | m:p12-06-sync-tmp-1 to -5, m:p12-06-sync-volume-1 to -5 |
| A sync with two writers at once; three | 212 µs; 274 µs | m:p12-06-sync2-tmp-1 to -5, m:p12-06-sync3-tmp-1 to -5 |
| A one-way loopback hop to a reader asleep on its own thread | 83 µs | m:p12-06-hop-1 to -5 |
| The warm loopback round trip (half of it one way) | 50 µs | m:p12-06-rtt-1 to -5 |
| A hand-off to a loop asleep on a channel | 61 µs | m:p12-06-handoff-1 to -5 |

A cluster's nodes sync on one disk, two and three at once, and its hops wake threads that were
asleep. The floors along a commit's path, the client's two hops included, sum to between 524 µs
(every hop warm) and 939 µs (every hop and queue cold) on this machine. The median criterion is
543 µs. **The target is at the bottom of what the floors allow even at 625 writes a second**, with
nothing yet spent on the software. Whether the target is restated against the floors is yours (a
register row).

## The result of the phase

### Where a commit's latency went (P12-03)

The hosts and the generator stamp each hand-off of a write (bench mode only; nothing inside
`RaftNode`). The bench joins them by command and log index into thirteen segments (fourteen after
P12-04), in process from the hosts' traces and in Compose fetched from each node.

Every write joined in every traced run of the phase, with no causality violation and no mismatched
join.

The guard the breakdown approved could not fail. The segments are differences of consecutive
stamps, so they sum to the span by construction, whatever the stamps in between are. The guard is
now content instead: every stamp the join reaches by index must carry the write's own command
(S-lat-2, amended).

The control plants a delay in one hand-off, and it must appear in that segment and in no other
(S-lat-1).

The trace costs 2 to 7% at the median and two to four times at the 99th percentile (an interleaved
A/B). So the decomposition is read by its medians, and the curve is measured untraced.

At 625 writes a second, before any change (medians of five runs' medians): the generator's own
lateness was the largest segment, 1.46 ms of 3.14 in process and 1.55 ms of 3.80 in Compose.
**The headline prediction's first half held.** At 3,125 the largest were the leader's loop queues,
behind syncs that keep the loop 0.64 busy.

### The instrument first (P12-04)

The old generator waited with `Task.Delay`. It sent 13 to 21% of its writes early, its median
lateness was 1.46 ms, and it released writes in bursts.

The new one sleeps on the kernel's clock to 80 µs before each write and spins the rest. A first
version spun through every interval above 500 writes a second. In Compose that took more than a
processor from the cluster, and an interleaved three-way A/B there replaced it.

The generator's change, and the host's below (P12-05), each interleaved against the commit it
started from: five runs a side, same session, untraced.

| Comparison | Rate | Median before → after | 99th percentile before → after | Records |
|---|---|---|---|---|
| The generator, in process | 625 | 3,279 → 1,239 µs | 6.5 → 5.4 ms | m:p12-04-paced-ab-base-625-1 to -5, m:p12-04-paced-ab-fix-625-1 to -5 |
| The generator, in process | 3,125 | 7,510 → 1,943 µs | 54 → 31 ms | m:p12-04-paced-ab-base-3125-1 to -5, m:p12-04-paced-ab-fix-3125-1 to -5 |
| The generator, Compose | 625 | 3,873 → 1,581 µs | 7.7 → 5.3 ms | m:p12-04-paced-compose-ab-495b600-625-1 to -5, m:p12-04-paced-compose-ab-85edc0a-625-1 to -5 |
| The generator, Compose | 3,125 | 9,484 → 6,956 µs | 184 → 86 ms | m:p12-04-paced-compose-ab-495b600-3125-1 to -5, m:p12-04-paced-compose-ab-85edc0a-3125-1 to -5 |
| The host's hand-offs, in process | 625 | 1,176 → 1,099 µs | 4.5 → 3.1 ms | m:p12-05-ab-base-625-1 to -5, m:p12-05-ab-fix-625-1 to -5 |
| The host's hand-offs, in process | 3,125 | 1,479 → 1,358 µs | 30.0 → 29.8 ms | m:p12-05-ab-base-3125-1 to -5, m:p12-05-ab-fix-3125-1 to -5 |

The generator was inflating the latency phases 10 and 11 reported, by two thirds at 625 in
process. The fall was 1.4 times the lateness removed, because the old generator's bursts queued in
the leader.

### The host's hand-offs (P12-05)

Three changes, in the decomposition's order, each measured before the next:
1. each accepted connection is read on a thread of its own, blocking;
2. the loop is woken by the thread that writes its input, not through the pool;
3. a node's sends are written from its loop.

The six host hand-offs that are neither a sync nor a network hop now total 129 µs at 625, each
under 50 µs.

Each removed hand-off made the next wake dearer, and the decomposition saw every move. The net gain
was 79, 26 and 24 µs. The persist barrier is unchanged, and S-hostdisk-1 is still caught.

### The curve against the target (P12-06)

In process, this phase against phase 11's head, interleaved; Compose at the same rates. Each cell
is the range over five runs.

| Rate | Phase 11's head, in process: median; 99th percentile | This phase, in process | This phase, Compose | Records |
|---|---|---|---|---|
| 625 | 3.23 to 3.39 ms; 6.4 to 12.5 ms | 1.04 to 1.14 ms; 2.6 to 11.2 ms | 1.27 to 1.38 ms; 2.6 to 42.5 ms | m:p12-06-ab-base-625-1 to -5, m:p12-06-ab-fix-625-1 to -5, m:p12-06-compose-625-1 to -5 |
| 1,250 | 3.36 to 3.66 ms; 7.3 to 28.7 ms | 0.81 to 0.91 ms; 4.7 to 15.9 ms | 1.35 to 1.44 ms; 4.2 to 6.7 ms | m:p12-06-ab-base-1250-1 to -5, m:p12-06-ab-fix-1250-1 to -5, m:p12-06-compose-1250-1 to -5 |
| 2,000 | 4.19 to 4.56 ms; 10.4 to 26.0 ms | 0.90 to 1.03 ms; 10.7 to 71.5 ms | 1.61 to 1.74 ms; 6.4 to 17.2 ms | m:p12-06-ab-base-2000-1 to -5, m:p12-06-ab-fix-2000-1 to -5, m:p12-06-compose-2000-1 to -5 |
| 3,125 (the criterion's load) | 6.08 to 8.21 ms; 20.8 to 247 ms | 1.27 to 2.05 ms; 21.2 to 40.2 ms | 3.71 to 7.41 ms; 34.7 to 116 ms | m:p12-06-ab-base-3125-1 to -5, m:p12-06-ab-fix-3125-1 to -5, m:p12-06-compose-3125-1 to -5 |
| 3,500 | — | 1.77 to 4.70 ms; 31.8 to 207 ms | — | m:p12-07-local-3500-1 to -5 |
| 4,000 | 25 to 349 ms; 260 to 564 ms | 4.4 to 394 ms; 268 to 603 ms | 412 to 1,343 ms; 525 to 1,658 ms | m:p12-06-ab-base-4000-1 to -5, m:p12-06-ab-fix-4000-1 to -5, m:p12-06-compose-4000-1 to -5 |

The median at 3,125 in process is 1.40 ms, within the predicted 0.6 to 1.5 ms, and 2.6 times the
criterion. The 99th percentile criterion (2.47 ms) is met nowhere. The highest sustained rate is
3,125 in process and 2,000 in Compose.

**The gap, attributed** (the done criterion's second branch). At 625 every remaining segment is at
or under its floor above, except the persists, which are 24 to 68 µs over the concurrent sync. At
3,125, the leader's three queues (requests 313 µs, the wait to send 244, answers 384) are the loop's
load: its syncs take 0.75 of it, and a single server at that load waits about as long (an M/D/1
model fed with measured inputs, not a floor timed alone).

### The corrected gate (P12-07)

| Reading | Breaking rate | The leader's sync busy there | Records |
|---|---|---|---|
| As written: the lowest rate whose 99th percentile passes 100 ms in any run | 1,000 (one run of five, whose leader lost office mid-run) | 0.241 to 0.265, median 0.245 | m:p12-06-ab-fix-1000-1 to -5 |
| The guard's rate below it | 800, sustained in all five | 0.217 to 0.229 | m:p12-07-local-800-1 to -5 |
| Counting only tails broken by load (no leader change) | 3,500 (one run of five, 207 ms) | 0.775 to 0.796, median 0.787 | m:p12-07-local-3500-1 to -5 |
| The next measured rate | 4,000 (every run) | 0.794 to 0.807, median 0.800 | m:p12-06-ab-fix-4000-1 to -5 |

**The gate does not select the group commit, read either way.** Both readings are under 0.8, so
the register row closes by a spec change naming the measured cap (§2): the leader's single loop,
which runs its syncs inline, 79% of it at 3,500 writes a second, with requests and answers queuing
behind them. **The headline prediction's second half was wrong:** I predicted the corrected gate
would select the group commit.

**For you:** the load reading misses the threshold by 1.6%. Phase 11 read 75 to 80% at 4,000, and
the queues behind the syncs are most of the latency left at 3,125. The rule, applied as written,
does not propose the group commit; how much weight a 1.6% margin carries is yours.

### The control-margin audit (P12-08)

`gates margins` reads a table of every check whose verdict compares a run-to-run quantity with a
threshold. Each row carries both sides over at least five runs, unpatched and patched (timings at
GitHub's worse ratio), and the command flags any within a factor of 1.5 of its bound.

The first audit flagged five rows, and the staleness check as recorded a sixth:
- the slowdown control's floor (2.00 against 1.5);
- the barrier's lists per write (exactly its bound; a count, now read as writes without a list);
- the barrier per list's floor (1.03 against 0.9);
- the hand-off control's other segments (0.185 against 0.25), and its band's top against GitHub's
  1.40;
- one soak effect (108 against 100);
- the shard staleness factor (shard totals up to 1.34 of their recorded sums against a factor of
  2).

Each was moved or redeclared and measured again. Three of the six were bands drawn around the
expected value, which have no margin by construction.

**The audit's neighbour was a stand-in, and a kinder one than the harness.** The touched stage
before the second push failed the hand-off control unpatched, at −0.03, beside the Membership and
Scale suites' baseline checks. The audit had run the controls beside three single-threaded busy
processes, which never burst; the suites do, and a burst in one of the control's alternating
windows moved every median with it. The control now reads each segment's 5th percentile: a planted
spin moves the lower tail by its set amount, and load moves the upper part. The audit gained the
suites as a third neighbour, and every bench control was measured again in all three modes:
every row keeps 1.5 on both sides. The closest is the hand-off floor's patched side: with S-lat-1,
follower-queue rose 0.33 of the delay once beside the busy processes, 1.51 from the floor of 0.5.
In the same runs S-lat-1 is caught by the other segments' bound as well (network-out at 0.99 to
1.01 against 0.4). Next are the shard factor's low side (1.54) and the 32-in-flight backlog guard
(1.62).

**And the cost checks were audited on the shard compositions that had already run.** The second
push's run failed its head and two commits on S-disrupt-4, which the refreshed cost file had put in
a shard of its own. It took 41.5, 45 and 29.7 s against its line of 164.6; the last is beyond the
entry factor of 5 as well as the shard factor of 3. An entry's line is its time on GitHub beside
three other workers; alone, it has the runner's four processors. In fifteen shared shards it had run
86 to 182 s; its patch is to `Raft.Core`, and its build recompiles every project above it. Both
lower bounds now widen by the processors per worker (four over the shard's workers), the upper
bounds unchanged (S-shard-10), and a shard of one entry is judged as that entry, not again at the
shard factor (S-shard-11). Over shards of two entries or more, the shard factor still keeps 1.54.
The audit's held-out data had five one-entry shards, at 0.60 to 0.83; none of them was S-disrupt-4.

**Two GitHub observations past their bounds are left for you.** The slowdown control read 13.49
against its upper bound of 5 once, in `88512a6`'s test step (locally 1.84 to 2.82 over 15 runs).
No sabotage crosses that bound, and its own comment says it catches only a rise counted twice,
which would read about 4. And S-bench-1 survived once, at `c458641`: the stall control passed with
its sabotage, as it did twice in 13 runs in phase 11 (locally its patched side was 2 to 32 ms over
15 runs, against 150). `gates margins` fails on the first; the second has no value to put in a row,
since the harness prints none for a survivor. Whether to drop or redesign the slowdown's upper bound,
and whether the stall control needs a larger stall, are yours.

### The phase's cost in CI (P12-09)

Measured on the phase's two pushed runs before this report, every job a first attempt: `c8ff188`
(run:37785921243) and `f849ac2` (run:37830104156). Compared with phase 11's three: run:37693717232,
run:37701188082 and run:37720359149. This report's own push is not in them.

| | Phase 12 (two runs) | Phase 11 (three runs) | Runs |
|---|---|---|---|
| Harness shards (12), slowest and fastest step | 306 and 149 s; 351 and 79 s | 320 and 180 s; 314 and 136 s; 361 and 138 s | run:37785921243, run:37830104156, run:37693717232, run:37701188082, run:37720359149 |
| Gates step | 5 and 3 s | 4, 3 and 3 s | run:37785921243, run:37830104156, run:37693717232, run:37701188082, run:37720359149 |
| Tests step | 396 and 290 s | 329, 324 and 264 s | run:37785921243, run:37830104156, run:37693717232, run:37701188082, run:37720359149 |
| Baseline soak job | 804 and 561 s | 897, 494 and 834 s | run:37785921243, run:37830104156, run:37693717232, run:37701188082, run:37720359149 |
| Membership soak job | 884 and 864 s | 952, 957 and 828 s | run:37785921243, run:37830104156, run:37693717232, run:37701188082, run:37720359149 |
| Compose job | 101 and 96 s | 96, 92 and 100 s | run:37785921243, run:37830104156, run:37693717232, run:37701188082, run:37720359149 |
| Per-commit jobs (commits by shards), all given a runner | 132 and 228 | 84, 12 and 144 | run:37785921243, run:37830104156, run:37693717232, run:37701188082, run:37720359149 |
| Longest per-commit job | 717 and 755 s | 829, 511 and 774 s | run:37785921243, run:37830104156, run:37693717232, run:37701188082, run:37720359149 |

- **Every job of both runs was given a runner and ran its steps:** 132 and 228 per-commit jobs and
  every head job.
- **The shards** stayed under phase 11's slowest (306 and 351 s against 361). The fastest head
  shard of each run (149 and 79 s) is a single entry that failed its cost check, which this push's
  fix is for.
- **The tests step** grew with the phase's bench controls (396 s) and came back to 290; **the
  soaks** moved within the runner's spread.
- **Ten harness entries were added**, not under ten as predicted: two of them (S-shard-10 and -11)
  came from the second run.
- **The local run before each push** took about 33 minutes: the report rule, preflight, build,
  gates and tests (about 7.5), the host sabotages with the secret scan and the README walk (about
  19.5), and the touched entries (22 in 320 s; 15 in 308 s). Once the touched stage failed and the
  push waited for the fix (S-lat-1 unpatched at `14b9de6`, above).

## Done criteria

- **The latency decomposed along its path from the hosts' and the generator's timestamps (§9), the
  segments summing to the latency:** P12-03, every write joined; the sum is an identity, so content
  and causality are the guards.
- **The generator's lateness measured and bounded before any host change is judged:** P12-04, before
  P12-05; its median lateness 0.4 to 1.1 µs in every run since, and no write dispatched early. The
  tail is bounded by the records, not by the control (amended).
- **The host's largest non-design segments removed or explained:** P12-05, each hand-off under 50 µs.
- **The curve measured again against L, in process and in Compose: the criterion met, or the gap
  attributed segment by segment beside measured floors:** P12-06, not met; attributed (the floors
  above; at 3,125 by a model with measured inputs, which is not a floor timed alone).
- **The corrected gate applied:** P12-07, under 0.8 both ways; the row closed by §2.
- **Every run-to-run threshold check measured on both sides, any within 1.5 moved or redesigned:**
  P12-08, except two observations from GitHub's second run past their bounds, left for you (above).

## Predictions

**Evidence 10, forcing 0.** Right 1, partly 8, wrong 1.
Each outcome is in the breakdown.

- **P12-00, right.**
- **P12-01, partly:** three phases failed on phase 11's tree, not four; the audit found the two rows
  predicted and two more.
- **P12-02, partly:** 10 entries added, not 25 to 40.
- **P12-03, partly:** the generator's lateness was the largest segment, larger than predicted; at
  3,125 the loop's queues were, which I did not name.
- **P12-04, partly:** needed, and two thirds of the median; the tail bound was not reached.
- **P12-05, partly:** the two changes named, and a third they needed; the median not under 1 ms; what
  remains is the syncs, not wake-ups.
- **P12-06, partly:** the median in range; the tail, the sustained rate and the Compose gap not.
- **P12-07, wrong:** the gate does not select the group commit.
- **P12-08, partly:** the two named were flagged, the replication check was not; four more were;
  the audit's neighbours missed the one that broke the hand-off control, and its deals missed a
  shard of one entry; two GitHub observations past their bounds remain.
- **P12-09, partly:** every job had a runner and no shard came near 400 s; ten entries added, not
  under ten; the touched stage's growth on a push changing the node is untested (no push did).

## What exists

- **P12-01:** `Register.CompletionProblems` (both directions), `RegisterCompletionTests`; S-status-1,
  S-status-2.
- **P12-02:** the touched stage selects the soak-derived entries when the node changes
  (`Touched.SoakDerived`); S-touch-1, S-touch-2.
- **P12-03:** `HandOffTrace` (the hosts' stamps, and their bytes for a Compose node's `Trace|`),
  `Decomposition` (the join and its guards), `bench load --decompose 1`, `bench.sh trace-ab`,
  `DecompositionTests`, the hand-off control; S-lat-1, S-lat-2.
- **P12-04:** the generator's schedule on a thread of its own (`Pacing`: `clock_nanosleep` to 80 µs,
  then a spin), its lateness in every load record, the connection wait as a segment of its own,
  `bench.sh compose-ab`, the lateness control against a server that answers at once; S-bench-4.
- **P12-05:** `Frames.Read`; connections read on threads of their own; the input and outbound
  channels' continuations on the writing thread.
- **P12-06:** `bench sync --writers`, `bench handoff`, `bench hop`, `bench.sh floors`.
- **P12-07:** §2's statement.
- **P12-08:** `gates margins`, `scripts/margins.sh`, `MarginTests`, the margins tables in
  `docs/phases/P12/`; the cost file refreshed, the shard factor 3, the cost checks' lower bounds
  widened by the processors per worker, a shard of one entry judged as that entry; S-margin-1,
  S-shard-10, S-shard-11.

**Test projects** (at the start of phase 12 in brackets): Architecture 52 (52), Gates 180 (160),
Checker 173 (173), Simulation 110 (110), Core 852 (852), Scale 26 (26), Budget 3 (3), Membership 3
(3), Host.Tests 31 (26), Host.Slow.Tests 9 (7).

## Deviations and choices for the reviewer

- **Amendments to approved text, each with its measurement:**
  - **S-lat-2:** the sum guard could not fail; the guard is now content.
  - **The hand-off control, three times:**
    - its band: ±20% failed unpatched in the harness at 1.22, so half to twice against the delay
      actually spun;
    - then half to three times (P12-08);
    - then its statistic: the 5th percentile over the delay set, not the median over the delay spun,
      after the median failed unpatched beside the harness's suites (above, under P12-08).
  - **The lateness control:**
    - the 99th percentile bound needs two free processors, so the median and no early dispatch
      instead;
    - the median bound of 100 µs failed on GitHub at 569 µs, so 5 ms, with no early dispatch the
      property that holds under any load.
  - **The generator's mechanism:** a sleep to 80 µs and a spin, not a spin through every interval.
  - **S-margin-1's text:** the failure it reproduces is a patched side near the bound.
- **Six unpushed commits reworded, 150 records relabelled** (disclosed in the findings and the
  commit that says so). Their `Task:` line sat outside the trailer block, and the trailers gate
  found it before the push. Each reworded commit's tree is byte-identical to the one measured; a
  script replaced each old hash with its new one in the records' fields, ids and file names and in
  the breakdown. The alternative was about 100 minutes of measurements again. If you would rather
  the records were taken again, they can be.
- **Two sets of records discarded and taken again:**
  - P12-03's first 40, whose commit was repaired before the push to fix a stale patch;
  - P12-05's first five, for the same reason. I started the measurement before acting on the patch
    check.
- **P12-07's reading:** above. The leader change that sets the as-written reading is unexplained,
  twice in 565 load runs this phase, one on each host version (a register row).
- **New rows promised to P12, which need a home when you decide phase 13:**
  - the leader change;
  - the target against the floors;
  - and, from P12-01, the cost file's currency, the touched rate, the Budget seeds, and a record's
    commit.
- **Pushed commits that failed their jobs:** seven in the first run, twelve in the second
  (run:37785921243, run:37830104156), tabled below. The causes, each fixed at a later commit unless
  said:
  - S-sess-3's cost line, and then S-disrupt-4 alone in a shard (this push's fix);
  - the hand-off control, through its band and then its statistic (fixed at `e098dc6`);
  - the lateness control's median bound (fixed at `9c2ecb6`);
  - the slowdown control's upper bound, once, and S-bench-1's survival, once (not fixed: yours).
- **This push carries a fix to the gates** found by the second run: the cost checks' allowance for a
  shard's processors (S-shard-10, S-shard-11), under P12-08. It was found after a push, on the
  branch, as AGENTS.md says such failures will be.

## Register

Closed: the latency and group-commit row (P12-07, by §2). Opened: the leader change, the target
against the floors (both P12). Open from P12-01: four rows (P12).

## Findings added this phase

In `docs/findings.md` under phase 12, in order:
- the class above;
- a guard that holds by construction guards nothing;
- an instrument that errs both ways;
- a backlog shows where the queue is;
- a probe costs least where it is read;
- the switch expression's common type;
- a measurement pins its commit;
- an instrument that spins takes a processor;
- bursts hid a wake-up;
- a bound on a tail is a bound on the neighbours;
- a commit's message is part of its hash;
- a control calibrated alone was decided by its neighbours, again;
- a median is a tail on a saturated machine;
- removing a hand-off made the next wake dearer;
- a band drawn around the expected value has no margin;
- a neighbour's burst moves a median, and a planted delay moves the lower tail;
- an entry's line is its time beside three other workers.

## Still the person's

- The cold walk of the README (the P0 row).
- Whether the target is restated against the measured floors.
- What phase 13 is, given P12-07's reading.
- The slowdown control's upper bound (13.49 once on GitHub, no sabotage crosses it) and the stall
  control's survival once on GitHub: drop or redesign.

## Commits never verified in CI

Nineteen pushed commits of the phase never had a green verdict of their own: seven of its first
push (run:37785921243) and twelve of its second (run:37830104156). **Every one failed; none never
ran**: every (commit, shard) job of both runs was given a runner and ran its checks, 132 and 228
per-commit jobs and each head's twelve shards. Five commits of the first push and eight of the
second passed on every shard. **Accepted as never verified** (reviewer, at phase 10's acceptance),
as in phases 7, 9, 10 and 11: rewriting them would force-push, which `each-commit-list` refuses by
design. The causes, read from each job's log:

| Commit | Run | Result | Jobs | Cause |
|---|---|---|---|---|
| `d3aff30` | run:37785921243 | failed | shard 1 | S-sess-3's cost line, alone in its shard (250 s recorded; 118 s at the head, 0.47, past the shard factor of 2), refreshed at `0e37ba0` and `75f5a0e` |
| `c4506af` | run:37785921243 | failed | shards 1, 8 | S-sess-3's line, as at `d3aff30`; the hand-off control's ±20% band in S-lat-1's baseline (1.40 at `85edc0a`, below), widened at `c8ff188` and `4bd8595`, and its statistic changed at `e098dc6` |
| `c6aac4c` | run:37785921243 | failed | shard 8 | the hand-off control's band; documentation only |
| `85edc0a` | run:37785921243 | failed | shard 8 | the same, at 1.40 |
| `b7cd6c4` | run:37785921243 | failed | shard 8 | the same; documentation only |
| `3a8197f` | run:37785921243 | failed | shards 8, 11 | the same; the lateness control's median bound of 100 µs in S-bench-4's baseline, moved to 5 ms at `9c2ecb6`; documentation only |
| `c8ff188` (head) | run:37785921243 | failed | sabotage 1/12, 11/12 | S-sess-3's line alone (118 s against 250); the lateness control's median (569 µs against 100) |
| `cbb2767` | run:37830104156 | failed | shards 8, 11 | S-lat-1's target unpatched (the median control: follower-to-send rose 167 µs); S-bench-4's target unpatched (the generator's median lateness 208 µs against 100) |
| `88512a6` | run:37830104156 | failed | shards 1, 8 | its test step: the hand-off control (leader-queue rose 3.6 ms) and the slowdown control at 13.49 against its upper bound of 5; S-lat-1's target unpatched (follower-queue at −0.33); documentation only |
| `c458641` | run:37830104156 | failed | shards 6, 8 | S-bench-1 survived (the stall control passed with its sabotage); S-lat-1's target unpatched (leader-queue rose 8.3 ms) |
| `fd90165` | run:37830104156 | failed | shard 8 | S-lat-1's target unpatched (leader-queue rose 163 µs); documentation only |
| `4c27d1c` | run:37830104156 | failed | shards 1, 11 | S-sess-3's cost line, alone in its shard (121 s against 250, past the shard factor of 2); S-bench-4's target unpatched (281 µs) |
| `b2f43a8` | run:37830104156 | failed | shards 8, 11 | S-lat-1's target unpatched (follower-queue at −0.13); S-bench-4's target unpatched (641 µs); documentation only |
| `0e37ba0` | run:37830104156 | failed | shard 8 | S-lat-1's target unpatched (leader-queue rose 247 µs) |
| `fa81009` | run:37830104156 | failed | shard 8 | the same (420 µs); documentation only |
| `75f5a0e` | run:37830104156 | failed | shard 10 | S-lat-1's target unpatched (response-to-client rose 236 µs, against 0.4 of the delay) |
| `14b9de6` | run:37830104156 | failed | shards 2, 10 | S-disrupt-4 alone in its shard, 29.7 s against its line of 164.6 (both cost checks); S-lat-1's target unpatched (leader-queue rose 300 µs); documentation only |
| `e098dc6` | run:37830104156 | failed | shard 2 | S-disrupt-4 alone, 45 s against 165 (the shard check) |
| `f849ac2` (head) | run:37830104156 | failed | sabotage 2/12 | S-disrupt-4 alone, 41.5 s against 164.6 (the shard check); documentation only |
