# Phase 13 — task breakdown

The leader's loop, against the restated target (spec §11 phase 13, added at phase 12's acceptance;
its done criterion is P13-00's to write, at this breakdown's approval). Format as in
`docs/phases/P12/breakdown.md`, parsed by `gates breakdown`; outcomes will say `(evidence)` or
`(forcing)`.

**Draft for review.** No implementation until it is approved.

**Where latency stands, against the restated target** (`docs/design/performance-target.md`, restated
at phase 12's acceptance: the measured floor plus the queue the leader's loop forms at the
criterion's load). In process at 3,125 writes a second the median is 1.40 ms against 1,066 µs,
**about 1.3 times**, and the 99th percentile 21 to 40 ms against 3.38 ms, six to twelve times. In
Compose the median is 3.7 to 7.4 ms, against a criterion not yet grounded for Compose (its hops cross
containers, and no floor was measured for them). The phase-12 acceptance message put the median at
2.6 times the restated criterion; that ratio was against the old 543 µs, and the restated one is
about 1.3. The tail is where the target is missed most.

**The cap phase 12 named** is the leader's single loop running its syncs inline: at 3,125 a second
the syncs keep it 0.75 busy, and the decomposition's three loop queues (requests 312 µs, the wait to
send 244, answers 384 at their medians) are half the traced median of 1,829 µs. **The loop waits for
each sync, and the contract never asked it to.** Node-interface §4: "a `Send` or `ClientResponse` is
released only after every persist effect that the same node emitted before it ... is durable ...
the node never waits, the world holds the message back." The simulator's world does exactly that,
and every invariant and soak holds under it; the host alone runs each effect list to the end, its
syncs included, before taking the next input. This phase moves the waiting from the loop to the
effects, as the contract describes, so requests and answers stop queuing behind syncs. It is not a
group commit: every persist is still synced on its own.

**Done when** (proposed for spec §11 by P13-00):
- **the host executes each node's effect lists off its loop**, on one thread, in the order the node
  emitted them, so the node never waits for a sync (node-interface §4); the persist barrier, the
  stop on a failed write or sync, and every invariant, soak, Compose and checker result unchanged;
- **the curve measured again against the restated target**, interleaved against phase 12's head, in
  process and in Compose: the median and 99th-percentile criteria at 3,125 writes a second met, or
  the gap attributed segment by segment beside the measured floors and the allowance's model;
- **Compose's hop floor measured**, and a Compose criterion restated from it;
- **every row promised to phase 13 closed:** the stall control redesigned so its sabotage cannot
  pass; leader changes in load runs explained or counted; the four hand-kept inputs (a record's
  commit, the touched estimate, the cost file, the Budget seeds) checked by something other than
  memory; the target's figures still set by hand grounded or declared conventions.

Carried forward from phase 12's acceptance:
- **the instrument first** (the CRDT project's §13.30 in the benchmark): every comparison here is
  interleaved against phase 12's head, on one machine in one session, never against an older
  record; every load record keeps the generator's lateness, and gains the cluster's role changes
  (P13-03);
- **a gate that does not decide answers no:** no group commit in this phase and no gate for one;
- **a check that cannot fail is rejected:** every new check has a sabotage, and every new timing
  control is measured on both sides in the margin audit's three neighbour modes (alone, beside
  three busy processes, beside the Membership and Scale suites);
- **a target figure set by hand is suspect until grounded** (P13-09).

## Ordering

The spec amendment first (P13-00). Then the two process tasks for the hand-kept inputs, so this
phase's own pushes use them (P13-01, P13-02), and the role changes in load records (P13-03), so every
record the latency work takes carries them. Then the latency work: the loop measured on the day
before any change (P13-04), the effects taken off the loop (P13-05), the curve against the restated
target (P13-06), and Compose's floor and criterion (P13-07). Then the stall control (P13-08), after
the host has changed, because the host's change moves its numbers. Then the target's remaining
figures (P13-09), which need P13-04's measurement, and the cost in CI (P13-10).

**Blocking set:** P13-00 to P13-09. P13-10 blocks nothing.

## Decisions for review

1. **Phase 13's done criterion** (P13-00), as above. The phase is done when the criteria are met, or
   when the remaining gap is attributed: each remaining segment beside its floor timed alone, and
   the queues beside the allowance's model with its inputs measured (P13-04).
2. **The remedy: one effect thread per host, in order, from a bounded queue** (P13-05). The loop
   hands each effect list to the thread and takes its next input; the thread runs the lists in the
   order the node emitted them, each persist synced before anything after it, which is node-interface
   §4's barrier exactly. The queue holds 1,024 lists; when it is full the loop waits, which is
   today's behaviour, under overload only. A failed write or sync stops the host before any later
   effect runs (P10-07). Core and the simulator are unchanged: the simulator already lets the node
   run ahead of durability. **Not chosen:**
   - a group commit (the reviewer: not this phase);
   - **a response released ahead of later persists.** §4 holds a `ClientResponse` behind every
     persist the node emitted before it, so once the loop runs ahead, a commit's response waits in
     the effect queue behind the appends of newer writes. Releasing it as soon as its own entry is
     durable would need Core to say what each response depends on: a change to the contract, to the
     simulator's world, and to every positive control of the barrier. Proposed only if P13-06
     attributes the remaining gap to that second wait;
   - **more than one loop** (one per peer, say): it reorders the node's inputs, which Core assumes are
     serial.
3. **Spec §4 amended** (P13-00): the host runs effects off the loop, in emitted order, and the node
   never waits; the stop on a failed sync, unchanged in meaning, is stated for the effect thread.
4. **The comparison is interleaved against phase 12's accepted head** (its host and generator), five
   runs a side at each rate, untraced, in process and in Compose; the decomposition is taken traced,
   at 625 and 3,125, as in phase 12. Phase 12's records are not the baseline: they are a day older.
5. **The four hand-kept inputs, homed here at phase 12's acceptance** (P13-01, P13-02):
   - a measurement record's commit must be an ancestor of the commit being checked (`gates
     measurements`);
   - the touched stage's estimate becomes the shard plan's own model of the selected entries (their
     cost-file lines dealt to the stage's workers, plus the baseline build, P10-00) scaled by one
     local-to-GitHub ratio fitted to phase 11's and phase 12's runs, and the stage fails when a run
     takes more than twice its estimate (the estimate decides whether a push needs
     `--accept-estimate`);
   - the build-collect job reads every shard's entry times through GitHub's API (as `gates reports`
     reads runs, so no new action) and fails when the run's total is beyond a factor of 1.5 of the
     file's sum for the same entries, writing the refreshed file into its log;
   - the Budget tests' seeds move to a file both read, and the soak whose configuration they use
     fails when one of them is not among its ten hardest decided histories by checker states.
   Each is a gate change with a sabotage. If you would rather keep phase 13 to latency, they move to
   a later phase by your direction, and the register rows move with them.
6. **The stall control's redesign** (P13-08): a 1-s stall every 4 s instead of 300 ms every 2 s, the
   election timeouts raised to 3 to 6 s so the stalled leader keeps office, the open side judged at
   its 95th percentile. The sabotage leaves one slow write a stall, about 0.5% of writes; the control
   about 21%; a runner would have to pause past 150 ms for 4.5% of a run to flip either side, against
   2% today.
7. **The target's figures still set by hand** (P13-09): the allowance's model is a claim, and P13-04
   measures its input (the loop's work beside its syncs). The operating point (half of C_design),
   the sustained-rate definition (within 5%, the 99th percentile under 100 ms) and its halving are
   conventions of where and how to measure: proposed to be declared as such in the target, each with
   its reason, rather than grounded. Composing percentiles by summing stays, declared lenient.
8. **Compose's criterion** (P13-07): the restated target's formula with Compose's measured hop floor
   in place of loopback's, the same allowance; Compose is judged against it from P13-07 on.

## Tasks

### P13-00 — The spec amended: phase 13's done criterion; the host's effects off the loop

- **Task:** Decision 1 and decision 3. Spec §11's phase-13 row gets the done criterion above in place of the placeholder; §4 says the host runs each node's effect lists off its loop, in the order emitted, and that the node never waits for a sync; nothing else in the spec changes. The register rows promised to phase 13 cite the row.
- **Vacuity:** An amendment that leaves §4's description of the host's loop as it is: the host would then contradict its spec after P13-05. Guarded by the outcome naming the sections changed and showing that §4 changed.
- **Sabotage:** ; manual: the diff read against the approved text, and every section it touches named in the outcome
- **Verifiable here:** partial — the text here, its approval the reviewer's
- **Prediction:** The amendment changes §11 (the phase-13 row) and §4 (the host's loop), and no other section; node-interface §4 needs no change, because it already says the node never waits. **Observable:** the diff.
- **Outcome:** pending

### P13-01 — A record's commit, and the touched estimate, checked against what they describe

- **Task:** Decision 5, its first two items. `gates measurements` refuses a record whose commit is not an ancestor of the commit it checks, or does not resolve; the touched stage's estimate is the shard plan's model of the selected entries (their cost-file lines dealt to the stage's workers, plus the baseline build) scaled by one local-to-GitHub ratio in `ci/sabotage-touched.txt`, fitted to every touched run of phases 11 and 12, and the stage fails when its run takes more than twice its estimate. Two register rows close.
- **Vacuity:** An ancestry check on a clone without history resolves nothing and passes everything; guarded by failing when a commit does not resolve, and by CI's full-history checkout, which the per-commit matrix already needs. A fit that reproduces only the runs it was fitted on proves nothing about the next; guarded by stating the fit's error on each run, and by the stage's own comparison on every later push.
- **Sabotage:** S-meas-3, S-touch-3
- **Verifiable here:** yes — both run in the local stages
- **Prediction:** No record on today's tree names a commit off the branch (the reword's relabel left none). The flat 13.7 s an entry is within a factor of 2 of 15 of phase 11's and 12's 19 touched runs: it misses both single entries (153 s against 14, since S-kill-1 alone takes 117 s), one of four two-entry runs, and the 67-entry run (381 s against 918). A fixed cost plus a rate cannot fit runs this uneven; the shard plan's model, which knows each entry's line and each worker's build, comes within a factor of 2 of at least 17 of the 19. **Observable:** the gate's result on today's tree; each run's time beside the old and the new estimate.
- **Outcome:** pending

### P13-02 — The cost file and the Budget seeds checked against the runs that produce them

- **Task:** Decision 5, its last two items. The build-collect job reads every shard's entry times from the run (through GitHub's API) and fails when the run's total is beyond a factor of 1.5 of the cost file's sum over the same entries, and writes a refreshed file into its log; the Budget tests' seeds move to `ci/budget-seeds.txt`, and the soak whose configuration they use fails when one of them is not among its ten hardest decided histories by checker states. Two register rows close.
- **Vacuity:** A total over the shards that reported compares less work than the run did; guarded by requiring every shard's times, as build-collect already requires every shard's result. A ranking by something other than the checker's states names the wrong seeds; guarded by asserting that the ranking's first seed's state count is the run's maximum.
- **Sabotage:** S-shard-12, S-soak-12
- **Verifiable here:** partial — the seeds' check runs in a local soak (deterministic); the cost check's logic by its tests here, its numbers only on GitHub
- **Prediction:** On the last green run's times (run 37845882191) the total is within a factor of 1.3 of the file's sum, since the file was refreshed from runs a day older. The three named seeds (9532, 6673, 9824) are still the baseline soak's three hardest decided histories, in that order (3,702, 2,371 and 890 states), because P11-03 picked them after the resend fix and nothing since has changed Core, the simulator or the workload. **Observable:** the check's ratio on that run; the soak's ranking.
- **Outcome:** pending

### P13-03 — Every load record carries the cluster's role changes

- **Task:** The register row on phase 12's two unexplained leader changes. Every load record (bench mode) carries, for its measured window, each host's changes of role with their times and, for each election, the electing follower's longest silence from the leader before its timer fired; in process read from the hosts, in Compose from a `Roles|` client command available in bench mode only. A leader change in an unfaulted run is then explained (a silence past the election timeout: the leader stalled) or counted as unexplained. The row closes when every leader change in phase 13's load runs is one or the other.
- **Vacuity:** A count read from one host misses a change on another; guarded by reading every host. A record that carries the field but never a change says nothing; guarded by a control that stalls the leader past its timeout mid-run and needs the record to show the change and its silence.
- **Sabotage:** S-lead-1
- **Verifiable here:** yes — in process and in Compose
- **Prediction:** Leader changes in phase 13's load runs are rarer than 1 in 200, and each recorded one follows a follower's silence longer than its election timeout, not a timer fault: phase 12's two (1,000 a second in process; 3,125 in Compose on phase 11's host) were overload or collector stalls. **Observable:** the role changes and silences in the records.
- **Outcome:** pending

### P13-04 — The loop measured on the day, before any change

- **Task:** Decision 4's baseline and decision 7's input. At phase 12's head: the decomposition at 625 and 3,125 in process and in Compose (traced, five runs each), and the leader's loop time split, per write, into the node's handling and the effects' execution (its syncs among them), from the host's own timers in bench mode. The split is the allowance's model's input: a single server whose service is the sync alone.
- **Vacuity:** A split whose parts do not add up to the loop's busy time measures something else; guarded by the parts summing to the loop's measured busy fraction within 5%, reported.
- **Sabotage:** ; manual: the split's parts against the loop's busy fraction in each record
- **Verifiable here:** partial — measured here; the split read against the loop's busy time is the reviewer's to check
- **Prediction:** At 3,125 a second in process, the loop's three queues are still about half the traced median (850 to 1,000 µs of 1.7 to 1.9 ms), and the loop's work beside its effects (reading requests and answers, the node's handling) is under 0.15 of its time, so the allowance's single server of syncs holds within 20%. **Observable:** the segments; the loop's split in the records.
- **Outcome:** pending

### P13-05 — The effects off the loop

- **Task:** Decision 2. Each host runs its node's effect lists on one thread of their own, in the order the node emitted them, from a queue of 1,024 lists (the loop waits only when it is full); a failed write or sync stops the host with no later effect run (P10-07); the persist barrier's cost, the hand-off trace and the decomposition read the new thread as they read the loop. The register row on the loop's cap closes with P13-06.
- **Vacuity:** An effect thread the loop still waits on is today's host under another name; guarded by a control that holds a sync open and needs the loop to take the next input meanwhile (S-loop-3). Out-of-order lists release a send before an earlier persist is durable, which no latency number shows; guarded by a host test over a file system that delays every second sync, recording each send's time against each earlier persist's durability (S-loop-1). Lists that keep running after a failed sync answer clients about writes that are not durable; guarded by a test whose second sync fails and that needs nothing sent after it (S-loop-2). S-hostdisk-1 stays caught.
- **Sabotage:** S-loop-1, S-loop-2, S-loop-3; shared: S-hostdisk-1
- **Verifiable here:** yes — the host, the bench and Compose run here
- **Prediction:** At 3,125 a second in process, interleaved against phase 12's head: the leader's request and answer queues fall from about 300 and 380 µs at their medians to under 80 each; a wait in the effect queue appears twice on a commit's path, before the leader's persist and before the response (§4 holds each response behind every earlier persist), 100 to 300 µs each; the median falls by 15 to 35%, to 0.9 to 1.2 ms. The highest sustained rate rises from 3,125 to 3,500 or 4,000, the sync now the only serial work (about 1/S, 4,100 a second, under load). Every invariant, soak, Compose and checker result is unchanged. **Observable:** the segments and the curve's records; the CI run.
- **Outcome:** pending

### P13-06 — The curve against the restated target

- **Task:** Decision 4. The curve measured again, untraced, five runs a side interleaved against phase 12's head at 625, 1,250, 2,000, 3,125, 3,500 and 4,000 writes a second in process, and the same rates in Compose; the decomposition at 625 and 3,125; each judged against the restated target (P13-07's criterion for Compose), the gap attributed segment by segment beside the measured floors and the allowance's model.
- **Vacuity:** A comparison across days measures two machines' days, as phase 12's records against phase 11's would have; guarded by interleaving every comparison in one session. A criterion met by a run whose leader changed measures something else; guarded by P13-03's role changes in every record.
- **Sabotage:** ; manual: every table number cites its record
- **Verifiable here:** partial — in process and Compose here; GitHub's runners are not the measuring machine
- **Prediction:** **The headline.** Taking the syncs off the loop removes the loop's queues but the restated median (1,066 µs at 3,125) is met in process in at most two runs of five, missed by under 15%, because §4 makes each response wait in the effect queue behind later persists, a second wait the restated allowance counts once. The 99th percentile (3.38 ms) is met nowhere, falling to 5 to 15 ms. Compose's median at 3,125 falls by half or more, to 1.5 to 3 ms. If the second wait is the remaining gap, the next remedy is decision 2's contract change, not more host work. **Observable:** the curve's records; the decomposition's segments.
- **Outcome:** pending

### P13-07 — Compose's hop floor, and its criterion

- **Task:** Decision 8 and the register row's Compose half. A cold one-way frame from one Compose container to a reader asleep in another, five runs (`bench hop` across containers); Compose's criterion restated in the target from it with the same allowance; the Compose curve judged against it.
- **Vacuity:** A hop measured within one container is loopback's; guarded by the record naming two hosts and the bench refusing a peer address that is its own.
- **Sabotage:** ; manual: the record's two hosts, and the floor beside loopback's
- **Verifiable here:** partial — Compose runs here; whether two containers on one machine stand for Compose's hosts is the reviewer's
- **Prediction:** A cold cross-container hop takes 100 to 200 µs at its median, against loopback's 83, so Compose's median criterion is 1.1 to 1.5 ms, and Compose's median at 3,125 after P13-05 is still above it. **Observable:** the record; the target's Compose line.
- **Outcome:** pending

### P13-08 — The stall control redesigned so its sabotage cannot pass

- **Task:** Decision 6 and the register row. The stall control plants a 1-s stall every 4 s with election timeouts of 3 to 6 s, and judges the open side at its 95th percentile; its margins measured in the audit's three neighbour modes on both sides (`scripts/margins.sh`); S-bench-1 caught.
- **Vacuity:** A stall long enough to cause an election measures failover, not a stall; guarded by the control's assertion that the stalled host keeps office.
- **Sabotage:** ; shared: S-bench-1
- **Verifiable here:** yes — the control and its margin script run here
- **Prediction:** Unpatched, 18 to 24% of open-loop writes take over 150 ms (the 95th percentile 400 to 900 ms); with S-bench-1, under 1% (the 95th percentile under 20 ms); every margin row of the control keeps a factor of 3 or more on both sides in all three modes, and the leader keeps office in every run. **Observable:** the margin table.
- **Outcome:** pending

### P13-09 — The target's figures still set by hand, grounded or declared

- **Task:** Decision 7 and the register row's other half. The allowance's model checked against P13-04's split; the operating point, the sustained-rate definition and its halving declared in the target as conventions, each with its reason; summing percentiles declared lenient; the row closed.
- **Vacuity:** Declaring every figure a convention grounds nothing; guarded by grounding the one that is a claim (the allowance's model) by measurement, and by your decision on which are conventions.
- **Sabotage:** ; manual: the target's section read against decision 7
- **Verifiable here:** partial — a document from P13-04's records; which figures are conventions is the reviewer's
- **Prediction:** P13-04's split holds the allowance's model within 20% (the loop's work beside its syncs under 0.15 of its time), so the restated criterion stands; nothing else in the target changes. **Observable:** the split's records; the target's section.
- **Outcome:** pending

### P13-10 — The phase's cost in CI

- **Task:** The harness shards, the soaks and the per-commit matrix on the phase's first-attempt runs against phase 12's, the per-commit job count per push and whether every job was given a runner, and the never-verified commits tabled, failed apart from never ran.
- **Vacuity:** A run with a job that never ran compares less work. Guarded by counting only jobs with steps and stating any that never ran.
- **Sabotage:** ; manual: the comparison checked against each run's job list
- **Verifiable here:** no — CI's numbers only in CI
- **Prediction:** The phase adds eight harness entries (S-loop-1 to -3, S-lead-1, S-meas-3, S-touch-3, S-shard-12, S-soak-12) and moves no head shard past 400 s, against phase 12's slowest of 360 s. The soak whose seeds are checked grows by under 10%. Every per-commit job is given a runner. **Observable:** the shards' slowest step, the entries added, the soak's time, and the job counts.
- **Outcome:** pending

## Sabotage ids

New series: S-loop (P13-05); S-lead (P13-03). S-meas-3, S-touch-3, S-shard-12 and S-soak-12 follow
their series. Each id's `sabotage/<id>/` entry lands in the same commit as the check it proves and
is run on that commit before it is pushed.

- **S-meas-3:** the ancestry check skipped, so a record naming a commit off the branch passes.
- **S-touch-3:** the touched stage's run never compared with its estimate.
- **S-shard-12:** the run's total never compared with the cost file's sum.
- **S-soak-12:** the Budget seeds never compared with the soak's ranking.
- **S-lead-1:** role changes not recorded, so a forced leader change reads as none.
- **S-loop-1:** two effect threads, so lists run out of order and a send can leave before an earlier persist is durable.
- **S-loop-2:** after a failed sync the queued lists still run.
- **S-loop-3:** the loop waits for each list to finish (the change undone behind the new thread).

## Register rows

Promised here, at phase 12's acceptance: the cost file kept current (P13-02), the touched estimate
(P13-01), the Budget seeds (P13-02), a record's commit (P13-01), the unexplained leader changes
(P13-03), the stall control's redesign (P13-08), and the target's figures still set by hand with
Compose's hop floor (P13-07, P13-09). Each closes with its task. The rows promised to phase 0 (the
person's walk) stay the person's.
