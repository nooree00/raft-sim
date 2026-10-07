# Phase 10 — report

**Status: awaiting review.**

Performance, measured (spec §11 phase 10, and the register rows promised to it): P10-00 to P10-10,
as approved, in the breakdown's order. The first numbers the project has ever recorded about its own
speed are here, each in a record under `measurements/` with the configuration it was measured under,
and every number in this report's tables cites its record (`m:`) or its CI run (`run:`), which `gates
measurements` checks.

This report certifies the commit that contains it. `gates reports` checks that commit's CI run on
the next push.

## The result of the phase

### The throughput target is missed by more than two hundred times, and the cause is not the disk

The written target (P10-02, `docs/design/performance-target.md`), set from measured inputs before any
end-to-end number: one sync S = 160 µs, a loopback round trip R = 47 µs, so a commit L = 2S + R =
367 µs. At half the design's capacity (one leader sync per write, 6,250 a second), a median of at most
551 µs and a 99th percentile of at most 2.75 ms; and a sustained rate of at least 200,000 writes a
second, half the disk's capacity with 64 entries a sync.

Measured (P10-04), the open-loop generator at each rate for 10 s after a 2-s warm-up, five
repetitions each, in one process (three hosts on loopback) and in Compose (three containers):

| Offered (writes/s) | In process: completed/s, the five runs | In process: median latency | Compose: completed/s | Records |
|---|---|---|---|---|
| 625 | 624.7 to 624.9 | 3.2 ms (3.1 to 3.2) | 602.7 to 624.9 | m:p10-04-local-625-1 to -5, m:p10-04-compose-625-1 to -5 |
| 1,000 | 999.7, 304.9, 242.6, 201.6, 999.6 | 3.0 ms in the two that kept up; 5.3 s median of five | 465 to 627.3 | m:p10-04-local-1000-1 to -5, m:p10-04-compose-1000-1 to -5 |
| 1,250 | 0 to 158.3 | 10.2 s | 230.6 to 451.3 | m:p10-04-local-1250-1 to -5, m:p10-04-compose-1250-1 to -5 |
| 1,500 | 0 in all five | 11.8 s | 224.7 to 535.7 | m:p10-04-local-1500-1 to -5, m:p10-04-compose-1500-1 to -5 |
| 2,000 | 0 in all five | 16.5 s | 0 to 362.6 | m:p10-04-local-2000-1 to -5, m:p10-04-compose-2000-1 to -5 |
| 3,125 (the latency criterion's load) | 0 in all five | not finite | 0 in all five | m:p10-04-local-3125-1 to -5, m:p10-04-compose-3125-1 to -5 |

- **Throughput:** the highest sustained rate is about 625 to 1,000 writes a second in process and
  about 600 in Compose, against 200,000. Missed, as predicted; but not for the predicted reason.
- **The disk is not the cap.** The leader syncs once per committed write at every rate (1.00 to 1.01),
  which would allow 6,250 a second. What caps it is replication's cost per write growing with the
  backlog: below the knee the leader sends 3.4 messages and about 300 bytes to its peers per committed
  write; past it, about 200 messages and 170 KB. `RaftNode.SendAppend` does not advance a follower's
  next index when it sends, so every client write resends every unacknowledged entry, up to 64, to
  every follower. Once a backlog forms each write costs more, the backlog grows, and the cluster
  completes almost nothing: **a metastable failure** (findings). A burst does it: a stall, a slow
  sync, a busy runner (it is what made the stall control fail on GitHub, below).
- **Latency:** at the criterion's load, 3,125 a second, no write scheduled after the warm-up completed
  within the run, in either construction, so the target's median and 99th percentile are not finite
  there. Below the knee, at 625 a second, the in-process median is 3.2 ms and the 99th percentile
  6.9 ms: about six times the model's. Compose's median at 625 a second ranges from 4 ms to 362 ms
  across runs (each node writes every Core event to standard output).

Two provisional phase-11 register rows record this, **not built** (spec §2 excludes optimisation
until there is a baseline; this is the baseline): replication that resends unacknowledged entries on
every write (the cap measured here), and a group commit on the leader (the next cap, at the design's
6,250). Whether either goes into phase 11 is the reviewer's.

### The persist barrier costs a seventh to a fifth of a commit: kept (P10-05)

| Rate (writes/s) | Leader's sends wait behind its own persist, per write | Share of mean commit latency | Records |
|---|---|---|---|
| 300 | 534 µs (526 to 578) | 18% (of 2.9 ms) | m:p10-05-local-300-1 to -5 |
| 625 | 452 µs (443 to 497) | 14% (of 3.2 ms) | m:p10-05-local-625-1 to -5 |

Under the quarter the decision rule set, so the disk-acknowledgement row is closed: the barrier
stays. The guard held: with every sync delayed 20 ms and nothing else, the barrier measured 21.2 ms
per write against 20.2 ms slept (`TheBarrierMeasuredIsTheLeadersOwnSync`).

### A failed sync now stops the host; a power cut is tested (P10-07)

Before: a sync that threw killed the host's loop thread and nothing else; the process stayed up,
accepted client connections and answered none of them (a probe showed it, 3 s after the failure).
Now the host stops on a failed write or sync, closes its listeners and connections, executes nothing
more of the failed effect list, and the node process exits with code 3. `FsyncFailureTests` inject
both faults through the host's file-system interface under the real client's load, with the checker
on the history: a failing sync on the leader and on a follower; and a power cut just after a write
and before its sync, then the kill, then a restart from what the last syncs left. This is the crash
that phase 9's SIGKILL could not produce (it keeps the page cache). Machine-level power loss remains
a stated limit.

### Session expiry costs under about 3% of a soak execution (P10-06)

| | Phase-9 head | Phase-8 head | Records |
|---|---|---|---|
| 1,000 soak executions, Release, five interleaved runs | 63.6 s (62.5 to 65.0) | 63.0 s (61.6 to 64.6) | m:p10-06-soak-cur-1 to -5, m:p10-06-soak-p8-1 to -5 |

The difference, 0.6 s, is inside the spread (paired differences from −1.6 to +1.9 s). This is a
bound, not a figure. **A deviation:** the per-command micro-measurement with a planted 1-µs cost was
not made, so sensitivity below the bound is not shown.

### P10-08: the non-root failures of phase 9's first push

Reproduced, and explained (the reviewer's approval: a container, an unprivileged user, the Docker
socket and the proxy certificate read-only). Ten runs of the README walk and the host sabotages at
each commit, as uid 1001:
- **At `47d6ecd`, the host sabotages exited 255 in all ten runs.** S-compose-1 is caught; then its
  worktree cannot be removed, because the Compose build ran as root and left `bin/` and `obj/`
  owned by root, and `git worktree remove --force` exits **255** on a file it cannot delete. `set -e`
  passes it on. That is phase 9's exit code, and the guess applied at `1921551` (chown the worktree
  back first) named the right cause. At `1921551`, no run gave 255.
- **The walk's exit 1** did not recur in the ten walks at `47d6ecd`. At `1921551` it occurred twice in
  20 script runs (a walk, and a walk inside the host sabotages), both on the guard "an operation
  spanned the kill", with the history linearizable: the kill stamped before `docker compose kill`
  returned, the defect phase 9 fixed at `634c87a`, which neither commit contains. The same code ran
  at `47d6ecd`, so that is the likely cause of GitHub's exit 1; GitHub's own log stays unread.

So P9's finding changes from unexplained to explained. **For the reviewer:** the register closes a
row with a test or a spec change, and an explanation is neither, so the row is re-promised to P11,
for the reviewer to close.

### The visualiser is dropped (P10-09)

§9's optional item is removed by the spec amendment `e94b0a7`. No failing execution of phases 1 to 9
was diagnosed more slowly for the want of one (the list is in P10-09's outcome); the slowest
diagnoses were phase 9's, whose logs could not be read at all.

## Done criteria

- **A written target before any end-to-end number** (decision 2): P10-02, from the measured sync
  and round trip; committed before the first P10-04 record.
- **Every number with its configuration** (decision 1): P10-01, `measurements/*.json` written by the
  tool (commit, SDK, runtime, image, kernel, CPU model, count and limit, GC, tiering, the data file
  system, warm-up, repetition, load); `gates measurements` refuses a record missing a field and a
  report table row with a number and no citation. S-meas-1, S-meas-2.
- **An open-loop generator, with controls that prove it measures** (decision 3): P10-03, latency from
  the scheduled time; a planted stall in its tail and absent from a closed loop (S-bench-1); a planted
  sync delay moving the median by the model's amount (S-bench-2).
- **Throughput and latency against the target, in process and in Compose**: P10-04, above.
- **The barrier's cost and the disk-acknowledgement decision**: P10-05, kept; S-bench-3.
- **Session expiry's cost** (register): P10-06, a bound.
- **fsync failure injection** (register): P10-07; S-fsync-1, S-fsync-2.
- **The non-root failures** (register): P10-08, explained by reproduction.
- **The visualiser** (register): P10-09, dropped.
- **Cost-balanced harness shards, with the staleness check and the largest-manifest test**
  (the reviewer's conditions): P10-00; S-shard-4 to S-shard-9.

## Predictions

**Evidence 11, forcing 0.** On evidence: right 2, partly 7, wrong 2. Each outcome is in the breakdown.

- **P10-00, partly:** the slowest shard was under 600 s on the first run (580 s) and over it on the
  third (634 s); the spread was 1.61 to 2.15, not within 1.3; entries ran 0.33 to 4.4 times their
  recorded costs, not under 2.
- **P10-01, partly:** the first record failed the gate on the CPU limit, as predicted, but because
  the machine has cgroup v1, not because .NET ignores the quota (it does not).
- **P10-02, partly:** the median sync was under 1 ms (160 µs); its 99th percentile was 2.3 to 3.3
  times the median, not over ten.
- **P10-03, wrong:** the first control's numbers matched, for the wrong reason (a stall longer than
  the election timeout, and the collapse).
- **P10-04, partly:** the throughput miss was right; the cap is replication, not the sync; the
  latency half was wrong (nothing completed at the criterion's load).
- **P10-05, wrong:** the barrier is 14 to 18% of a commit, not a third to a half.
- **P10-06, right:** under the soak's spread.
- **P10-07, partly:** the predicted state (the process up, accepting, answering nothing) and not the
  predicted failure message (the dispose's rethrow masked it).
- **P10-08, partly:** the walk's exit 1 was the kill stamp, as predicted; the guess did reproduce 255, because git exits 255, not 128, on a file it cannot delete
- **P10-09, right:** no failure would have been diagnosed sooner with a timeline.
- **P10-10, partly:** the gates step grew by under 10 s, as predicted; the shards did not stay under 600 s in every run, and the new host entries' recorded costs were wrong

## What exists

- **P10-00:** `ShardPlan.Balance` (longest first into the least-loaded shard), `WorkerShares`
  (longest-processing-time across workers, the baseline build counted), `ci/sabotage-costs.txt`, the
  staleness check (each shard's total at factor 2; an entry's own line at factor 5 and 30 s), the
  plan printing modelled shard times, the 308-entry test. S-shard-4 to S-shard-9.
- **P10-01:** `Measurement` (the configuration read from the machine), `gates measurements`.
  S-meas-1, S-meas-2.
- **P10-02:** `Raft.Host bench sync|rtt`, `docs/design/performance-target.md`.
- **P10-03:** `LoadGenerator` (open loop, 64 connections, latency from the schedule, a per-write
  timeout, answers counted in the window), `BenchControlTests`. S-bench-1, S-bench-2.
- **P10-04:** `Raft.Host bench load|record`, `scripts/bench.sh` (in process and Compose), the host's
  sync and message counters.
- **P10-05:** the barrier accounting in `DiskExecutor`. S-bench-3.
- **P10-06:** `scripts/bench.sh soak-ab`.
- **P10-07:** the host's fail-stop (`NodeHost.Failure`, `Stopped`, exit code 3), `FsyncFailureTests`
  with an injected file system (failing syncs, a power cut, a revert to the last syncs). S-fsync-1,
  S-fsync-2.
- **P10-08:** no code: the reproduction is in the outcome (a container as uid 1001, ten runs at each commit).
- **P10-09:** the spec amendment.

**Test projects** (at the start of phase 10 in brackets): Architecture 50 (50), Gates 158 (129),
Checker 170 (170), Simulation 110 (110), Core 845 (845), Scale 23 (23), Budget 3 (3), Membership 3
(3), Host.Tests 33 (15).

## Deviations and choices for the reviewer

- **The staleness check changed after the first runs (P10-00).** The reviewer's condition was to
  fail when the cost file is stale beyond a stated factor. Per entry at factor 3, it failed four
  shards of one run on noise: one entry's time depends on what its worker built before it
  (S-compact-1: 6.3, 6.5 and 28.5 s). It now judges each shard's total against its recorded sum
  (factor 2; the totals ran 0.78 to 1.61) and an entry's own line only beyond factor 5 and 30 s,
  which still catches a new entry recording a tenth of its time. The costs were refreshed from
  GitHub's entry times (the harness prints them as an annotation). For the reviewer to keep or
  change.
- **The manifest passed 308 entries, so the plan is 12 shards now.** The count is derived (n =
  ceil(entries / 28)), and the three entries this phase added last took it to 309. The modelled
  shard times are 402 to 414 local seconds on GitHub's costs. The 308-entry test still holds for 11.
- **The stall control runs at 50 writes a second, not 200** (P10-03, after the push): at 200 on
  GitHub's runner a stall's backlog did not drain (the collapse) and writes went unanswered.
- **The touched-file stage (the reviewer's ask to report each fraction).** The plans printed before
  anything ran, at each push of the phase: 14 of 301 entries (4.7%), 8 of 305 (2.6%), 7 of 305
  (2.3%), and 18 of 309 (5.8%), estimated 4.1 minutes and run in 407 s, at this report's push. None was over the threshold. A push that changes
  node or harness code took a few entries, because the selection names files a patch or control
  touches, and most of this phase's code was new files.
- **P10-06's micro-measurement was not made** (above): the row was closed on the soak's bound.
- **Two capacities in the target.** Decision 2 said "the model's capacity" without saying what one
  sync carries; the target defines the design's (one sync per write) and the disk's (64 entries a
  sync), and uses each for one criterion.

## The new work's cost in CI (P10-10)

Measured on the phase's three pushed runs, every job a first attempt: `bc24b3d` (run:37569743638),
`6f48523` (run:37584673088) and `2586b5f` (run:37594669723). Compared with phase 9's last run,
`8de7acb` (run:37557274882). This report's own push is not in them.

| | Phase 10 (three runs) | Phase 9's last run | Runs |
|---|---|---|---|
| Harness shards (11), slowest and fastest step | 580 and 360 s; 590 and 328 s; 634 and 295 s | 683 and 398 s | run:37569743638, run:37584673088, run:37594669723, run:37557274882 |
| Gates step | 4, 3 and 4 s | 3 s | run:37569743638, run:37584673088, run:37594669723, run:37557274882 |
| Tests step | 252, 192 and 288 s | 183 s | run:37569743638, run:37584673088, run:37594669723, run:37557274882 |
| Baseline soak job | 491, 640 and 874 s | 837 s | run:37569743638, run:37584673088, run:37594669723, run:37557274882 |
| Membership soak job | 651, 991 and 925 s | 916 s | run:37569743638, run:37584673088, run:37594669723, run:37557274882 |
| Compose job | 98, 105 and 105 s | 101 s | run:37569743638, run:37584673088, run:37594669723, run:37557274882 |
| Longest per-commit job | 716, 848 and 949 s | 791 s | run:37569743638, run:37584673088, run:37594669723, run:37557274882 |

- **The shards:** the slowest is 50 to 100 s under phase 9's in every run, and stayed inside the
  ceiling, but the spread is 1.6 to 2.2, not the 1.3 predicted, and the third run's slowest passed
  600 s. With the costs now from GitHub, the modelled shards are even (402 to 414 local seconds, 12
  shards); whether GitHub's spread follows is for this push's run.
- **The tests step** grew by the bench controls and the fsync tests, which run real clusters for 10
  to 20 s each; the longest per-commit job with it.
- **The soaks** are the same code as phase 9's, and moved 491 to 874 s: the runner, as phase 9 found.
- **The local run before this push:** 1,894 s for the head before this report (`a745a69`): the report rule, preflight, build, gates and tests, the host sabotages with the secret scan and the README walk, and the 18 touched entries (407 s); phase 9's was 1,018 s. The growth is the host tests (the bench controls and the fsync tests) and a touched selection that was not empty. The report commit had its own full run before the push.

## Register

- **Closed:** disk acknowledgements (P10-05, kept, with the measurement); session expiry's cost
  (P10-06, a bound); fsync failure injection (P10-07); the visualiser (P10-09, dropped by `e94b0a7`).
  The non-root failures are explained (P10-08) and re-promised to P11, for the reviewer to close.
- **Opened, provisional, for phase 11 (the reviewer's):** replication that resends every
  unacknowledged entry on every write; a group commit on the leader.

## Findings added this phase

In `docs/findings.md` under Phase 10:
- the cluster slows in total as writes are outstanding, so a backlog never drains;
- a throughput figure that divides every answer by the window;
- a planted stall longer than the election timeout measures an election;
- controls calibrated on an idle machine fail on a shared one;
- one entry's time is not a property of the entry;
- fixes later in a push leave its earlier commits red for good;
- a test's teardown error hides its assertion;
- a crash after a clean kill is a kill again;
- the configuration a number is recorded under has to be read;
- phase 9's unexplained failure was its guess, and an exit code is measured, not recalled.

## Still the person's

- Adding `soak-membership` and `compose` beside `soak` in branch protection.
- Deleting `prerewrite-b96fc4b`, the probe branches and the sabotage branch.
- The cold walk of the README.
- P0.
- Whether the two provisional phase-11 rows go into phase 11.

## Commits never verified in CI

Fourteen pushed commits never had a green verdict of their own: the three push heads, each of
whose runs was red, and eleven commits the per-commit matrix failed. One more, `05eb9b8`, passed on
every shard. The causes, read from each job's annotations where the commit's own scripts emit them:

| Commit | Run | Red (commit, shard) or job | Cause |
|---|---|---|---|
| `28a3b49` | run:37569743638 | shard 1 | `HostTests.ClientWritesCommitAndAReadSeesThem` got no reply (a request lost at an election), fixed at `b29dc63` by retrying as a client does |
| `bc24b3d` (head) | run:37569743638 | sabotage 5/11 | not read: no annotation (the annotations came at `6f48523`) and the job log is not readable from here |
| `a1cae8c` | run:37584673088 | shard 4 | no annotation (before `6f48523`); the head's red shards in the same run were the staleness check |
| `9dd948c` | run:37584673088 | shard 5 | the same |
| `b076bb9` | run:37584673088 | shards 4, 5, 6 | the same |
| `e405319` | run:37584673088 | shards 2, 4, 6, 7, 8, 9 | the same |
| `b29dc63` | run:37584673088 | shards 2, 4, 6, 9, 10 | the same |
| `6f48523` (head) | run:37584673088 | sabotage 1, 2, 4, 6, 9 of 11 | the per-entry staleness check: the host entries' lines (about 15 to 20 s recorded, 62 to 75 s on GitHub), S-sched-1 and S-sched-2, S-compact-1 |
| `f715910` | run:37594669723 | shards 2, 4, 6, 9 | the per-entry staleness check: the host entries' lines, corrected later in the push, and S-compact-1 |
| `474d103` | run:37594669723 | shards 1, 2, 6, 7, 9, 10 | the same, and S-bench-1's baseline (67 writes unanswered) |
| `f23f43f` | run:37594669723 | shards 5, 6 | the per-entry staleness check on noise (S-commit-1, S-compact-3, S-dur-3: 0.27 to 0.32 of their lines) |
| `1dbc998` | run:37594669723 | shards 6, 7, 10 | the same, and S-bench-1's baseline (the closed run's 99th percentile 390 ms) |
| `eb98d09` | run:37594669723 | shards 7, 10 | S-bench-1's baseline (187 unanswered), S-compact-1 |
| `2586b5f` (head) | run:37594669723 | sabotage 7/11, 10/11 | S-bench-1's baseline (165 unanswered), S-compact-1 (28.5 s against 6.5) |

Two causes cover all but two: the per-entry staleness check, which judged noise as stale lines
(changed at `e2ee908`, the costs refreshed from GitHub), and the stall control's load on a shared
runner (`7b670f6`). The other two: a host test's lost request (fixed in the phase) and `bc24b3d`'s
unread shard. Every one of them is contained in this report's head, whose run is the check of the
fixes. That does not make them green on their own: the staleness failures would recur on each
commit's own scripts. **For the reviewer:** accept them as never verified, as in phases 7 and 9;
rewriting them would force-push, which `each-commit-list` refuses by design.
