# Phase 4 — report

**Status: accepted** (reviewer, at the start of phase 5; the status line folded into the next push). Log replication and log persistence (spec §11 phase 4), P4-01 to
P4-10 as approved, P4-11, added when the head's full local run failed the harness ceiling, and
P4-12, added when GitHub's run of the head failed it again (the P5 register row, brought forward).
The reviewer's decisions at the end of the phase are applied: per-commit verification moved to CI
(the working agreement amended), the P4-09 limits approved, CheckQuorum's exclusion stated in spec
§2 with its measured consequence, the liveness-as-a-rate finding, and every sabotage patch checked
with `git apply` only (`gates patches`). At the ceiling on GitHub: P4-12 for the head, and the
commits before it accepted as unverifiable in CI, listed below under "Commits never verified in CI".

This report certifies the commit that contains it. `gates reports` checks that commit's CI run on
the next push.

## The results of the phase

### S-ran-2: the count-gate failure, planted in advance and caught

The result of the phase. P4-11 moved tests out of the project people run, which is the risk the
CRDT project met as a discovery: `npm test` green, 213 of 213, with the comparator inverted,
because the only oracle lived in a file the run excluded. Here the same failure was planted
deliberately, in advance, on the commit that created the risk. S-ran-2 drops the durability tests
from `Raft.Scale.Tests` and edits the baseline from 11 to 9. **The old gate passed it:
"Raft.Scale.Tests: 9 executed … ok".** A count gate compares a number to a number, so removing tests
and adjusting the expectation satisfies it exactly. The new gate compares the tests written in a
project's sources with the tests its run executed, and named both durability tests and the two
harness entries aimed at them. It is the first time this pattern has been planted and caught
rather than discovered.

The same mechanism turned up twice more at smaller scale this phase, each a local check drifting
from the enforcing one with nothing signalling the disagreement: my `patch --dry-run` against the
harness's `git apply` (five stale patches through), and my local CI script's hard-coded 6 shards
against the count CI derives from the manifest. Both now use the enforcing tool.

### A liveness deadline is met by a system that thrashes

The phase's most transferable finding. Invariant 11 is written as a deadline: after the last fault
heals, a leader acts within K, and (new in this phase) a command a client submitted after the
suffix began is committed in fact within K. Both clauses have now been met by a system that was
plainly degraded:

- **P3-06, the leader clause.** With the §6 rule off, a node that cannot hear the leader deposed
  it about 20 times per run. The clause never failed: each deposed leader was replaced in time.
- **P4-06, the commit clause.** With the rule off again, leaders changed 21 times per run and
  committed commands halved (74,470 against 148,810 over the same 200 seeds). The clause failed in
  none of the 151 runs where the cut-off node was not the leader: no 3,000-tick stretch passed
  without a commit.

A deadline generous enough for every correct run is met by one that does the thing often and badly.
Liveness-as-a-deadline cannot express liveness-as-a-rate. The findings log says what a rate version
would need (a per-execution baseline from the same seed with the fault removed, a windowed commit
and leader-change rate, a stated tolerance, a scope of which faults must be tolerated at full rate,
and a floor on the baseline), and that every later phase's liveness criterion should say whether a
thrashing system passes it. Not built.

### The soak has found three checker defects, and all three were the checker wrong about a correct node

- P3: the silent step-down (a leader leaving office without sending was never counted as leaving).
- P3: the in-flight grant (a grant counted for a term the candidate had already left).
- **P4: the barrier-release message.** The first soak with logs failed log matching on seed 4: "n1
  sent entry 26 of term 1 that its log does not hold". n1 composed an AppendEntries carrying 26,
  which the barrier held behind its write of 26; it then learnt of term 2 and truncated 26; the
  write completed and the barrier released the old message. The message was true when composed. The
  checker (P4-01) judged it against the sender's log at release. Fixed: the simulator records the
  step that emitted each send, and the checker judges an entry against the log as of that step.

Three of three in one direction. A checker that is wrong this way sends the reader hunting in Raft
for a bug that is not there, and the cost is paid in the node's code, not the checker's. Each was
found only because the soak runs at a scale no hand-built trace reaches.

## Done criteria

| Criterion (spec §11 phase 4, and the breakdown's additions) | Where it is shown |
|---|---|
| Invariants 2, 3, 4, 5, 6, 7 and 10 hold | P4-01's checkers, proven on hand-built traces (each rejects a violating trace and accepts its twin). In simulation: P4-03's replication runs, P4-05's crash runs, and the soak (below): no violation over 10,000 executions |
| The election restriction is re-tested with non-empty logs | P4-04: the vote rule as a unit test; Figure 8 on three nodes, exact (`ManualCluster`), with a batch of 1; a stale candidate refused. Under the commit-by-counting sabotage, invariant 7 goes red at the step where the old entry reaches a majority |
| A crash-during-write test passes | P4-02: `EntryLogTests`, 3 loss modes × 200 seeds × 3 crash cycles, 0 failures; recovery at every offset of a torn record |
| Committed entries survive any crash schedule, all nodes at once included | P4-05: 100 generated runs, half crashing every node at once and half a majority, entries committed before every crash, 31 crashes catching a log write in flight; 50 constructed runs crashing every node one tick after a commit claim; all green |
| Invariant 11 gains its commit clause | P4-06: `CommitLiveness`, by ghost id (an old entry with the same bytes and a new leader's no-op do not count, S-live-1/2). In the soak: checked in 9,297 of 10,000, time to a committed command after the suffix at most 1,007 (window 3,000) |
| Invariants 1, 8, 9 and 11 keep holding over 10,000 executions, with logs, and the distribution is reported | P4-07: local soak, seeds 1..10,000, three clients retrying on timeout, faults until 12,000, run to 22,000: no violation of invariants 1–11 (membership aside), 27,880 elections, 1,649,740 entries committed in fact (fewest 92 in one execution). Distribution below |
| The positive control reaches Leader Completeness | P4-08, **partly**: a lost fsynced log record turns invariant 6 red, naming n2's disk, in all 85 runs where n2 followed (its record completed the quorum), none where it led. Leader Completeness went red in 0 of 200: n2 cannot be re-elected without the entry. The twin is green in all 200 |
| Every configured limit exercised at its largest legitimate value (spec §10) | P4-09: `LimitTests`: a full batch of 64 commands of 1 MiB replicates in one AppendEntries; a command of exactly `MaxCommandBytes` is appended and one byte more refused; each option at its bound accepted and one past refused; the timing bounds hold with a lost heartbeat; K's comparison is inclusive |

## The distribution (10,000 executions, local soak)

Floor 100 (1% of 10,000, at least 3). No declarations remain: writes completed out of order at a
crash, declared at P3 acceptance at 8 of 10,000, reached 165 (1.65%), and `clock-rate-diverged`,
now the node's perceived time, is at 57.6%, no longer near-always.

| Effect | Count | Rate |
|---|---:|---:|
| follower caught up by backtracking | 6,981 | 69.81% |
| conflicting suffix truncated | 2,124 | 21.24% |
| leader crashed with uncommitted entries | 1,830 | 18.3% |
| entry committed in a later term | 1,075 | 10.75% |
| command retried and duplicated | 1,852 | 18.52% |
| crash with a log write in flight | 3,832 | 38.32% |
| writes completed out of order at crash | 165 | 1.65% |
| clock rate diverged (perceived time) | 5,764 | 57.64% |
| node isolated for a timeout | 1,076 | 10.76% |
| split vote | 7,508 | 75.08% |

The election and fault effects are in the soak report; each cleared the floor.

## Predictions

**Evidence 12, forcing 0** (P4-07's cost half, forced, is counted inside its evidence outcome and
named there). Right 2, partly 6, wrong 4.

| Task | Outcome | In one line |
|---|---|---|
| P4-01 | right | a bytes-keyed invariant 5 accepts the byte-equal overwrite; ghost ids reject it |
| P4-02 | partly | the index-free format failed (60); indices alone also failed (3), fixed by chaining on the previous term |
| P4-03 | wrong | one entry per rejection took 1,033 ticks for 3,008 entries; a rejection hint was needed |
| P4-04 | partly | invariant 7 red at the step, as predicted; the "fewer than half" half had no sample (the construction is exact) |
| P4-05 | wrong | acknowledging early shows on invariant 7 in every run, never on invariant 6 |
| P4-06 | wrong | the commit clause fails only where the cut-off node led, rule on or off; it never sees the churn |
| P4-07 | partly | the distribution half right (1.65%); the cost half wrong (about 14×), then forced to about 3× by a client pause |
| P4-08 | partly | invariant 6 red wherever the lost record completed the quorum; Leader Completeness never |
| P4-09 | wrong | the codec caps nothing; two limits did not exist at all |
| P4-10 | right | fails on exactly `2f9b8bc` (weak: known from the red run) |
| P4-11 | partly | the old count check passes a dropped test file and the new gate names it; Core fell to 8–10 s and every shard fits, but shard 3 took 12.6 min against a predicted 12 |
| P4-12 | partly | every GitHub shard under the ceiling, the slowest 12.75 min against a predicted 12; its own prediction commit failed shard 3, as predicted |

## What exists

| Task | Delivered | Sabotages |
|---|---|---|
| P4-01 | Ghost ids by creation and provenance, commitment in fact from durable observations, incremental checkers for invariants 2–7 and 10 | S-loginv-1..7, S-ghost-1 |
| P4-02 | `EntryLog`: chained records (index, term, previous term, command, checksum), later-index-wins, torn tail cut | S-logfile-1..3 |
| P4-03 | Replication in `RaftNode`, tests first: consistency check, conflict-only truncation, rejection hint, commit by majority in the current term, apply, client answers after apply, no-op; `Raft.Kv` | S-repl-1..8 |
| P4-04 | `ManualCluster`; the election restriction and Figure 8 | S-commit-1, S-commit-2, S-restrict-1 |
| P4-05 | `DurabilityTests`: generated and constructed crash-all/majority runs; the leader's persist-before-send as a unit test | S-dur-1..3 |
| P4-06 | `CommitLiveness` (clause and continuity); P3-06's construction with clients | S-live-1, S-live-2, S-disrupt-3 (wrong-reason by design: records that the clause is blind to the churn) |
| P4-07 | The soak with logs and clients; replication effects; perceived-time skew; `SentObservation.Step`; client retry and think time (`IClientWorkload.Retry`, `ClientCall.After`) | S-soak-4, S-soak-5, S-cov-10, S-repl-9 |
| P4-08 | The lost-log-record control and its twin; the lost-vote control with clients | S-pos-4, S-pos-5 |
| P4-09 | `RaftOptions.MaxCommandBytes`, `RaftOptions.Refusal`, `LimitTests` | S-limit-1, S-limit-2 |
| P4-10 | A report commit that is not its push's head fails `gates each-commit` | S-each-5, S-each-6 |
| P4-11 | `tests/Raft.Scale.Tests` (the simulation-scale tests, the checkers linked from `Raft.Core.Tests`); `gates testcount` requires every written test to run in its project, none outside a project, every harness target in its named project; `gates patches` checks every patch with `git apply` | S-ran-1..3, S-patch-1 |
| P4-12 | A test entry builds its target project, not the solution, for patch, revert and control; baseline checks once per shard, each target project on one worker, filtered to the shard's targets | S-harness-1, S-harness-2 |

**Sabotage entries:** 200 (156 at the start of the phase): 195 run by the harness in 7 shards (6
before), 5 by `scripts/host-sabotages.sh`. Eight patches went stale on edits made this phase and
were regenerated with their mechanisms unchanged; five of them (S-disk-3, S-sim-2, S-loginv-7,
S-soak-3, S-iface-2) had passed my `patch`-based check and were fixed in the commits that broke
them (findings; `gates patches` now checks every patch with `git apply` on every commit). S-shrink-5's
patch used a heartbeat P4-09 now refuses and was changed to a legal one. Fourteen entries were
retargeted to `Raft.Scale.Tests` after the split. Every entry gave its expected result on the head's
full local run.

**Test projects** (at the start of phase 4, `9c9e170`, in brackets): Architecture 42 (40), Checker
130 (130), Gates 108 (100), Simulation 106 (105), Core 98 (59), Scale 11 (new at P4-11; its tests
were in Core).

## Deviations and choices for the reviewer

- **The log record carries the previous entry's term** (decision 4 said index, term, command):
  indices alone recovered two logs that never coexisted (P4-02).
- **The P4-09 bounds** (approved): heartbeat at most a third of the minimum timeout, a timeout
  spread of at least one heartbeat, `MaxCommandBytes` 1 MiB with `too-large|<max>`, a batch of at
  least 1, and a largest command a full batch can encode.
- **The soak's workload and length:** clients pause 100 ticks between operations and runs go to
  22,000 ticks (findings: the cost of an unpaused workload; state-placed crashes now firing).
- **Two simulator interfaces widened:** `IClientWorkload.Retry` (a default method; workloads that
  do not retry are unchanged) and `ClientCall.After`; `SentObservation.Step`. The golden trace
  changed by one line (the skew fault's detail now carries its rate).
- **P4-06's constructed seeds where the cut-off node led are counted, not asserted** (approved;
  spec §2 now says so with the number).
- **A new test project, `Raft.Scale.Tests`** (P4-11, option A at the ceiling). The checkers stay in
  `Raft.Core.Tests`, where they are proven on hand-built traces, and are compiled into it by link.
- **The working agreement amended** (reviewer, at the end of the phase): the head's full run locally
  before a push, per-commit verification in CI, required before merge. `gates reports` stated as a
  known residual there: it has no local check (it needs the GitHub API).
- **P4-12 brought forward from phase 5** (reviewer, at the ceiling), as its own task with its
  prediction and sabotages; the P5 register row is closed and phase 5's breakdown no longer carries it.
- **Pre-P4-11 per-commit jobs accepted as unverifiable in CI, not exempted** (reviewer, at the
  ceiling): an exemption on permanent commits never expires. The ceiling stays at 15 minutes.

## CI and its cost

- **Local, before this push: the head's full run, all green** (the amended agreement), on `9f8f28c`,
  the commit before this report: preflight, build, gates (breakdown, trailers, register, patches),
  tests (all six projects, every written test executed), all 7 harness shards, the soak at 10,000,
  host sabotages, secret scan, README walk. 65 minutes end to end.
  - **Shards:** 7.9, 6.4, 7.2, 4.8, 7.7, 5.1 and 8.3 minutes (ceiling 15); on GitHub the same shards took about 1.5 times as long. Before P4-12, on the P4-11 commit: 9.4, about
    11.1, 12.6, 8.9, 10.7, 10.2 and 9.6.
  - **Soak:** about 10 minutes for 10,000 executions in the SDK container.
  - **After the run** only this report, `AGENTS.md` and P4-12's outcome line (its local half, from this run) changed. The sabotages that patch
    documentation (S-bd-1/2, S-readme-1/2, S-reg-1/2, S-trace-1/2), the gates, the report-at-head
    rule, the host checks and the README walk were run again on the final head.
  - `gates reports` is not in the local run and cannot be (AGENTS.md, known residual).
- **GitHub, the machine that enforces the ceiling, about 1.4 to 1.5 times slower than the SDK container
  locally:**
  - Run [36779855011](https://github.com/nooree00/raft-sim/actions/runs/36779855011) (head `4006cc9`,
    the first submission of this report): red. The head's harness shard 3 took 15.8 min; per-commit
    jobs at P4-03..P4-11 failed the ceiling or were cancelled at 40 minutes (below).
  - Run [36810331470](https://github.com/nooree00/raft-sim/actions/runs/36810331470) (head `5783cd5`,
    P4-12): every head shard green (11.9, 10.0, 8.0, 5.7, 8.7, 7.3, 12.7 min), soak, secrets and README
    walk green. Red for two expected reasons: `gates reports` failed because this report, then at
    `4006cc9`, certified a commit whose run was red; and `50f1720` shard 3 failed the ceiling, as P4-12
    predicted. This commit carries the report again, at the push head, so the push's run certifies it.
- **Per-commit verification is CI's** (the amended agreement); the commits it could not verify are
  listed in the next section.
- **What waiting cost this phase** (your question at approval): the head's full local run was 95
  minutes on the P4-11 commit and 65 after P4-12. Before P4-11 it could not pass at all.
  Two GitHub runs were red at the ceiling; the first lost the per-commit matrix for twelve commits,
  for good. Earlier P4 work lost time to container restarts (the Docker daemon had to be restarted
  several times) and to runs killed by the session's time limit, resumed stage by stage.
- **This push's run** is the certifying run for this report, checked by `gates reports` on the next
  push.

## Commits never verified in CI

Run [36779855011](https://github.com/nooree00/raft-sim/actions/runs/36779855011) (head `4006cc9`)
checked each commit of the phase-4 push with that commit's own tooling, and run
[36810331470](https://github.com/nooree00/raft-sim/actions/runs/36810331470) (head `5783cd5`) did the
same for `50f1720`. Each listed job either failed
the harness's 15-minute ceiling or was cancelled at the job's 40-minute timeout. Three failed jobs were
read in full (the head's shard 3, `6e663e3` shard 4, `a36bafb` shard 1); in each, every entry gave its
expected result and the only failure was the ceiling. The others were not read one by one; their
logs are in the run. **No other run will check these jobs:** the commits are pushed, the per-commit
matrix covers only a push's new commits, and the reviewer chose (at the ceiling) to accept them as
unverifiable in CI rather than exempt them, since an exemption on permanent commits never expires.

| Commit | What it is | Jobs with no passing verdict |
|---|---|---|
| `9b2e42f`, `dc27294`, `0d4f401`, `f793fe8` | P4 approval, P4-10, P4-01, P4-02 | none: verified |
| `903daf1` | P4-03 replication | shard 3: **cancelled at 40 min, no verdict** |
| `325e7e8` | P4-04 election restriction | shard 3: **cancelled at 40 min, no verdict** |
| `a0f212c` | P4-05 durability | shard 3: **cancelled at 40 min, no verdict**; shard 7 over the ceiling |
| `b5e7ba4` | P4-06 commit clause | shard 3: **cancelled at 40 min, no verdict**; shards 2, 5, 7 over the ceiling |
| `6e663e3` | P4-07 soak with logs | shards 2, 3, 4, 5, 7 over the ceiling |
| `56d926e` | P4-08 positive control | shards 1, 2, 3, 4, 6, 7 over the ceiling |
| `a36bafb` | P4-09 limits | shards 1, 2, 3, 4, 5, 7 over the ceiling |
| `307a671` | P4 outcomes (docs) | all 7 shards over the ceiling |
| `2b9495d` | working-agreement amendment (docs) | shards 1, 2, 3, 4, 7 over the ceiling |
| `4576c89` | P4-11 prediction (docs) | all 7 shards over the ceiling |
| `d8c3534` | P4-11 split | none: verified |
| `0bb3372` | P4-11 outcome (docs) | shard 3 over the ceiling |
| `4006cc9` | P4 report (the head) | harness shard 3 over the ceiling (15.8 min) |
| `50f1720` | P4-12 prediction (docs, next push) | shard 3 over the ceiling (its checks step about 16.5 min), in run [36810331470](https://github.com/nooree00/raft-sim/actions/runs/36810331470), as P4-12 predicted; shards 1, 2, 4–7 verified |

Shard 1 of every commit also runs that commit's preflight, build, gates and tests; where shard 1
failed, those ran but the job's verdict is the harness's. This is S-rare-1's class returning: a
mechanism verified at the head (every entry ran green on the full local run of `d8c3534`, and on the
GitHub head shards other than 3) but not at its own commit, for this bounded, named set.

## Findings added this phase

In `docs/findings.md` under Phase 4: the chained log record; the skew measure that measured the
protocol; the deposed leader truncating before persisting its term; the crash tests that cannot see
a leader sending before persisting; the early acknowledgement as a spurious claim; the leader that
can send but not hear; the commit clause blind to churn; the checker judging at release; the soak's
cost with unpaused clients; the stable suffix moved by state-placed crashes; skew as perceived
time; the stale-term acknowledgement no test saw; the limits that did not exist; the default batch
hiding Figure 8; liveness as a deadline, not a rate; and a check made with a different tool from the
one that enforces it, with the sweep for other instances (one more found: my local script's
hard-coded shard count); and a tooling-cost fix belonging at the start of a phase and measured on
the machine that enforces, with what its lateness cost here (the section above).

## Still the person's

- The soak as a required check in branch protection.
- The cold walk of the README for this phase (not scripted).
- The sabotage branch.
