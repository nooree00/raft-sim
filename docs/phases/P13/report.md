# Phase 13 — report

**Status: for review.** The leader's loop, against the restated target (spec §11 phase 13, P13-00's
row): P13-00 to P13-09 as approved, with the amendments each outcome records. Every number in this
report's tables cites its record (`m:`) or its CI run (`run:`). This report certifies the commit
that contains it; `gates reports` checks that commit's CI run on the next push. **It is pushed
again with two fixes** that its first push's run (run:38493194546) found after that push, both in
tests the effect thread broke on GitHub and not here (under P13-09 and deviations).

## First: moving the syncs off the loop moved the queue, not the load (P13-03 to P13-05)

Phase 12 named the cap as the leader's single loop running its syncs inline. This phase took the
effects off the loop, as node-interface §4 always allowed: one thread, in order, from a bounded
queue (P13-04). **The loop's queues went, and the effect thread's took their place.** Interleaved in
process against phase 12's host at 3,125 writes a second, five runs a side:

| At 3,125 writes a second, in process | Phase 12's host | This phase's host | Records |
|---|---|---|---|
| Median commit latency | 1,124 µs | 1,410 µs | m:p13-05-ab-base-3125-1 to -5, m:p13-05-ab-fix-3125-1 to -5 |
| 99th percentile | 26.4 ms | 35.7 ms | m:p13-05-ab-base-3125-1 to -5, m:p13-05-ab-fix-3125-1 to -5 |
| The leader's loop busy | 0.94 | 0.06 | m:p13-05-ab-base-3125-1 to -5, m:p13-05-ab-fix-3125-1 to -5 |
| The effect thread busy | — | 0.95 | m:p13-05-ab-fix-3125-1 to -5 |
| Serial work per write (loop, or effect thread) | 302 µs | 319 µs (16 handling on the loop, 304 executing) | m:p13-05-ab-base-3125-1 to -5, m:p13-05-ab-fix-3125-1 to -5 |
| Waits in the effect queue, before a persist and before a response (medians) | — | 280 µs, 213 µs | m:p13-05-ab-fix-3125-1 to -5 |
| Highest rate sustained in every run | 3,125 | 3,500 | m:p13-05-ab-base-3125-1 to -5, m:p13-05-ab-fix-3500-1 to -5, m:p13-05-ab-base-3500-1 to -5 |

P13-03 had measured the split before the change: of the loop's work at 3,125, the node's handling
was 4.5% and the effects' execution 95%, the sync 78% of it. What moved was the server, not the
work, and the median rose by a quarter: one more hand-off on each node's path (+78 µs at 625) and
the response's wait behind later persists, which §4 requires. **The headline prediction was wrong**
(the restated median met in no run of five, missed by 32%, not under 15%), **and its named
consequence does not follow:** it held that if the second wait were the remaining gap, the next
remedy would be the contract change. The second wait is 213 µs of a 344-µs gap at the median; the
first, before the persist, is larger, and both are the same serial resource at 0.95 of its time.
What changes the curve is less serial work per write, which is the group commit the corrected gate
did not decide, or the contract change, which would remove at most the response's wait. Both are
yours (decisions below). In Compose the trace shows the same exchange, the leader's two queues
(1,312 and 1,122 µs) replaced by the two waits, its traced median a quarter lower; untraced, both
hosts were past Compose's capacity on this machine at 3,125 that day.

## Second: the currency mechanism, and what its first run found (P13-01)

You merged the four hand-kept-input checks into one task with one mechanism, the thing that would
catch the fifth. They did share it, and it caught a fifth and a sixth on its first run.

- **A registry**, `ci/inputs.txt`: every input a gate reads, found in the tree rather than listed
  (every file under `ci/`, every `ci/` path a script, test, project file or workflow names, every
  repository path a string literal in the gates' source begins with), each with what verifies it:
  a currency observer, a gate that compares it both ways, a subject, a decision, or an open register
  row. `gates inputs` fails an input with no line. It cannot see a value kept by hand inside code
  (the Budget seeds were one, now `ci/budget-seeds.txt`) or a path a gate builds from pieces.
- **One comparison** (`Currency`: within a factor beyond a floor, or a member of an observed set),
  one failure text naming the input, the key, the claim, the observation and what the file should
  hold, and **four observers**, each where its truth lives: a record's commit against git's
  ancestry; the touched estimate (now the shard plan's model, its lines scaled by a fitted 1.25)
  against the stage's own run; the cost file's sum against GitHub's run total, read from every head
  shard's notice; the Budget seeds against the soak's ten hardest decided histories.

**What it found.** The ancestry observer failed 15 records on its first run: phase 10's
`p10-05-local-*` and `p10-06-soak-cur-*` named two commits rewritten before phase 10's push, for
three phases, after phase 12's relabelling had been taken as leaving none (I predicted none). Their
measured trees (`src/`, the bench script, the props, `global.json`) are byte-identical to the
on-branch commits, so they are relabelled. The registry found two premises nothing checks:
`docs/phases/P12/margins.tsv`, written by phase 12's own audit after it audited the inputs, and
`ci/broad-targets.txt`; each is now a register row. **What it cannot hold:** one cost-file line to
better than the harness's factor of 5. An entry's own time is not stable to better than about 2.5
on GitHub, so S-sess-3's line, 2.9 times low, could not be told from noise by any check one run
allows. The run's total can be held (factor 2), and the row closes on that, with this said.

| Observer, where it runs | Its input | Claims | Stale | Furthest | Run |
|---|---|---|---|---|---|
| github-run, `gates build-collect` | `ci/sabotage-costs.txt` | the run's total over 338 entries | 0 | 1.01 times the file's sum (factor 2) | run:38482181300 |
| soak-ranking, the soak job | `ci/budget-seeds.txt` | 3 seeds | 0 | ranked 1 to 3 (3,702, 2,371 and 890 states) of the ten hardest | run:38482181300 |
| record-commit, `gates measurements` | `measurements/` | 856 records | 0 | every commit an ancestor of the head | run:38482181300 |

The fourth, touched-run, runs in the local stage only: before the push of `3a1056b` it selected 46
of 338 entries (13.6%), estimated them at 6.5 minutes and ran them in 382 s, 0.98 of the estimate.


## Third: the target's figures: two declared, two dropped, the model falsified (P13-06, P13-08)

Your amendments: declare the operating point and the sustained-rate definition as conventions;
ground or drop the halving and the percentile summing; report the allowance's sensitivity. And
Compose stays ungrounded until its hop floor is measured.

- **Compose's floor measured (P13-06):** a cold frame from one container to a reader asleep in
  another took 110 µs at the median and 219 at the 99th percentile (m:p13-06-hop-compose-1 to -5),
  against loopback's 83 and 201. Compose's criterion from it: 1,167 µs and 3.50 ms.
- **Conventions declared:** each with what another choice would have tested.
- **The summing, tested and dropped.** End to end at 100 writes a second, where the model's queue
  is negligible, the median was 1,647 µs on this phase's host and 1,486 on phase 12's. The 99th
  percentiles were 2,598 and 2,402 µs. All are above the summed floor (939 µs, 2,186 µs): the run
  carries an idle system's wake-ups. At 1,250 the median on phase 12's host was 846 µs, below the
  summed median. The floor depends on the rate, so the sum is neither a bound nor an estimate.
  Dropped, as decision 7 said; **both latency criteria's floors are therefore ungrounded,** and
  1,066 µs and 3.38 ms are reported against, not met or missed in a grounded sense.
- **The halving, dropped.** The model meant to ground it is falsified by the same runs (below). The
  sustained rate is reported with no criterion: 3,500 in process on this phase's host, 3,125 on
  phase 12's.
- **The allowance's sensitivity, and its model checked.** The model is now a committed command,
  `bench allowance`. It reproduces the restated 127 and 1,194 µs within 0.4%, and its sensitivity
  table is in the target. It is asymmetric: 50% low moves the median criterion to 939 µs; 25% high
  to 1,358; 50% high puts the server at utilisation 1.0. You expected a wrong model to move the
  criterion a little; it does so only one way. **Both of its inputs proved wrong:** the serial work
  is 1.43 times the sync, and the generator's arrivals are evenly spaced, not Poisson. Those errors
  push the queue opposite ways (1,961 µs at the median with the measured service; none with even
  arrivals). The measured waits, 213 to 280 µs, lie between, so the restated allowance was close by
  cancellation.

This is the third of the target's figures set before measuring to fail when measured, after
phase 10's 200,000 a second and phase 12's slack factors.

## The result of the phase

### The spec (P13-00)

§11's phase-13 row has its done criterion; §4 says the host runs each node's effect lists off its
loop, in emitted order, the node never waiting for a sync, the barrier across lists as within one.
Node-interface §4 needed no change. The register's rows promised to phase 13 cite the row, and the
loop's cap gained a row of its own.

### Every load record carries the cluster's role changes (P13-02)

Each host keeps its node's changes of role with its evidence. For an election, that is how long the
host had read no frame only a leader sends (by the wire form's type byte, held to the codec by a
test) and the size of the tick it was handling. A load record keeps every host's changes over its
window, in process from the hosts and in Compose through `Roles|` (bench mode only,
`RAFT_BENCH_ROLES=1`). The control stalls the leader's loop for a second and needs the election, its
silence and the same changes through `Roles|` (S-lead-1).

| Load runs this phase | Runs | With leader changes | Elections counted unexplained | Records |
|---|---|---|---|---|
| P13-03, in process and Compose | 30 | 1 (Compose, 625 a second) | 0 | m:p13-03-traced-compose-625-2 |
| P13-05, in process | 70 | 0 | 0 | m:p13-05-ab-base-3125-1 to -5, m:p13-05-ab-fix-3125-1 to -5 and the other rates', m:p13-05-traced-local-625-1 to -5, m:p13-05-traced-local-3125-1 to -5 |
| P13-05, Compose | 70 | 3 (at 2,000 and above, past capacity) | 1 | m:p13-05-compose-ab-d1e6d43-2000-2, m:p13-05-compose-ab-a2aba25-3125-4, m:p13-05-compose-ab-a2aba25-4000-3 |
| P13-08, in process | 10 | 0 | 0 | m:p13-08-ab-base-100-1 to -5, m:p13-08-ab-fix-100-1 to -5 |

The counted one came during an election already under way, in a collapsed run. Its 123-ms silence
was reset by frames still leaving the deposed leader's effect queue, which drains after the leader
steps down. The rule counts it; the record says why. One in 45 runs had a leader change, not rarer than one in
200 as predicted; none in process. After S-lead-1's control failed once on GitHub (P13-09), the rule
allows the clock's interval (10 ms) between a timeout's expiry and the tick that sees it, a cause
inferred, not seen; no stored verdict changed.

### The loop measured before the change (P13-03)

| Per write, at 3,125 writes a second in process | Phase 12's host | Records |
|---|---|---|
| The node's handling | 13.6 µs | m:p13-03-untraced-local-3125-1 to -5 |
| The effects' execution | 287.6 µs | m:p13-03-untraced-local-3125-1 to -5 |
| Of which the sync | 233.7 µs | m:p13-03-untraced-local-3125-1 to -5 |
| The loop busy | 0.92 to 0.96 | m:p13-03-untraced-local-3125-1 to -5 |
| Timed parts against busy time read from the wait | 0.999 to 1.002 | m:p13-03-untraced-local-3125-1 to -5 |
| The loop's three queues, traced (medians) | 99, 70 and 212 µs, of a 1,211-µs median | m:p13-03-traced-local-3125-1 to -5 |

The guard is not algebra: the wait is timed apart from the parts.

### The effects off the loop (P13-04) and the curve (P13-05)

In process, this phase's host against phase 12's (`d1e6d43`, instrumented, nothing it does
changed), interleaved; Compose the same two hosts, interleaved, untraced. Each cell is the range
over five runs: the median; the 99th percentile.

| Rate | Phase 12's host, in process | This phase's host, in process | Phase 12's host, Compose | This phase's host, Compose | Records |
|---|---|---|---|---|---|
| 625 | 1.06 to 1.12 ms; 2.77 to 4.50 ms | 1.16 to 1.30 ms; 2.54 to 7.19 ms | 1.23 to 2.07 ms; 2.35 to 8.65 ms | 1.32 to 2.78 ms; 2.73 to 27.7 ms | m:p13-05-ab-base-625-1 to -5, m:p13-05-ab-fix-625-1 to -5, m:p13-05-compose-ab-d1e6d43-625-1 to -5, m:p13-05-compose-ab-a2aba25-625-1 to -5 |
| 1,250 | 0.82 to 0.93 ms; 4.12 to 43.3 ms | 0.85 to 1.16 ms; 4.09 to 10.7 ms | 1.30 to 2.60 ms; 3.57 to 36.7 ms | 1.38 to 3.44 ms; 6.45 to 60.4 ms | m:p13-05-ab-base-1250-1 to -5, m:p13-05-ab-fix-1250-1 to -5, m:p13-05-compose-ab-d1e6d43-1250-1 to -5, m:p13-05-compose-ab-a2aba25-1250-1 to -5 |
| 2,000 | 0.86 to 0.92 ms; 7.14 to 12.6 ms | 0.89 to 1.04 ms; 8.54 to 81.4 ms | 1.69 to 906 ms; 20.8 to 1,205 ms | 1.71 to 72.2 ms; 8.88 to 317 ms | m:p13-05-ab-base-2000-1 to -5, m:p13-05-ab-fix-2000-1 to -5, m:p13-05-compose-ab-d1e6d43-2000-1 to -5, m:p13-05-compose-ab-a2aba25-2000-1 to -5 |
| 3,125 (the criterion's load) | 1.08 to 1.49 ms; 11.3 to 50.1 ms | 1.12 to 1.46 ms; 22.7 to 54.9 ms | 5.13 to 2,056 ms; 48.2 to 2,363 ms | 6.99 to 1,550 ms; 37.0 to 4,387 ms | m:p13-05-ab-base-3125-1 to -5, m:p13-05-ab-fix-3125-1 to -5, m:p13-05-compose-ab-d1e6d43-3125-1 to -5, m:p13-05-compose-ab-a2aba25-3125-1 to -5 |
| 3,500 | 1.34 to 2.38 ms; 19.6 to 253 ms | 1.51 to 5.35 ms; 30.8 to 56.7 ms | 7.10 to 3,920 ms; 99.4 to 4,231 ms | 231 to 2,449 ms; 867 to 3,559 ms | m:p13-05-ab-base-3500-1 to -5, m:p13-05-ab-fix-3500-1 to -5, m:p13-05-compose-ab-d1e6d43-3500-1 to -5, m:p13-05-compose-ab-a2aba25-3500-1 to -5 |
| 4,000 | 3.30 to 165 ms; 39.9 to 397 ms | 2.40 to 278 ms; 38.7 to 788 ms | 223 to 5,110 ms; 482 to 6,864 ms | 324 to 4,643 ms; 427 to 5,999 ms | m:p13-05-ab-base-4000-1 to -5, m:p13-05-ab-fix-4000-1 to -5, m:p13-05-compose-ab-d1e6d43-4000-1 to -5, m:p13-05-compose-ab-a2aba25-4000-1 to -5 |

**The median at 3,125 in process rose by a quarter**, 1,124 µs to 1,410 at the runs' medians, and
the restated 1,066 µs is met in no run of either host's five. The 99th percentile's criterion
(3.38 ms) is met nowhere. **The highest sustained rate rose one step:** at 3,500 every run of this
phase's host kept the definition, four of five of phase 12's. **Compose holds to 2,000:** from
3,125 both hosts collapsed in four repetitions of five (the first ran at 5.1 and 7.0 ms; the machine
slowed over the hour). At 2,000 one run of each host's five went past capacity (906 and 72 ms at
the median) and phase 12's host lost its leader in another; the hosts' medians of five there are
3,563 and 3,637 µs. Traced, in
Compose at 3,125, the median fell from 4,376 µs to 3,294 (m:p13-03-traced-compose-3125-1 to -5,
m:p13-05-traced-compose-3125-1 to -5, not interleaved), a quarter, not the predicted half.

**The gap, attributed** (the done criterion's second branch): at 3,125 in process it is 344 µs at
the median. The wait in the effect queue before a response is 213 µs of it and the wait before a
persist, earlier on the path, 280; both are queues at the one serial resource, the leader's
execution of its effects, 304 µs a write at 0.95 of its time. The floors it was to be read beside
were dropped by P13-08, and the allowance's model was falsified by it (below).

### Compose's hop floor (P13-06)

| Floor | Median | 99th percentile | Records |
|---|---|---|---|
| A cold one-way frame, container to container | 110 µs | 219 µs | m:p13-06-hop-compose-1 to -5 |
| (for comparison) the same on loopback, phase 12 | 83 µs | 201 µs | m:p12-06-hop-1 to -5 |

### The stall control (P13-07)

The control was redesigned so its sabotage cannot pass (P13-07): a 1-s stall of the leader's loop
every 4 s, election timeouts 3 to 6 s so the stalled leader keeps office, and the open loop judged at
its 95th percentile. Unpatched, 86 to 88 of 400 open-loop writes took over 150 ms (21.5 to 22%), the
95th percentile 806 to 810 ms in three runs here and 811 to 839 ms over the audit's fifteen. With
S-bench-1 (the loop never stalls) the 95th percentile was 2.0 ms in the harness, 3.3 to 3.7 ms
alone, 10 to 16 beside three busy processes and **24 to 26 beside the Membership and Scale suites,
over the predicted 20**.

The margin audit was run again after the host's change (`docs/phases/P13/margins-bench.tsv`, at
`3a1056b`: the host's bench controls, fifteen runs a side, five alone, five beside busy processes,
five beside the suites; the replication cost check, one run a side). `gates margins` passes it:
the stall control's open side keeps 5.95 unpatched and 3.80 patched, its closed side 3.12; every
other row keeps the audit's 1.5, the closest being the 32-in-flight backlog guard (1.62) and the
barrier per list's floor (1.73), as in phase 12. The audit tolerates a failing run and records only
percentiles, so whether the stalled leader kept office in each of its runs is not in its table; the
control asserts it, and passed in CI and in the three runs here. `docs/phases/P12/margins.tsv`, the
file `gates margins` reads by default, still holds phase 12's bench rows (a register row).

### The target's figures (P13-08)

| Figure | As restated at phase 12 | Measured this phase | Now | Records |
|---|---|---|---|---|
| The floor's median, the parts summed | 939 µs | 1,647 µs (this host) and 1,486 (phase 12's) at 100 writes a second; 846 on phase 12's host at 1,250 | dropped: it depends on the rate | m:p13-08-ab-fix-100-1 to -5, m:p13-08-ab-base-100-1 to -5, m:p13-05-ab-base-1250-1 to -5 |
| The floor's 99th percentile, the parts summed | 2,186 µs | 2,598 and 2,402 µs at 100 writes a second | dropped | m:p13-08-ab-fix-100-1 to -5, m:p13-08-ab-base-100-1 to -5 |
| The allowance's service per write | 212 µs, the sync alone | 304 µs, 1.43 times the sync | the model's input wrong | m:p13-05-ab-fix-3125-1 to -5 |
| The allowance's median wait (M/D/1) | 127 µs | the model with the measured service 1,961 µs; with even arrivals none; measured 213 and 280 | the model falsified | m:p13-08-allowance-3125-d1.0, m:p13-08-allowance-3125-measured, m:p13-08-allowance-3125-measured-even, m:p13-05-ab-fix-3125-1 to -5 |
| The median criterion at 3,125 | 1,066 µs | 1,410 µs in process | reported against, not grounded | m:p13-05-ab-fix-3125-1 to -5 |
| The 99th-percentile criterion at 3,125 | 3.38 ms | 35.7 ms in process | reported against, not grounded | m:p13-05-ab-fix-3125-1 to -5 |
| The sustained-rate criterion (the halving) | 3,139 a second | 3,500 sustained in process | dropped; reported with no criterion | m:p13-05-ab-fix-3500-1 to -5 |
| Compose's criterion | none | from the 110-µs hop: 1,167 µs and 3.50 ms | restated, on the same summed floor | m:p13-06-hop-compose-1 to -5 |

The operating point (half of C_design) and the sustained-rate definition are declared conventions
in `docs/design/performance-target.md`, each with what another choice would have tested; the
sensitivity table, from committed records (`bench allowance`), is there.

### The phase's cost in CI (P13-09)

Measured on the phase's one pushed run before this report, every job a first attempt: `3a1056b`
(run:38482181300). Compared with phase 12's first two runs and its report's: run:37785921243,
run:37830104156 and run:37845882191. This report's own push is not in them.

| | Phase 13 (one run) | Phase 12 (three runs) | Runs |
|---|---|---|---|
| Harness shards, slowest and fastest step | 352 and 87 s (13 shards) | 306 and 149 s; 351 and 79 s; 360 and 92 s (12) | run:38482181300, run:37785921243, run:37830104156, run:37845882191 |
| Gates step | 6 s | 5, 3 and 4 s | run:38482181300, run:37785921243, run:37830104156, run:37845882191 |
| Tests step | 405 s | 396, 290 and 395 s | run:38482181300, run:37785921243, run:37830104156, run:37845882191 |
| Baseline soak step (the soak's own time) | 538 s (536) | 804, 561 and 427 s (803, 559 and 426) | run:38482181300, run:37785921243, run:37830104156, run:37845882191 |
| Membership soak step | 727 s | 884, 864 and 824 s | run:38482181300, run:37785921243, run:37830104156, run:37845882191 |
| Compose job | 97 s | 108, 102 and 100 s | run:38482181300, run:37785921243, run:37830104156, run:37845882191 |
| Build job, and its collecting step | 26 s, 10 s | 24, 36 and 29 s; 8, 13 and 6 s | run:38482181300, run:37785921243, run:37830104156, run:37845882191 |
| Per-commit jobs (commits by shards), all given a runner | 163 (13 commits by 12 or 13) | 132, 228 and 12 | run:38482181300, run:37785921243, run:37830104156, run:37845882191 |
| Longest per-commit job | 734 s | 735, 755 and 564 s | run:38482181300, run:37785921243, run:37830104156, run:37845882191 |

- **Every job was given a runner and ran its steps:** 163 per-commit jobs and every head job.
- **Ten harness entries were added**, the ten predicted. The three S-loop entries took the manifest
  from 335 to 338, past the 336 twelve shards of 28 hold, so the head and every commit from
  `a2aba25` ran thirteen shards; the slowest stayed under phase 12's (352 s against 360).
- **The soak's own time moves 1.9 times with its runner** on identical work (the same elections,
  entries and hardest histories to the count), so the comparison resolves only the check added to
  it: its verdict was printed 0.07 s after the soak's report.
- **One per-commit job failed**, `4ca269b`'s shard 9: S-lead-1's control, unpatched (below). Every
  other (commit, shard) passed, and the head's jobs passed; the run is red on that one job.
- **The report's first push** (`96fae37`, run:38493194546): every job given a runner and run, 52
  per-commit jobs and the head's; the slowest head shard 350 s; the soaks' own steps 490 and 436 s.
  Two per-commit jobs failed, both at `5e11dd5` (shards 10 and 13): the hand-off control's other
  segments and the election test's announcement, each failing unpatched (below). The head passed.
- **The local run before the push** took about 35 minutes: the report rule, preflight, build, gates
  and tests (about 8), the host sabotages with the secret scan and the README walk (about 20), and
  the touched entries (46 in 382 s).

## Decisions for you

1. **The effect thread: keep it, revert it, or keep it and take the contract change.** It is what
   the spec now says (P13-00) and it does what §4 describes, but in process it costs a quarter of the
   median at 3,125 writes a second and one hand-off at every rate, for one step of sustained rate.
   Reverting means amending §4's new bullet back. The contract change (a response released at its
   own entry's durability, decision 2's option not chosen) needs the effect thread, and would remove
   at most the response's wait (213 µs at the median). On a machine with no processor free, the
   hand-off also couples the three nodes in one process: a delay in the followers' path moved the
   persist segments by up to a millisecond, where phase 12's host moved them by 85 µs (the hand-off
   control's fix, below). Deployed one node to a machine, that coupling is not there; in tests it is.
2. **The next remedy.** The data name the leader's serial work per write as the cap (0.95 busy at
   3,125, wherever it runs). Less of it per write is the group commit the corrected gate did not
   decide; the headline's named consequence (the contract change) did not follow. A register row,
   promised to P13, needs its home.
3. **The target.** Both latency criteria rest on summed floors that the test dropped; the halving
   is dropped; the allowance's model is falsified. The figures stand as what is reported against.
   Whether to restate (one option: a floor measured end to end at the curve's warmest point, 846 µs at
   1,250 a second; another: a model with the generator's arrivals and the measured service) is yours.
4. **Homes for four rows promised to P13 by this phase's results:** the two premises the registry
   found (`ci/broad-targets.txt`, `docs/phases/P12/margins.tsv`), the serial work, and the target's
   figures.
5. **Compose on this machine** was past its capacity at 3,125 and above for both hosts on the day of
   P13-05, so its comparisons hold to 2,000; its numbers are not comparable with phase 12's.

## Deviations and choices

- **P13-03 and P13-05's baseline is phase 12's host instrumented** (`d1e6d43`: P13-02's role log
  and P13-03's loop timers, nothing the host does changed), not phase 12's head itself, so every
  record of the comparison carries role changes and the loop's split. The Compose baseline ran from a
  worktree at `d1e6d43` after my mid-run edit to `src/` made bench.sh refuse it, as it should.
- **The control for the effect thread runs on three nodes with election timeouts of 1 to 2 s.** A
  one-node cluster answered no plain write, and on three nodes a held sync holds the leader's
  heartbeats too (they wait behind the persist, §4), so at the default timeouts the leader lost office
  during the 300-ms hold. The same holds for any slow sync: the effect thread does not keep a leader
  in office through one.
- **`EffectQueue` was renamed `EffectThread`** for the analyzer's rule on type names.
- **Seven unpushed commits rewritten** (`5121b0e` to `3a1056b`): two outcomes lacked the em dash
  `gates breakdown` requires, which would have failed every later commit in the per-commit matrix.
  The 21 records naming two of the rewritten commits are relabelled to their replays, whose `src/`,
  scripts, props and `global.json` are byte-identical.
- **A commit made with a gate failing:** P13-02's first commit went in while `gates patches` reported
  two stale patches (S-fsync-1, S-lat-1); amended before any push, and each commit since gated on the
  fast gates first.
- **The container restarted** during the first margin audit, which left nothing; it was run again.
- **This push carries a fix found by the phase's run** (run:38482181300), after a push, on the
  branch, as AGENTS.md says such failures will be: `1f25f3d` makes S-lead-1's control stall whichever
  node leads when it acts and wait for a node taking office after the stall (it failed unpatched at
  `4ca269b`, and about once in sixty runs here), and allows P13-02's election rule the clock's
  interval. The second change came after P13-02's outcome was written; the outcome says so, and no
  stored verdict changed. Sixty runs of the control since, none failed.
- **Two fixes after the report's first push** (run:38493194546), found on GitHub, after a push, on the
  branch, in one commit (`7f4b02c`), so that no commit of this push carries either fault:
  - The election test waits for the new leader's announcement. Since P13-04 the effect
    thread writes it while the loop publishes the role, and the test, reading the log the moment it
    saw the role, once found none. Read from the code; forty contended runs here did not reproduce it.
  - The hand-off control runs in twenty alternating windows of a second, not six of three, and
    with 2 ms planted, not 500 µs, slept to its last 80 µs rather than spun (`NodeHost.DelayHandOff`).
    Its other segments had risen past 0.4 of the delay (leader-persist 226 µs on GitHub): with no
    processor free, the planted delay moves the followers' persists away from the leader's, and the
    effect threads' wakes wait on one another. Reproduced on two processors beside two busy loops (ten
    failures in eighteen runs, phase 12's host none in six); after the change, none in eight. The bound
    is unchanged; its rows were audited again, both sides in the three modes, at `7f4b02c`: follower-queue
    rose 1.0005 to 1.0050 of the delay unpatched and at most 0.0035 with S-lat-1, the other segments at
    most 0.059 unpatched and at least 0.9905 with it, every margin 2.0 or more (`docs/phases/P13/margins-bench.tsv`).
- **Fifteen phase-10 records relabelled** by P13-01's first run (above), to on-branch commits whose
  measured trees are byte-identical; the alternative was to retake phase 10's measurements.

## Done criteria

- **The host executes each node's effect lists off its loop, on one thread, in emitted order, the
  node never waiting for a sync, with the barrier, the stop on a failed write or sync, and every
  invariant, soak, Compose and checker result unchanged:** P13-04; met, the soaks and the Compose run
  green on run:38482181300. Its latency is worse (the first section).
- **The curve measured again against the restated target, interleaved against phase 12's head, in
  process and in Compose: the criteria met, or the gap attributed segment by segment beside the
  measured floors and the allowance's model:** P13-05; not met, attributed: the gap is the two waits
  at the one serial resource, beside floors whose sum P13-08 then dropped and a model it falsified.
- **Compose's hop floor measured and a Compose criterion restated from it:** P13-06; met.
- **Every hand-kept input a gate reads registered with what verifies it, the four compared by one
  mechanism, any other a register row:** P13-01; met, two others found, each a row.
- **Every row promised to phase 13 closed:** the stall control (P13-07), leader changes (P13-02), and
  the four inputs (P13-01) closed; the target's figures settled as far as measurement goes (P13-08),
  the row kept open for your restatement. The results opened three rows; with the target's, four
  need homes (decisions above).

## Predictions

**Evidence 10, forcing 0.** Right 3, partly 5, wrong 2.
Each outcome is in the breakdown.

- **P13-00, right.**
- **P13-01, partly:** one comparison for the four, the flat rate's misses as named and the seeds
  unchanged; 15 phase-10 records off the branch, not none; 28 inputs, not 15 to 25; the model's
  "17 of 19" undecided (14 of the 15 it can be computed for, at a scale fitted to them).
- **P13-02, partly:** every change explained or counted; changes 1 in 45 runs, not rarer than 1 in 200.
- **P13-03, partly:** the handling under 0.15 of the loop; the queues a third, not half; the single
  server of syncs off by 1.29, not within 20%.
- **P13-04, partly:** the loop's queues gone and the two waits in range; the median rose 25%, not
  fell 15 to 35%; the sustained rate one step up, as predicted.
- **P13-05, wrong:** the median met in no run and missed by 32%; the tail at 35.7 ms, not 5 to 15;
  the named consequence (the contract change next) does not follow.
- **P13-06, right.**
- **P13-07, partly:** every share and margin as predicted; S-bench-1's 95th percentile beside the
  suites 24 to 26 ms, not under 20.
- **P13-08, wrong:** the end-to-end floor above the sum, not under it; the service 1.43 times the
  sync, not 1.0 to 1.2; the halving dropped, not grounded.
- **P13-09, right.**

## What exists

- **P13-01:** `ci/inputs.txt`, `gates inputs` (`Inputs`), `Currency` (two policies, one failure
  text, `gates currency soak-ranking`), the observers in `gates measurements`, `gates sabotage
  --touched` (`Touched.Estimate`, `Touched.CheckRun`), `gates build-collect`
  (`BuildCollect.CheckCosts`) and the soak job; `ci/budget-seeds.txt`; `checks: read` in the
  workflow; S-input-1, S-input-2, S-meas-3, S-touch-3, S-shard-12, S-soak-12.
- **P13-02:** `NodeHost.RoleChanges`, `RoleChange`, `Roles|` (`RAFT_BENCH_ROLES=1`), `StallOnce`,
  the records' `roles`; `RoleChangeTests`; S-lead-1.
- **P13-03:** the loop's timers (`NodeHost.LoopTimes`) and the records' `leader_loop_*` fields.
- **P13-04:** `EffectThread`, `NodeHost.EffectTimes` and `EffectWaits`, the records' `leader_effects_*`
  and `leader_effect_queue_*` fields; `EffectThreadTests`; S-loop-1 to S-loop-3; S-fsync-1 now
  swallows the effect thread's failure.
- **P13-05:** `bench.sh compose-node-ab`.
- **P13-06:** `bench hop-reader`, `bench hop-writer`, `bench.sh compose-hop`.
- **P13-07:** the stall control redesigned; `margins.sh` reads its row.
- **P13-08:** `bench allowance`, `bench.sh allowance`; the target's two new sections.

**Test projects** (at the start of phase 13 in brackets): Architecture 52 (52), Gates 207 (180),
Checker 173 (173), Simulation 110 (110), Core 852 (852), Scale 26 (26), Budget 3 (3), Membership 3
(3), Host.Tests 38 (31), Host.Slow.Tests 9 (9).

## Register

Closed: the leader changes (P13-02), the host's loop running each effect list to its end (P13-04),
the stall control (P13-07), and the four hand-kept inputs (P13-01: the cost file's currency, the
touched estimate, the Budget seeds, a record's commit). Settled as far as measurement goes and kept
open for you: the target's figures set by hand (P13-06, P13-08). Opened: the leader's serial work
per write (P13-05); `ci/broad-targets.txt` and `docs/phases/P12/margins.tsv`, premises nothing
checks (P13-01). Every row open for phase 13 has its home with you at acceptance.

## Findings added this phase

In `docs/findings.md` under phase 13, in order:
- moving the serial work moved its queue, not its load;
- a model whose two errors cancel looks grounded;
- a floor that depends on the rate is not a sum of parts;
- the currency mechanism's first run found what memory missed for three phases;
- a control that names "the leader" once and acts on it later tests a moment that has passed;
- a format rule found late costs a replay;
- a hand-off off the loop couples the nodes that share a processor;
- a state one thread publishes and an effect another runs are ordered by nothing a reader sees.

## Still the person's

- The cold walk of the README (the P0 row).
- The decisions above: the effect thread, the next remedy, the target, the four rows' homes.
- Whether two containers on one machine stand for Compose's hosts (P13-06): they share one kernel,
  which is what lets one clock time the hop.
- Adding `compose` to branch protection, as at phase 9.

## Commits never verified in CI

Two pushed commits of the phase never had a green verdict of their own: `4ca269b`, of its first push
(run:38482181300), and `5e11dd5`, of the report's first push (run:38493194546). **Both failed; none
never ran**: every (commit, shard) job of both runs was given a runner and ran its checks, 163 and 52
per-commit jobs and each head's thirteen shards, and every other commit passed on every shard. **Accepted as never verified** (reviewer, at phase 10's
acceptance), as in phases 7, 9 to 12, unless you say otherwise: rewriting them would force-push, which
`each-commit-list` refuses by design. The causes, read from the jobs' logs:

| Commit | Run | Result | Jobs | Cause |
|---|---|---|---|---|
| `4ca269b` | run:38482181300 | failed | shard 9 of 13 | S-lead-1's target unpatched: no follower took office after the leader was stalled, because a second election had made another node leader before the stall; fixed at `1f25f3d`, pushed with this report. S-lead-1's ten other runs in the run passed |
| `5e11dd5` | run:38493194546 | failed | shards 10 and 13 of 13 | S-lat-1's target unpatched: leader-persist rose 226 µs against the bound of 200, the effect threads coupled on a busy runner, fixed at `7f4b02c`; S-host-1's target unpatched: a leader found with no announcement yet written, fixed there too. Documentation only; the same source passed at `ad38cdf`, `715500a` and the head |
