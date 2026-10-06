# Phase 9 — report

**Status: accepted** (reviewer, on run 37543529337, the report's head). Edits at acceptance, by the
reviewer's direction:
- the headline is the control's rate under real timing, put first with its number, and what the
  `compose` job's green is good for is stated beside it;
- the non-root failure is recorded as unexplained, not fixed, with a register row;
- two findings added: a guard dismissed as a flake (the second instance), and an oracle's time
  resolution as part of its correctness;
- session expiry's cost is recorded as unmeasured, a phase-10 register row;
- the four commits never verified are accepted as such, consistent with phase 7.

Decisions at acceptance: the touched-file stage is working, and its fraction stays in every report;
the shard size (25, 12 shards) is approved, subject to measuring it against cost-balanced assignment
first, since the cause found is distribution by id order, which changing the size only reshuffles
(phase 10's breakdown).

Real sockets, multi-process (spec §11 phase 9): P9-00 to P9-10, as approved, in the breakdown's
order. One sabotage was added beyond the breakdown (S-compose-2, P9-08). Four defects were found
after the tasks' own commits. Three are understood and fixed in a commit of this phase; one is not
understood:
- two found by the local run before a push: the kill stamped on the wrong side of the kill (P9-08),
  and a test port taken between its choice and its bind (P9-05);
- one found by CI as an intermittent test failure, then reproduced locally on fewer CPUs: a
  sequential client overlapping itself at the history's microsecond resolution (P9-06);
- **one unexplained:** the README walk and the host sabotages failed on GitHub's non-root runner on
  the phase's first push, with no readable log. A guess was applied (P9-08) and both have passed
  since. That is not evidence the guess was right. A register row records it.

Each is under "What the phase found".

This report certifies the commit that contains it. `gates reports` checks that commit's CI run on
the next push.

## The result of the phase

### Under real timing, the control was caught once in ten runs; the construction catches it every time

The positive control (decision 8, P9-07) is a leader that answers writes when it appends them
(`RaftOptions.AnswerAtAppend`): it loses an answered write when it dies holding one no follower has.
- **In the in-process construction**, which holds the leader's frames before the kill, the history
  is rejected on every run, and the real host in the same steps is accepted.
- **In ten Compose runs under real timing**, each killing the leader under three clients' load, the
  control was caught **once** (run 2, key k1). Every guard held in all ten, so each run did kill a
  leader that had been answering at append. The window, a leader dying with an answered write that
  no follower has, opened in one run.

So, for this class of bug (one that needs a narrow window in timing), a real-process run is about a
tenth as strong as the construction. That bounds what the `compose` job verifies. **A green
`compose` job is a weak signal for timing bugs.** What it is good for:
- **wiring:** the image builds, the three nodes and the client start, find each other by name, and
  talk over the two networks;
- **start-up and restart:** an election with nobody calling anything, and recovery of a killed node
  from its volume;
- **real sockets:** the framed transport and the client's line protocol, end to end;
- **the host's own code paths:** the clock, the disk executor on a real file system, the redial, the
  event log, none of which the simulator runs.

It is not crash-safety coverage: SIGKILL keeps the page cache (below), and timing bugs are left to
the constructions and the simulator.

### Three processes, a real client, a killed leader, the checker green

`scripts/compose-run.sh` starts three node containers and a client container on the CI-pinned SDK
image. The client runs three logical clients in sessions over six keys for 40 seconds. Ten seconds
in, the script kills the leader with SIGKILL, waits for another node to lead, and restarts the
killed node from its volume. It then runs `tools/Raft.Check` on the client's history and the nodes'
events. Before any verdict counts, the check confirms each of these (P9-08):
- the killed node was the leader;
- a write completed before the kill and another after it;
- an operation spanned the kill;
- the restarted node applied entries past the last one it had applied before the kill.

WGL must then accept the history. On GitHub the `compose` job is required by `gates build-collect`
and passed in each of the phase's three runs, taking 87 to 94 seconds. Locally, every run since the
kill-stamp fix has been accepted, with 36,000 to 39,000 operations and the hardest key at 37,000 to
39,000 states.

**What a green Compose run does not say (decision 7, disclosed at approval).** SIGKILL keeps the
operating system's page cache, so a write the host never synced survives the kill. Phase 9's crash
testing is therefore weaker than the simulator's: the simulator's disk loses unsynced writes at a
crash and can lose writes the node was told were durable. A green run says the host recovers from
what its files hold after its process dies. It says nothing about what a power cut leaves in them.
Two things would test that:
- **Machine-level power loss:** a virtual machine whose power is cut, or a block device that drops
  its write cache (`dm-log-writes`, `dm-flakey`). Not reachable here, and stated as a limit.
- **fsync failure injection:** the real host's disk executor run against its file-system interface,
  with a sync that fails and a crash that discards every write after the last successful sync.
  Reachable, and opened as a register row for phase 10.

### The session table's limit, with expiry (decision 5, P9-01)

At the bound of 1,000,000 sessions the snapshot is 36,000,017 bytes, **36.0 bytes per session**.
Phase 8 measured 28 bytes; recording each session's last use adds 8. The 1,000,001st `Register`
evicts the least recently used session and is answered. Every node evicts the same session, because
the log decides the order. In the scale sample, at a bound of 2 for 3 clients, a session was evicted
and its client registered again in every execution, and every checker held.

### What the phase found

Each finding is in `docs/findings.md` under Phase 9. The ones that changed work:

- **Predictions about my own next mistake prevent it and test nothing.** The four forcing outcomes
  (P9-01, 02, 06, 08) each named a mistake in code not yet written. The evidence outcomes were about
  what the code cannot decide: the platform, the scheduler, the workload, a rate.
- **A guard timed on the wrong side of the act it guards.** The kill's time was stamped before
  `docker compose kill`, which takes far longer than one operation. So the attempt in flight at the
  real kill began after the stamp, and "an operation spanned the kill" failed a correct run of
  37,156 operations. An earlier failure of the same guard had been taken for a flake and answered
  with a longer run. It is now stamped after the kill returns. A correct guard firing on a real
  problem, dismissed, then rediscovered later at greater cost: findings, at acceptance.
- **A free port asked of the system is free only until someone else asks.** The host tests' fixture
  released a port and bound it later, and a parallel test's outgoing connection took it in the gap.
  Ports now come from below the ephemeral range. I took it for the README walk's failure on GitHub;
  it was not (next item).
- **A sequential client overlapped itself at the history's resolution** (P9-06). The client stamps
  microseconds, and the checker reads two operations of one client as overlapping unless one's
  response is strictly before the other's invoke. A next invoke within a microsecond of the last
  response made one client look concurrent with itself, and KillTests failed intermittently on
  GitHub, in the README walk and the build job. Once the annotations named it ("client 0 has
  overlapping operations"), it reproduced locally in 1 of 10 runs with the container limited to 1.2
  CPUs: each run had 3 to 10 gaps of exactly 1 µs, and the rare gap of 0 failed. The client now
  stamps its invoke after its last response, moved by at most a microsecond, since that is when it
  happened. Phase 8's finding again: a choice between the client and the checker, here the unit,
  that the breakdown did not name.
- **The first push's two failures on GitHub are unexplained.** The README walk failed with exit 1
  and the host-sabotage step with exit 255, on GitHub's non-root runner. The logs were unreachable
  from here, the annotations carried only the exit codes, and neither failure reproduced locally,
  where everything runs as root. **What was applied is a guess:** root-owned files written by
  containers, which a non-root user cannot remove. So the client container now runs as the invoking
  user, and the host sabotages hand a worktree's root-owned files back before removing it. Both jobs
  have passed on every push since. That shows the failures have not recurred. It does not show the
  guess was right, and the facts do not single it out:
  - `host-sabotages.sh` exits only 0 or 1 itself, under `set -e`. Exit 255 came from a command inside
    it, unknown which, and root ownership does not predict that code.
  - Two causes found later can produce these failures, and both are since fixed for their own
    reasons. The kill stamped before the kill failed S-compose-1's baseline (the Compose run, exit 1),
    which the walk also runs, and the failing baseline is the path that removes a worktree at once.
    The microsecond overlap failed KillTests, which the walk also runs (that failure was exit 2,
    later).

  If either job fails again on GitHub, start from here: the fix was not a diagnosis. The failure is
  now readable: every host-side script prints its failure as `::error::` lines, the walk's in two
  annotations (GitHub keeps ten per step, and the first attempt lost the cause), and a failing test
  project in one, with colour codes stripped (the first version matched nothing). A register row
  records it.
- **Creating a file needs the directory synced, not only renaming one** (P9-03). The simulator never
  modelled the loss, so its crash testing could not have shown it.
- **A positive control whose signature needs real timing is rarely produced by real timing**
  (P9-07): 1 in 10 Compose runs. The construction that opens the window is the check.

## Done criteria

| Criterion (spec §11 phase 9, and the breakdown's additions) | Where it is shown |
|---|---|
| Three processes in Compose, a real client, a killed leader, the checker green | P9-08: `scripts/compose-run.sh`, the `compose` job (required), its guards; S-compose-1 (a follower killed) |
| The checker can fail on a real run | P9-07: the control, red in the construction on every run, 1 in 10 in Compose; S-ctl-2 |
| Touched-file sabotages run before the push (phase 8's acceptance) | P9-00: `gates sabotage --touched`; the plan printed first; S-gate-1 |
| Message corruption on a real transport (register) | P9-02: CRC-32C frames, every single-bit flip refused; S-frame-1 |
| Session expiry (register) | P9-01: LRU by the log, 36 bytes per session at the bound; S-sess-5 |
| A test where nobody calls anything (spec §10) | P9-04: three hosts elect on their own clocks; S-host-1 |
| The largest legitimate size (spec §10) | P9-02: a 67,109,665-byte frame |
| Recovery from real files | P9-03 (directory syncs, S-hostdisk-1), P9-05 (kill and restart in one process, S-kill-1) |
| The README's new path walked | P9-09: the walk runs the Compose block; S-walk-1 |

## Predictions

**Evidence 7, forcing 4.** On evidence: right 1, partly 2, wrong 4. Each outcome is in the breakdown.

| Task | Outcome | In one line |
|---|---|---|
| P9-00 | wrong | 109 of 287 entries over the measured range, not 94: the gate takes every changed file, tests and documents included |
| P9-01 | wrong (forcing) | the retry construction passed first: the prediction named the omission before the code |
| P9-02 | wrong (forcing) | every frame test passed first: the single-read assumption was never written |
| P9-03 | partly | directory sync needs P/Invoke on Linux, as predicted; the first failure was a created file, not a rename |
| P9-04 | right | the term reached 2 in 30 s: a heartbeat late on a loaded machine, the leader kept by the disruption rule |
| P9-05 | wrong | no guard failed: the load is continuous, so something is always in flight at the kill |
| P9-06 | wrong (forcing) | at most 5 attempts of an operation: the pause after a no-leader redirect was in the first client |
| P9-07 | partly | the construction needed one fix of mine; Compose caught the control in 1 of 10, as predicted |
| P9-08 | wrong (forcing) | start-up did not fail: names resolved at every dial was already the design |
| P9-09 | wrong | the walk runs Compose (it is a host job), so the block is walked, not skipped |
| P9-10 | wrong | the Compose job under 10 minutes held; the slowest shard reached 88% of its ceiling, not under 75% |

## What exists

| Task | Delivered | Sabotages |
|---|---|---|
| P9-00 | `gates sabotage --touched <since>` (`--accept-estimate`), `ci/sabotage-touched.txt`, its own ceiling for an accepted plan | S-gate-1 |
| P9-01 | LRU session expiry in `KvStateMachine` (`maxSessions`), the last use in the snapshot | S-sess-5 |
| P9-02 | `Crc32C`, `Frames`: length, message, CRC-32C | S-frame-1 |
| P9-03 | `DiskExecutor`, `IFileSystem`, `DirectoryFileSystem` (directory fsync through `open`/`fsync`/`close`) | S-hostdisk-1 |
| P9-04 | `NodeHost`: one loop thread, a periodic tick, peers redialled by name, the client line protocol, JSON-line events | S-host-1 |
| P9-05 | `Raft.Host.Tests`: `HostCluster`, kill and restart with the checker on the history | S-kill-1 |
| P9-06 | `RealClient`: sessions, redirects, retries, the history as JSON lines from one clock | S-client-4 |
| P9-07 | `RaftOptions.AnswerAtAppend` (off in every real configuration), the construction, `RAFT_ANSWER_AT_APPEND` | S-ctl-2 |
| P9-08 | `compose/`, `scripts/compose-run.sh`, `tools/Raft.Check`, the `compose` CI job, required | S-compose-1, S-compose-2 |
| P9-09 | The README's Compose section, walked; every `sh` block tagged | S-walk-1 |

**Test projects** (at the start of phase 9 in brackets): Architecture 50 (46), Gates 129 (119),
Checker 170 (170), Simulation 110 (110), Core 845 (842), Scale 23 (22), Budget 3 (3), Membership 3
(3), Host.Tests 15 (new).

## Deviations and choices for the reviewer

- **The touched-file stage's one large selection (P9-00, the reviewer's ask to report each).** The
  plans printed, before anything ran, at each push of the phase:
  - **The first push:** 80 of 296 entries (27.0%), estimated 18.3 minutes. It was over the 11-minute
    threshold and was run with `--accept-estimate`. The 80 entries ran in 1,107 seconds, each with
    its expected result.
  - **The five later pushes:** 0 of 296 (0.0%) three times, for the scripts, a test fixture and
    this report; 2 of 296 (0.7%) twice, for `scripts/ci-test.sh` (S-count-2) and the host tests
    (S-kill-1), then the client (S-client-4). The host entries the scripts touch (S-compose-1,
    S-walk-1) run in the host-sabotage stage of the same local run.

  So far the selection discriminates: a push that changes the node takes about a quarter to two
  fifths of the manifest, and one that does not takes almost none.
- **The harness shard size (P9-10), for decision.** *Approved at acceptance (25, 12 shards), subject to
  measuring it first against cost-balanced assignment: phase 10's breakdown.* The slowest shard reached 789 seconds of 900
  (88%), shard 6 in all three runs (749, 774, 789). The longest per-commit job, a build, the tests and a harness share, took 1,023 seconds. With GitHub's run-to-run spread, a
  shard can pass its ceiling. A smaller size in `ci/sabotage-shard-size.txt` (25 instead of 28: 12
  shards instead of 11) would bring the slowest well under. It is a visible decision, and it is not
  made here.
- **`RaftOptions.AnswerAtAppend` is a production option that exists for the positive control**, as
  `CompactPastCommit` and `ReadsWithoutQuorum` do. A separate method in `RaftNode` keeps the real
  answering path unchanged.
- **The history's entries carry the client's wall clock** (`Wall`), for one use only: placing the
  orchestration's kill on the history's timeline in the guards. The checker never reads it, and
  decision 4 holds: real-time order comes from the client's monotonic clock.
- **The test port range.** Host tests bind ports 20,000 to 31,999 on loopback, below Linux's
  ephemeral range. A machine that reserves ports there for something else would make a test skip to
  the next port, not fail.

## The new work's cost in CI (P9-10)

Measured on runs 37504400672 (head `47d6ecd`), 37523419601 (`634c87a`) and 37537300804
(`3722beb`, all green). Every job in the three ran once. The first two each failed jobs unrelated to
cost (the README walk; the host sabotages in the first), and every timed job passed. Compared with
phase 8's certifying run 37393582257.

| | Phase 9 | Phase 8 |
|---|---|---|
| Compose job | 93, 94 and 99 s (step 87 to 94 s) | — |
| Harness shards (11) | steps 340 to 749 s, 407 to 774 s, 271 to 789 s (slowest 88% of 900) | 407 to 652 s (72%) |
| Baseline soak step | 560, 840 and 409 s | 376 s |
| Membership soak step | 680, 836 and 780 s | 607 s |
| Longest per-commit job | 1,023, 832 and 764 s | 802 s |
| Local run before the push | 1,018 s (19 to 25 minutes at GitHub's ratio); 2,187 s with the accepted 80-entry selection | 650 s |

- **The shards cannot be compared one by one with phase 8.** Shards are dealt round-robin from the
  sorted ids, so the nine new entries moved almost every entry to another shard. Shard 6 shares 3 of
  its 27 entries with phase 8's shard 6. Its slowest entries are soak, known-limit and agreement
  checks, the heaviest kinds of entry in the manifest.
- **The soaks:**
  - `47d6ecd` and `634c87a` differ only in scripts, yet the baseline soak took 560 and 840 seconds.
    Most of the spread is the runner. Phase 8 saw 892 against 1,327 seconds for one commit.
  - The third run's baseline soak took 409 seconds, against phase 8's 376, with the same node code
    as the first two. So the rise over phase 8 is mostly the runner too.
  - **The consequence:** 409 to 840 seconds on identical code means session expiry's cost is
    **unmeasured**, not measured as small. Whatever its sorted set costs is inside a spread of a factor
    of two. It is an open register row for phase 10.
- **The manual check** (the comparison made once against a run with a re-run job) was not repeated:
  no phase 9 run had a re-run job. Both runs used were first attempts.
- **The local run:** 1,018 seconds:
  - 6 s for the report rule;
  - 253 s for preflight, build, gates and tests;
  - 758 s for the host sabotages, the secret scan and the README walk, two Compose runs among them;
  - under a second for an empty touched stage.

  Phase 8's was 650 seconds. The growth is the Compose runs.

## Register

- **Closed:** message corruption on a real transport (P9-02) and session expiry (P9-01).
- **Opened:**
  - fsync failure injection against the real host, promised to phase 10 (decision 7 (b)).
    Machine-level power loss is stated as a limit, not promised;
  - at acceptance: the first push's two failures on GitHub, unexplained, phase 10;
  - at acceptance: session expiry's cost, unmeasured, phase 10.

## Findings added this phase

In `docs/findings.md` under Phase 9:
- predictions about my own next mistake are design notes;
- a positive control needing real timing is rare in real timing;
- a created file needs its directory synced;
- an assembly rename hid the host sabotages, and the guard held;
- a shared event log interleaved lines;
- a guard timed on the wrong side of the kill;
- CI failed with no readable log;
- a sequential client overlapped itself at the history's resolution;
- at acceptance: a guard dismissed as a flake, the second instance;
- at acceptance: an oracle's time resolution is part of its correctness;
- a free port asked of the system was taken before it was bound.

## Still the person's

- Adding `soak-membership` and `compose` beside `soak` in branch protection.
- Deleting `prerewrite-b96fc4b`, the probe branches and the sabotage branch.
- The cold walk of the README.
- P0.
- The shard size: approved at acceptance, subject to the measurement in phase 10's breakdown.

## Commits never verified in CI

Four pushed commits never had a green verdict of their own. Each was a push head, so the per-commit
matrix of a later push does not cover it, and each head run was red:

| Commit | Run | Red jobs | Cause |
|---|---|---|---|
| `47d6ecd` | 37504400672 | readme-walk, secrets | unexplained (above); not recurred since `1921551`, which applied a guess |
| `634c87a` | 37523419601 | readme-walk | KillTests, the client's microsecond overlap, fixed at `3722beb` |
| `430cb27` | 37529394413 | readme-walk, build-core | the same |
| `3eff477` | 37532836857 | build-core | the same |

Every other pushed commit of the phase has a passing verdict for every (commit, shard): the
per-commit matrices of those runs, and run 37537300804 for `df201c6` and its head `3722beb`. Every
commit above is contained in `3722beb`, whose run is green in full. That does not make each of them
green on its own: a commit that failed a test intermittently would fail it again, and that is the
case for the last three. **Accepted as never verified** (reviewer, at acceptance), consistent with
phase 7: they are pushed and fixed forward, three would fail intermittently anyway, and rewriting them would force-push, after which `each-commit-list` refuses by design: it cannot
find the previous head, and since phase 6 (the register row done there) it fails rather than
widening its range.
