# Phase 10 — task breakdown

Measurement (spec §11 phase 10). Format as in `docs/phases/P9/breakdown.md`, parsed by `gates
breakdown`; outcomes will say `(evidence)` or `(forcing)`.

**Done when** (spec §11): throughput and latency against a written target, with the build
configuration recorded beside every number. In addition, carried forward:

- phase 9's acceptance, the shard size: 25 (12 shards) is approved, **but** measured first against
  cost-balanced assignment. The cause found was distribution by id order, and changing the size
  only reshuffles it (P10-00, measured before this breakdown; the numbers are in the task);
- the register rows promised to phase 10:
  - disk acknowledgements as inputs to Core, "decide, with measurements" (decision 6);
  - the visualiser, optional (decision 10);
  - fsync failure injection against the real host (decision 7);
  - the first push of phase 9's unexplained failures on GitHub (decision 9);
  - session expiry's cost, unmeasured (decision 8);
- phase 8's finding, and phase 9's at acceptance: before accepting an expensive measurement as a
  property of the system, ask which part of it is a representation choice, and an oracle's time
  resolution is part of its correctness. A latency figure is a history with a clock. Every choice
  between the load and the number is a decision below, not a default: whether the load waits for
  replies, where the clock starts, its resolution, what is discarded as warm-up, and how a
  percentile is computed;
- phase 9's acceptance: under real timing the control was caught once in ten runs. A measurement
  run in real processes is subject to the same thing: a number it produces is a sample of one
  machine's timing, not a property of the code, until its spread is known;
- phase 9's soaks: the same code took 409 to 840 s on GitHub. No number in this phase is compared
  across runners, and none is gated in CI;
- spec §10: every configured limit at its largest legitimate size, and one test where nobody calls
  anything; a rule tied to a mechanical act holds, a rule naming something to be careful about does
  not. "The build configuration recorded beside every number" is made mechanical (P10-01).

**Status: for review.** Nothing is implemented. P10-00's measurement was made before this breakdown, at the reviewer's request; its numbers are in decision 1, and its change waits for approval like every other.

## Ordering

The shard change first, because every push of the phase goes through the harness (P10-00). Then the
measurement's own machinery, before any number exists: the record format and its gate (P10-01),
and the written target, derived from a model whose inputs are measured separately (P10-02). Then
the load generator and the controls that prove it measures what it claims (P10-03), and the
measurements against the target (P10-04). Then the register rows that use the measurements: the
persist barrier's cost and the disk-acknowledgement decision (P10-05), session expiry's cost
(P10-06). Then the rows that do not: fsync failure injection (P10-07), the unexplained failures
(P10-08), the visualiser decision (P10-09). Then the cost in CI (P10-10).

**Blocking set:** P10-00 to P10-04 (the done criterion), P10-05 and P10-07 (register rows that a
completed phase cannot keep open). P10-06, P10-08 and P10-09 block nothing but their rows. P10-10
blocks nothing.

## Decisions for review

1. **Shard assignment: cost-balanced, at 11 shards, not round-robin at 25** (P10-00, measured).
   - **The measurement.** All 11 shards were run locally at `e48b7cd`, 4 workers each, as one
     sample. That gives every entry's cost (296 entries, 10,207 s in all) and every baseline unit's.
     A model of a shard's time was then fitted: the slowest worker's ready time (its baseline build
     plus its baseline units), then the slowest worker's entries, after the two-way barrier. It
     lands within 9% of the measured time on all 11 shards (from 8.5% under to 6% over).
   - **What the model says, slowest shard in local seconds:**

     | Assignment | Shards | Slowest | Fastest |
     |---|---|---|---|
     | Round-robin by id (today) | 11 | 520 (measured 544) | 312 |
     | Round-robin by id (size 25) | 12 | 519 | 297 |
     | Round-robin by id | 13 | 520 | 263 |
     | Cost-balanced across shards | 11 | 406 | 347 |
     | Cost-balanced across shards and workers | 11 | 336 | 321 |
     | Cost-balanced across shards and workers | 9 | 375 | 364 |

   - **Size 25 does not help.** The slowest shard is the one that happens to collect several of the
     costliest entries (`S-lin-7` 247 s, `S-kl-3` 200 s, `S-cov-7` 196 s, `S-codec-3` 195 s), and
     another deal by id collects a different such set. A 12th shard adds a job and its fixed cost
     (about 64 s of baseline build per worker) and leaves the maximum where it was.
   - **The worker deal matters as much as the shard deal.** Balancing shards alone takes the slowest
     to 406 s. Balancing the four workers inside each shard as well takes it to 336 s, because a
     shard's time is its slowest worker's.
   - **Robust to stale costs.** The costs come from one run, and runners differ. So the plan made
     from these costs was scored again under random errors in every entry's cost, 200 trials:
     with each cost off by up to ±50%, it has a median slowest shard of 366 s (95th percentile
     433 s), against round-robin's 554 s (636 s).
   - **Recommended:** cost-balanced across shards and workers, at 11 shards. The costs live in
     `ci/sabotage-costs.txt`, one line per entry, and a new entry adds its cost in the commit that
     adds it, from its own `--only` run (as a changed test count edits `ci/test-baseline.txt`). 9
     shards would also be faster than today, but the job count is left alone, so the gain is room
     under the ceiling, not fewer jobs. Taking 25 because it is the available knob is not
     recommended. The data and the model are in `docs/phases/P10/p10-00/`.
2. **The target is written before any end-to-end number, from a model** (P10-02). An absolute target
   borrowed from elsewhere (another system's published numbers) describes other hardware. The
   target is instead what this design should achieve on the machine it runs on: a commit costs the
   leader's sync, then a round trip, then a follower's sync (the persist barrier, spec §4, orders
   them). The model's inputs are measured alone first: the data volume's sync latency, and the
   loopback round trip, both distributions, not single figures. **Target:** at an offered write
   load of half the model's capacity, the median commit latency is within 1.5 times the model's, and
   the 99th percentile within 3 times; the highest offered rate the host sustains (completed equals
   offered within 5%, the 99th percentile under 100 ms) is at least half the model's capacity. The
   inputs are measured and the numbers filled in before P10-03 starts, and then not changed.
   **Disclosure:** the target is not written blind. Phase 9's Compose runs completed about 900
   operations a second with three closed-loop clients (37,000 operations in 40 s). The model, not
   that figure, sets the target.
3. **Open-loop load, latency from the intended start** (P10-03). A client that waits for each reply
   before sending the next (closed loop, as `RealClient` does) stops sending while the system
   stalls, so a stall delays one request per client and vanishes from the percentiles (coordinated
   omission). The load generator sends at a fixed offered rate on a schedule, and each latency runs
   from the scheduled time, not the time it was sent. Every sample is kept (no histogram library,
   no dependency); percentiles are read from the sorted samples, stated with the count. The clock is
   the client's `Stopwatch` in ticks (phase 9's finding: microseconds are coarser than a loopback
   round trip). A fixed warm-up (the first 5 seconds) is discarded and stated. Throughput is
   completed writes a second at each offered rate; the curve, not one point, is reported.
4. **Where numbers come from, and what is compared.** Each measurement runs on one named machine,
   repeated (five times unless stated), and reports the median with the spread across repetitions.
   A comparison (before and after, A against B) is made only within one machine and one session,
   with the two sides interleaved. Two machines: this container (in process and in Compose) and,
   for the record only, a GitHub runner. **No performance number is gated in CI:** phase 9 measured
   a factor of two between runners on identical code.
5. **The configuration beside every number is produced, not typed** (P10-01). The measuring tool
   writes each result as a record under `measurements/`, with its configuration filled in by the
   tool: commit, build configuration (Release or Debug), SDK and runtime versions, image digest,
   operating system and kernel, CPU model and count, container CPU limit, GC mode, tiered
   compilation and PGO settings, the data directory's file system, the offered load, the
   repetition, and the warm-up. `gates measurements` refuses a record missing any field, and refuses
   a number in a phase-10 report table that does not cite a record id. Everything measured is
   Release; the tests stay Debug, and a Debug number, if quoted, says so in its record.
6. **The disk-acknowledgement row is decided by measuring the barrier** (P10-05). Today a `Send` is
   released only after every `Persist` before it is durable (spec §4), so a leader's append reaches
   its followers one sync late. The paper allows the leader to write its log in parallel with
   replicating (§10.2.1), which needs either acknowledgements as inputs or a looser barrier. The
   measurement: in the real host at load, the time sends wait behind persists, as a share of commit
   latency. Recommended outcome rule: if the barrier accounts for under a quarter of median commit
   latency at the target load, the row is closed as "kept, with the measurement"; if more, the
   change is brought to the reviewer as a spec amendment, not made in this phase.
7. **fsync failure is fail-stop** (P10-07). A sync that fails leaves the file's state unknown (the
   PostgreSQL fsync episode: a retried sync can report success over lost pages). The host stops at
   once on a failed sync and acknowledges nothing after it; on restart it recovers from what the
   files hold. Two injections through P9-03's `IFileSystem`: a sync that throws, and a crash that
   discards every write after the last successful sync of each file. The checker runs on the
   history of each.
8. **Session expiry's cost, measured within one machine** (P10-06). The same soak executions run at
   the phase-9 head and at phase 8's head (before the last-use set), interleaved, five times each,
   in one session. The difference with its spread is the cost. No production switch is added to
   turn the set off.
9. **The unexplained failures need a non-root reproduction, and that needs the person** (P10-08).
   Locally everything runs as root. The way tried in phase 9, a host user given the session's proxy
   certificate and credentials, was refused by this session's safety checks, and correctly: it
   copies credentials to a new account. Proposed instead: the host-side scripts run inside a
   container as an unprivileged user, with the Docker socket and the proxy certificate mounted
   read-only, as `scripts/in-sdk.sh` already mounts them for the SDK container. **This needs the
   person's approval**, because it gives a container the Docker socket. Without it, the row is
   re-promised and the failure stays unexplained.
10. **The visualiser: dropped, by a spec amendment** (P10-09), unless the reviewer wants it. It is
    optional (spec §9). In nine phases no finding names a failure that a timeline would have
    diagnosed sooner than the shrinker's minimal schedule and the trace did. Dropping it needs the
    spec changed in the commit that closes the row (the register's rule).

## Tasks

### P10-00 — Harness shard assignment

- **Task:** Decision 1. `ShardPlan` assigns entries to shards by cost, not by id: longest first, each to the shard whose modelled time grows least, where a shard's time is its slowest worker's ready time (baseline build plus its baseline units) plus its slowest worker's entries. Inside a shard, entries go to workers the same way, longest first to the least-loaded worker. Ties break by id, so the plan stays deterministic. The costs come from `ci/sabotage-costs.txt` (seeded from `docs/phases/P10/p10-00/entry-costs.txt`), and the per-commit matrix computes each commit's shards from that commit's own file. `gates sabotage-plan` refuses a manifest entry with no cost, and a cost for an entry that does not exist.
- **Vacuity:** An assignment that leaves an entry out, or puts one in two shards, passes a shard that never ran it. Guarded by `ShardPlan.Problems` (every id in exactly one shard, no shard empty) for any assignment, and by `gates build-collect` requiring every shard. An assignment that reads no costs falls back to an order unrelated to them, and every shard still passes. Guarded by a gate test in which two planted heavy entries must land in different shards (S-shard-4, the costs ignored). A new entry with no cost would get one silently. Guarded by the refusal (S-shard-5, the refusal removed).
- **Sabotage:** S-shard-4, S-shard-5
- **Verifiable here:** partial — the assignment and its balance locally; the ceiling that matters on GitHub's runners only
- **Prediction:** On GitHub, the slowest shard's harness step falls under 600 s (two thirds of the 900-s ceiling), and the slowest is within 1.3 times the fastest, against phase 9's 271 to 789 s. The model puts the slowest at 336 local seconds, and phase 9's shard 6 ran 1.45 times slower on GitHub than locally (789 against 544), which gives about 490 s. The 600-s bound leaves room for GitHub's spread. **Observable:** each shard's harness step in the first CI run whose every job completed once.
- **Outcome:** pending

### P10-01 — Measurement records, and the configuration beside every number

- **Task:** Decision 5. A record format under `measurements/` (one JSON file per run: the configuration, written by the tool, and the samples' summary), the tool that writes it (the load generator's output, P10-03, and the model inputs', P10-02), and `gates measurements`: every record has every configuration field, non-empty; every number in a table of a `docs/phases/P10+/report.md` cites a record id that exists. The gate runs in the build job.
- **Vacuity:** A gate that reads no records passes an empty directory. Guarded by requiring at least one record once any phase-10 report exists, and by the sabotages.
- **Sabotage:** S-meas-1, S-meas-2
- **Verifiable here:** yes — the gate and its tests run locally
- **Prediction:** The first records written by the tool fail the gate on the container CPU limit: .NET reports the host's processor count, not the container's quota, unless the limit is read from the cgroup, and my first writer will take `Environment.ProcessorCount`. **Observable:** the gate's verdict on the first record written inside a container started with `--cpus`.
- **Outcome:** pending

### P10-02 — The written target, from a model with measured inputs

- **Task:** Decision 2. Measure the model's inputs alone, in Release, as records: the data volume's sync latency (a file appended in the host's frame size and synced, 10,000 times, on the Compose volume and in the in-process temp directory), and the loopback round trip (a framed message and its reply over the host's transport, 10,000 times). Write the model and the target into `docs/design/performance-target.md`, with the inputs' records cited, before P10-03 starts; the target's numbers are not changed afterwards.
- **Vacuity:** A target derived from the measured system's own end-to-end numbers is a description, not a target. Guarded by the order: the target's commit precedes the first end-to-end record, and `gates trailers` (each task's first commit descends from its prediction) holds P10-03 behind it.
- **Sabotage:** ; manual: the target document is checked against the record ids it cites, and the first end-to-end record's commit is shown to descend from the target's
- **Verifiable here:** partial — the inputs locally; a GitHub runner's for the record only
- **Prediction:** The data volume's median sync is under 1 ms on this machine, and its 99th percentile more than ten times the median. Phase 9's 900 operations a second with three closed-loop clients, about four in five of them writes each needing a leader sync and a follower sync in series, are only possible if a sync takes well under a millisecond. **Observable:** the sync record's median and 99th percentile.
- **Outcome:** pending

### P10-03 — The open-loop load generator, and the controls that prove it measures

- **Task:** Decision 3. A `bench` mode of the host executable: writes at a fixed offered rate on a schedule, each latency from its scheduled time, every sample kept, a warm-up discarded, against an in-process cluster or a Compose cluster; it writes a record (P10-01). Two controls, each a test: (a) **a planted stall:** the leader's loop pauses 200 ms every 2 s; at a moderate offered rate, the open-loop 99th percentile must reflect it, and the same run measured closed-loop must not (the representation shown to matter); (b) **a planted slowdown of known size:** every sync delayed by 2 ms through the file-system interface; the median commit latency must rise by the model's prediction for it (two syncs in series, so about 4 ms) within a stated tolerance.
- **Vacuity:** A generator that waits for replies measures closed-loop and hides stalls; one that starts its clock at the send does the same. Guarded by control (a), which fails in either case (S-bench-1). A generator insensitive to the system's cost passes everything; guarded by control (b) (S-bench-2).
- **Sabotage:** S-bench-1, S-bench-2
- **Verifiable here:** yes — both controls in one test process
- **Prediction:** Under the planted stall, at an offered rate that keeps the cluster at about half its capacity, the closed-loop 99th percentile stays under 20 ms and the open-loop 99th percentile is over 150 ms. A stall delays one request per closed-loop client, about 3 in 2,000 per period; at a fixed rate it delays every request scheduled in the 200 ms, a tenth of each period. **Observable:** the two percentiles in control (a)'s record.
- **Outcome:** pending

### P10-04 — Throughput and latency against the target

- **Task:** The done criterion. On this container, in Release: the offered-load curve (completed writes a second and the 50th, 99th and 99.9th percentiles at each offered rate, five repetitions each, the spread stated) for the in-process cluster and for Compose; the highest sustained rate; each against P10-02's target, met or missed, with the reason when missed. On a GitHub runner, the Compose curve once, for the record. Every number in the report cites its record.
- **Vacuity:** A curve measured below the knee says nothing about capacity; one measured only past it says nothing about latency. Guarded by offered rates from a tenth of the model's capacity to past the point where completed falls below offered, and by reporting where the knee is.
- **Sabotage:** ; manual: control (b)'s planted slowdown, run once at the target load in Compose, must move the numbers by the model's amount, or the Compose measurement is not measuring the host
- **Verifiable here:** partial — this container's numbers; a runner's for the record only
- **Prediction:** The host misses the throughput target and meets the latency target at half load. Each client write reaches Core as its own input and the leader syncs once per input, so the leader's sync rate caps throughput, while the model's capacity counts what one sync can carry. Followers receive up to 64 entries in one append and one sync, so they are not the limit. **Observable:** the highest sustained rate against the target, and the leader's syncs per committed write in the host's events.
- **Outcome:** pending

### P10-05 — The persist barrier's cost, and the disk-acknowledgement decision

- **Task:** Decision 6, the register row. In the real host at P10-04's target load: per committed write, the time the leader's sends waited behind its own persists, as a share of commit latency, from the host's events (each effect list's persist and send times, recorded in ticks). The row is closed by the decision rule, with the measurement as evidence, or brought to the reviewer as a spec amendment.
- **Vacuity:** A share computed from events that do not record when a send was released measures nothing. Guarded by a test in which the barrier is the only delay (a planted sync delay, no network delay): the measured share must be nearly all of commit latency.
- **Sabotage:** S-bench-3
- **Verifiable here:** yes — in process and in Compose on this container
- **Prediction:** The barrier is between a third and a half of median commit latency at the target load: the leader's sync and the follower's sync are in series today, and the round trip on loopback is far shorter than a sync, so pipelining the leader's sync with replication would remove about one sync from each commit. By the decision rule that brings it to the reviewer. **Observable:** the measured share, median and spread over five repetitions.
- **Outcome:** pending

### P10-06 — Session expiry's cost

- **Task:** Decision 8, the register row. The same 1,000 soak executions at the phase-9 head and at phase 8's head (`6f5001d`), in Release, in one session on this container, interleaved, five times each. The difference in execution time, with its spread; and, separately, the cost per session command of the last-use set in a micro-measurement of `KvStateMachine.Apply`.
- **Vacuity:** A comparison across commits that differ in more than the last-use set attributes everything to it. Guarded by listing every change to `Raft.Kv` and `Raft.Core` between the two commits, and measuring the micro-benchmark against the set alone.
- **Sabotage:** ; manual: a planted cost of known size (a busy wait of 1 µs per session command at the phase-9 head) must show as that many microseconds per command in the micro-measurement
- **Verifiable here:** partial — one machine, one session here; a GitHub runner's spread is not measured
- **Prediction:** The last-use set costs under 5% of a soak execution's time, and the difference across the two commits is inside the spread of the five repetitions. A sorted-set update is a few hundred nanoseconds, and an execution spends its time in the simulator and the checkers. **Observable:** the difference and its spread; the micro-measurement's cost per command.
- **Outcome:** pending

### P10-07 — fsync failure injection against the real host

- **Task:** Decision 7, the register row. Two wrappers of the host's `IFileSystem`: a sync that throws, and a crash that discards every write after each file's last successful sync. The host stops on a failed sync (a process exit with a stated code), acknowledges nothing after it, and recovers from its files on restart. Tests in one process under the real client's load, with the checker on the history: a leader whose sync fails, a follower whose sync fails, and a discard-crash of each.
- **Vacuity:** A host that catches the failure and goes on looks healthy and loses acknowledged writes later. Guarded by the test that the host's last acknowledgement precedes the failed sync, and by S-fsync-1 (the failure caught and ignored).
- **Sabotage:** S-fsync-1, S-fsync-2
- **Verifiable here:** yes — in one test process
- **Prediction:** Today an exception from a sync stops the host's loop thread and nothing else: the process stays up, still accepts client connections, and answers none of them, so the first test fails on "the host did not exit", not on any lost write. **Observable:** the first test's failure message before the change.
- **Outcome:** pending

### P10-08 — The unexplained failures of phase 9's first push

- **Task:** Decision 9, the register row, if the person approves the container. At `47d6ecd`, the README walk and `host-sabotages.sh` run inside a container as an unprivileged user, with the Docker socket and the proxy certificate mounted read-only, ten times each; then the same at `1921551` (the guess applied). The row is closed if a cause is shown; otherwise it stays open, with what was ruled out.
- **Vacuity:** A reproduction that runs as root again reproduces nothing. Guarded by the run printing its uid, and by a check that a root-owned file in the worktree cannot be removed by it.
- **Sabotage:** ; manual: a root-owned file planted in a worktree must make the unprivileged `git worktree remove --force` fail, or the reproduction is not unprivileged
- **Verifiable here:** partial — only with the person's approval of the container
- **Prediction:** The guess does not reproduce exit 255. `git worktree remove` fails with exit 128 on a file it cannot delete, and `set -e` would pass that on. What fails, if anything, is S-compose-1's baseline, through the kill stamped before the kill, with exit 1 from the walk. **Observable:** the exit codes of the ten runs at `47d6ecd`.
- **Outcome:** pending

### P10-09 — The visualiser: the decision

- **Task:** Decision 10, the register row. Read the findings of phases 1 to 9 for any failure whose diagnosis a timeline would have shortened; drop the row by a spec amendment (§9's optional item removed), or build the smallest timeline that would have helped, as the reviewer decides.
- **Vacuity:** A reading that finds nothing because it did not look tests nothing. Guarded by listing each finding that involved diagnosing an execution, with how it was diagnosed.
- **Sabotage:** ; manual: the list of diagnosed executions is checked against the findings log by the reviewer
- **Verifiable here:** partial — the reading here; the decision is the reviewer's
- **Prediction:** No finding of phases 1 to 9 names a failure that a timeline would have diagnosed sooner than the shrinker's minimal schedule and the trace. The failures that took longest, phase 9's on GitHub, were unreadable logs, which a visualiser does not fix. **Observable:** the list.
- **Outcome:** pending

### P10-10 — The new work's cost in CI

- **Task:** Measure what phase 10 adds to CI on GitHub: each harness shard under P10-00's assignment against phase 9's three runs, the build job (the new gate), and the local run before the push, from runs whose every job completed once.
- **Vacuity:** A measurement on a run with re-run jobs compares different work. Guarded by using only first attempts.
- **Sabotage:** ; manual: the comparison is checked against run attempts, every one a first
- **Verifiable here:** partial — CI's numbers only in CI
- **Prediction:** The build job grows by under 10 s (the measurement gate reads a few files), and the harness shards stay inside P10-00's prediction across every run of the phase, the new entries with their recorded costs included. **Observable:** the build job's gate step and each shard's harness step in each first-attempt run.

- **Outcome:** pending

## Sabotage ids

New series: S-meas (P10-01), S-bench (P10-03, P10-05), S-fsync (P10-07). S-shard-4..5 follow S-shard-3.
Each id's `sabotage/<id>/` entry lands in the same commit as the check it proves and is run on that
commit before it is pushed; the touched-file stage runs before every push; the shards run in CI.

## Register rows

To be closed here: disk acknowledgements (P10-05, by the decision rule), fsync failure injection
(P10-07), session expiry's cost (P10-06), the visualiser (P10-09). The unexplained failures
(P10-08) are closed only if a cause is shown, and are re-promised otherwise. Opened at approval if
the reviewer asks: a group commit on the leader, if P10-04 misses for the reason predicted.
