# Phase 14 — task breakdown

The group commit, against restated criteria (spec §11 phase 14, added at phase 13's acceptance; its
done criterion is P14-00's to write, at this breakdown's approval). Format as in
`docs/phases/P13/breakdown.md`, parsed by `gates breakdown`; outcomes will say `(evidence)` or
`(forcing)`.

**Draft, for review.** No task starts before approval.

**Why a group commit, and why now: by elimination.** Phase 11's gate (the leader's sync 80% busy at
the highest sustained rate) said no, at 61 to 67%. Phase 12's corrected gate was undecided: 0.787
against 0.8 needed a filter chosen after seeing the data, and a gate that does not decide answers
no. Phase 13 moved the leader's effects off its loop and showed what the cap is: the loop went from
0.94 busy to 0.06, the effect thread to 0.95, and the median at 3,125 writes a second rose 25%. The
serial work per write is the same wherever it runs. On phase 12's host at 3,125 it is 302 µs (the
node's handling 13 µs, the effects 289 µs, the sync 234 µs of them), one sync a write
(`m:p13-05-ab-base-3125-*`). **The followers already group their commits and the leader does not:**
the leader syncs 1.00 times a write at every rate, while each follower syncs 0.99 times a write at
625 writes a second, 0.84 at 2,000, 0.38 at 3,125 and 0.13 at 4,000, because the leader's
AppendEntries carries several entries under load and a follower syncs once a message. The remedy the
data name is the same grouping at the leader. It was reached by exhausting the alternatives over
three phases, not by a threshold (the reviewer, at phase 13's acceptance; spec §2).

**Where latency stands: against no grounded criterion.** On phase 12's host, the host this phase
starts from once the effect thread is reverted, at 3,125 writes a second in process: the median
1,124 µs (1,081 to 1,490 over five runs), the 99th percentile 26.4 ms (11.3 to 50.1). The criteria
phase 13 reported against, 1,066 µs and 3.38 ms, are not grounded: their floor was a sum of parts,
dropped by P13-08, and their allowance's model was falsified input by input. P14-02 restates both
before the group commit is judged.

**Done when** (proposed for spec §11 by P14-00):
- **the effect thread reverted:** the host runs each effect list on its loop again, and spec §4 says
  so; "the node never waits" stays the node's contract, kept by the simulator's world and by the
  host's barrier, as before phase 13. The three sabotage entries whose targets go with it are
  retired by a recorded mechanism that cannot retire a live entry, and the reverted host is measured
  against phase 13's head;
- **both latency criteria restated** (P14-02) from floors measured end to end, every figure set by
  hand declared as such;
- **a group commit in the host:** the loop takes the inputs already queued, runs the node on each,
  writes the batch's persists in the order the node emitted them and syncs once, and releases the
  batch's sends and responses after that sync. Core and the simulator unchanged; the persist barrier,
  the persists' order, the stop on a failed write or sync, and every invariant, soak, Compose and
  checker result unchanged;
- **the curve measured again against the restated criteria,** interleaved against the reverted host,
  in process: the criteria at 3,125 writes a second met, or the gap attributed segment by segment
  beside the floors. Compose runs as the wiring and integration check it is, its comparisons to
  2,000 writes a second;
- **every bench control's margins measured again** after the host's changes;
- **every row promised to phase 14 closed:** the leader's serial work per write (the group commit's
  subject), the broad-targets premise and the margins tables' premise each checked by a mechanism,
  and the target's figures restated.

Carried forward from phase 13's acceptance:
- **the instrument first:** every comparison interleaved on one machine in one session against the
  head it changes, never against an older record; every load record keeps the generator's lateness
  and the cluster's role changes;
- **a model validated by its output is not validated:** a model's inputs are measured and checked
  one by one before its output is compared with anything (P13-08's allowance agreed with the waits
  by cancelling two wrong inputs);
- **a composed floor is checked end to end** at the conditions it is used at;
- **a check that cannot fail is rejected:** every new check has a sabotage, and every new timing or
  counting control is measured on both sides in the margin audit's three neighbour modes;
- **Compose is a wiring check:** a Compose number above 2,000 writes a second on this machine is not
  a comparison, and none is called one;
- **every input a gate reads is registered** with what verifies it (`gates inputs`).

## Ordering

The spec amendment first (P14-00), then the revert (P14-01), so the group commit is built on the
host that existed before phase 13 and the floors are measured on it. Then the criteria (P14-02),
from P14-01's runs, before the group commit exists, so they cannot be fitted to it. Then the group
commit (P14-03) and its curve (P14-04). The two premises next (P14-05, P14-06), the margins tables'
before the audit (P14-07) so the audit's table is the first it holds. The cost in CI last (P14-08).

**Blocking set:** P14-00 to P14-07. P14-08 blocks nothing.

## Decisions for review

1. **Phase 14's done criterion** (P14-00), as above. The phase is done when the restated criteria are
   met, or when the remaining gap is attributed beside the floors measured end to end.
2. **The revert is phase 14's first task, not an edit at acceptance** (P14-01). The reviewer decided
   it at phase 13's acceptance; it is done as a task because it removes the targets of three sabotage
   entries (S-loop-1 to S-loop-3), which the breakdown gate does not allow (a completed task's
   sabotages must exist), because it has a measurable consequence worth predicting, and because the
   group commit builds on it. **The retirement:** an entry's `sabotage.txt` gains `retired: <commit>
   <reason>`; the harness, the patch gate and the cost file skip a retired entry; the breakdown gate
   accepts a retired entry only for a task with a final outcome, so a task in progress cannot lean on
   one; and retiring an entry whose target still exists in the tree fails, so retirement cannot
   silence a live check (S-retire-1). **Not chosen:** deleting the entries and rewriting P13-04's
   sabotage field (it edits an accepted record); keeping the effect thread's tests (they would test
   code that no longer exists).
3. **The restated criteria** (P14-02). **Proposed:** each criterion is a floor measured end to end
   plus one declared allowance.
   - *The median's floor:* the lowest median the reverted host reaches below its knee, measured end
     to end (on phase 12's host, 846 µs at 1,250 writes a second, `m:p13-05-ab-base-1250-*`).
   - *The tail's floor:* the lowest 99th percentile it reaches at any measured rate (2,402 µs at 100
     writes a second, `m:p13-08-ab-base-100-*`).
   - *The allowance, declared:* one leader sync measured at the operating point (234 µs at 3,125). A
     write that arrives while its leader is syncing waits for that sync before its own; with a group
     commit it waits for at most one. That is the queue any design with one disk keeps, stated as a
     convention with its arbitrariness recorded: zero would demand no queue at all, and two would
     admit a queue the group commit exists to remove.
   - Both floors are taken from P14-01's runs, on the reverted host, on the day; the operating point
     and the sustained-rate definition stay the conventions P13-08 declared.

   **Not chosen:** a queueing model with measured inputs (a trace of arrival times and per-write
   service fed to Lindley's recursion). The service depends on the design under test, so the
   criterion would move with the group commit it is meant to judge. Also not chosen: reporting
   latency with no criterion, as the sustained rate is; the reviewer asked for both criteria.
4. **The group commit's design** (P14-03): batching where the loop already is, with no timer.
   - After the input it waited for, the loop takes every input already queued, up to 64, and runs
     the node on each in order.
   - It executes their effect lists together: every persist written in emitted order without a
     sync, then one sync of each file written, in the order the files were first written (so a term
     record written before an append is durable before it, as today), the directory synced after
     a file was created, then every send, response and event in emitted order.
   - A rename, a delete or a truncate is a boundary: the writes before it are synced first, and it
     keeps its own syncs, as today, which keeps the snapshot's two-way barrier (P7-05).
   - A failed write or sync stops the host before any of the batch's outputs is released (P10-07).
   - An idle loop takes batches of one, so a write at low load waits for nothing it waits for
     today. Holding a send for a later write's persist is within node-interface §4, which says when
     a message may leave, not that it must.

   **Not chosen:** a timer window (adds latency at low load, and a timeout is one more tuned number);
   batching in Core (changes the node and the simulator's world for what the host can do alone); a
   group commit on the effect thread (reverted with it).
5. **The comparisons:** P14-01 interleaved against phase 13's head (`0abc3e4`, the effect thread), and
   P14-04 against P14-01's head (the reverted host). Five runs a side at each rate, untraced, in
   process; the decomposition traced at 625 and 3,125. Compose runs once as the wiring check, with a
   killed leader and the checker green, and no Compose number above 2,000 writes a second is
   compared with anything.
6. **The broad targets' observer** (P14-05): the harness already finds, for each entry, the tests its
   patch turns red beside its target. Each shard's notice carries them; build-collect counts, over
   the run's entries, how many turned each test red; and a test red beside ten or more entries must
   be in `ci/broad-targets.txt` (`Currency.Member`, observer `broad-run`). The registry's row becomes
   a currency line and the register row closes.
7. **The margins tables' premise** (P14-06): each row of a margins table carries the commit it was
   measured at (a table's header gives one for every row, and a `# at <commit>: <checks>` line
   overrides it for the checks it names, as P13's hand-off rows already need). `gates margins` reads
   the newest phase's table, not phase 12's. A phase report fails when a bench control's source
   (`tests/Raft.Host.Slow.Tests/BenchControlTests.cs`, `scripts/margins.sh`, or `src/Raft.Host/`) has
   changed since the commit of a row describing it (observer `margins-commit`). It fails reports,
   not pushes: an audit takes over an hour and belongs once a phase, before its report.

## Tasks

### P14-00 — The spec's phase-14 row and §4 back to its contract

- **Task:** Spec §11's phase-14 row gets its done criterion (decision 1). §4's bullet "The host runs the effects off its loop (phase 13)" is replaced by one saying the host runs each effect list on its loop, its syncs inline, and from P14-03 a batch of lists with one sync; "the node never waits" stays the node's contract, kept by the simulator's world and by the host's barrier. The register's rows promised to phase 14 cite the row.
- **Vacuity:** A spec that keeps the effect thread's bullet while the code no longer has one states a host that does not exist; guarded by P14-01 landing after this amendment, so the two never disagree on a pushed commit for longer than one push, and by `gates register`, which reads the phase row.
- **Sabotage:** ; manual: the diff checked against the decisions (§4 and §11 only)
- **Verifiable here:** partial — the diff and the register gate here; its approval the reviewer's
- **Prediction:** The amendment changes §4 (the one bullet) and §11 (the row's done criterion), and no other section: §2's paragraph recording the group commit chosen by elimination was written at acceptance and needs nothing more. `docs/design/node-interface.md` §4 needs no change, as it needed none for phase 13. **Observable:** the diff.
- **Outcome:** pending

### P14-01 — The effect thread reverted, measured against phase 13's head

- **Task:** Decision 2. Remove `EffectThread`, the host's effect timers and waits, and the records' `leader_effects_*` and `leader_effect_queue_*` fields; the loop executes each list again, with P13-02's role log and P13-03's loop timers kept. Retire S-loop-1 to S-loop-3 by the mechanism (harness, patch gate, cost file, breakdown gate), S-retire-1 proving it; regenerate the patches the revert makes stale (S-fsync-1 back to the loop's stop, S-lat-1 and S-lead-1 for their context). Measure the reverted host against `0abc3e4`, interleaved, five runs a side at 100, 625, 1,250, 2,000, 3,125, 3,500 and 4,000 writes a second, in process.
- **Vacuity:** A revert that leaves the effect thread wired behind a switch measures the same host on both sides; guarded by the records' loop busy fraction (0.06 on the effect thread's side, near 0.94 on the reverted side at 3,125) and the effect fields' absence. A retirement mechanism that accepts any entry silences checks wholesale; guarded by refusing an entry whose target still exists (S-retire-1). A retired entry still counted in the cost file skews the shard plan; guarded by the cost file's construction check against the manifest.
- **Sabotage:** S-retire-1
- **Verifiable here:** yes — the host suites, the gates and the interleaved runs on this machine
- **Prediction:** On the reverted side at 3,125 the leader's loop is 0.92 to 0.96 busy and the median is 15 to 30% under the effect thread's side, within 10% of phase 12's host as P13-05 measured it (1,124 µs); at 625 the reverted median is 5 to 10% lower (one hand-off fewer, about 78 µs on P13-04's measure); the highest rate sustained in every run falls from 3,500 to 3,125. Every soak, invariant and Compose result unchanged; the soaks identical to the count, Core and the simulator untouched. Host.Tests' count falls by the three `EffectThreadTests`. The hand-off control, run as at `7f4b02c` on two processors beside two busy loops, keeps every other segment within 0.1 of the 2-ms delay in five runs (the effect threads' coupling gone; phase 12's host kept 85 µs at 500). **Observable:** the records, the control's reports, the gates and the test counts.
- **Outcome:** pending

### P14-02 — Both latency criteria restated from floors measured end to end

- **Task:** Decision 3, as approved, written into `docs/design/performance-target.md` from P14-01's reverted-side records: the median's floor, the tail's floor and the leader's sync at 3,125, each cited; the allowance declared with its arbitrariness; Compose with no criterion (a wiring check); every figure from phases 12 and 13 superseded where it stands.
- **Vacuity:** Floors taken after the group commit exists could be chosen to suit it; guarded by writing them from P14-01's records before P14-03's first commit (the prediction-order check holds the order). A floor taken from one rate's lucky run is noise; guarded by each floor being the median over five runs at its rate.
- **Sabotage:** ; manual: each figure checked against its cited records
- **Verifiable here:** partial — computed here from P14-01's records; whether each declaration is honest is the reviewer's
- **Prediction:** The median's floor falls at 1,250 or 2,000 writes a second, 0.80 to 0.95 ms; the tail's floor at 100 or 625, 2.2 to 3.5 ms; the leader's sync at 3,125, 220 to 250 µs. So the median criterion comes to 1.02 to 1.20 ms and the tail's to 2.4 to 3.8 ms, near phase 12's restated figures (1,066 µs, 3.38 ms) by a different route. The reverted host meets the restated median criterion at 3,125 in one to three runs of five and the tail's in none. **Observable:** the target's new section and its citations.
- **Outcome:** pending

### P14-03 — A group commit in the host

- **Task:** Decision 4: the loop drains up to 64 queued inputs and executes their lists as one batch (`DiskExecutor`), the persists written in emitted order, each file synced once in first-written order, a rename, delete or truncate a boundary, outputs released after the batch's sync, a failed write or sync stopping the host before any output. A control in Host.Slow.Tests counts the leader's syncs per write under a burst of concurrent writes. The host's trace keeps each entry's durable stamp at its batch's sync.
- **Vacuity:** A group commit that never groups passes every barrier test; guarded by the control's count (S-group-1). A batch releasing its sends before its sync breaks the barrier while the counts look right; guarded by the host's barrier control on a recording file system, extended to a batch of lists (S-group-2). Files synced out of first-write order leave a durable entry above a lost term record after a crash; guarded by the recorded order (S-group-3). A failed batch sync that lets the batch's responses go acknowledges lost writes (S-group-4). A rename inside a batch without the pending syncs first breaks the snapshot's barrier (S-group-5).
- **Sabotage:** S-group-1, S-group-2, S-group-3, S-group-4, S-group-5; shared: S-hostdisk-1, S-fsync-1
- **Verifiable here:** yes — the host suites, the control, the sabotages and the Compose run
- **Prediction:** Under the control's burst (three nodes in process, 64 writes in flight) the leader syncs under 0.5 times a write, against 1.00 on the reverted host, and its followers no more often than today. The soaks are identical to the count (27,910 elections, 1,335,746 entries committed, the same ten hardest histories), Core and the simulator untouched; every host suite, the Compose run with a killed leader and the checker green. **Observable:** the control's report, the soak reports, the CI run.
- **Outcome:** pending

### P14-04 — The curve against the restated criteria

- **Task:** Decision 5: interleaved in process against P14-01's head, five runs a side at 100, 625, 1,250, 2,000, 3,125, 3,500, 4,000, 5,000 and 6,250 writes a second, untraced; the decomposition traced at 625 and 3,125; the result against P14-02's criteria, met or attributed beside the floors.
- **Vacuity:** A batch counted per input rather than per sync hides whether grouping happened; guarded by every record's leader syncs per write. A curve compared with an older record measures the day; guarded by interleaving.
- **Sabotage:** ; manual: the comparison checked against the records' syncs per write and loop busy
- **Verifiable here:** partial — measured here; GitHub's runners are not the measuring machine
- **Prediction:** At 3,125 writes a second the leader syncs 0.3 to 0.6 times a write (the followers sync 0.29 to 0.44 at that rate today), its loop is 0.55 to 0.80 busy, and the median is 0.85 to 1.0 ms, meeting the restated median criterion in at least four runs of five; the 99th percentile falls to 4 to 12 ms and misses the tail criterion. At 625 and below the median is within 5% of the reverted host's (batches of one). The highest rate sustained in every run rises from 3,125 to 4,000 to 5,000. The remaining tail is attributed by the decomposition at 3,125 to the persists' segments and the machine's own pauses, not to the loop's queues. **Observable:** the records and the decomposition.
- **Outcome:** pending

### P14-05 — The broad targets checked against the harness's own runs

- **Task:** Decision 6: the shards' notices carry each entry's red neighbours; build-collect counts them over the run and requires every test red beside ten or more entries to be listed in `ci/broad-targets.txt` (`broad-run`, a member observer); the registry's `row` becomes a `currency` line; the register row closes.
- **Vacuity:** An observer reading its observation from the file compares the file with itself; guarded by S-broad-1. A run with a shard's neighbours missing counts less than the run did; guarded by requiring every head shard's notice, as the cost observer does.
- **Sabotage:** S-broad-1
- **Verifiable here:** partial — the counting on written notices here; the run's counts on GitHub
- **Prediction:** On the phase's first run with it, the tests red beside ten or more entries are the twelve listed and at most three more, each an aggregate over generated executions in the Simulation or Scale suites; no listed test falls under ten. **Observable:** build-collect's note on that run.
- **Outcome:** pending

### P14-06 — The margins tables tied to the commits they measured

- **Task:** Decision 7: per-row commits in the margins tables (the header's for every row, `# at <commit>: <checks>` overriding), `gates margins` reading the newest phase's table, and the `margins-commit` observer failing a phase report when a bench control's source changed in its phase after a row describing it was measured. The registry's `row` for `docs/phases/P12/margins.tsv` becomes a `currency` line on the newest table; the register row closes.
- **Vacuity:** An observer that reads a row's commit as current passes every table; guarded by S-margin-2. A check run on pushes would fail every host change until a re-audit and would be turned off; guarded by running it for reports only.
- **Sabotage:** S-margin-2
- **Verifiable here:** yes — on phase 13's table and this phase's commits
- **Prediction:** Applied to phase 13's report at `0abc3e4`, the observer passes the hand-off rows (measured at `7f4b02c`, after the control's last change) and fails every other bench row (measured at `3a1056b`, before `1f25f3d` and `7f4b02c` changed `src/Raft.Host/`): phase 13's own report would have needed the full audit again after its last host change. Applied to this phase before P14-07, it fails every bench row; after P14-07, none. **Observable:** the gate's output on each.
- **Outcome:** pending

### P14-07 — The margin audit after the host's changes

- **Task:** `scripts/margins.sh` at P14-04's head, in the three modes, five runs a side, with the group commit's control added as rows (its syncs per write, both sides); the table becomes this phase's.
- **Vacuity:** An audit at a commit before the host's last change describes another host; guarded by P14-06's observer.
- **Sabotage:** ; manual: `gates margins` over the table, and P14-06's observer over this phase's report
- **Verifiable here:** partial — the audit runs here; GitHub's runners give other margins, seen only when a control fails there
- **Prediction:** Every row keeps the audit's 1.5 on both sides. The hand-off control's other segments stay under 0.1 of the delay unpatched in every mode, the effect threads' coupling gone. The group commit's control keeps its syncs per write under a third of its bound unpatched and at 1.00 with S-group-1, in every mode. **Observable:** the table and `gates margins`.
- **Outcome:** pending

### P14-08 — The phase's cost in CI

- **Task:** The harness shards, the soaks and the per-commit matrix on the phase's first-attempt runs against phase 13's, the entries added and retired, whether every job was given a runner, and the never-verified commits tabled, failed apart from never ran.
- **Vacuity:** A run with a job that never ran compares less work. Guarded by counting only jobs with steps and stating any that never ran.
- **Sabotage:** ; manual: the comparison checked against each run's job list
- **Verifiable here:** no — CI's numbers only in CI
- **Prediction:** The phase adds nine entries (S-retire-1, S-group-1 to S-group-5, S-broad-1, S-margin-2, and none for P14-02) and retires three, a net six; no head shard passes 400 s against phase 13's slowest of 352; the soaks' work is identical to the count and their time within their runners' spread; every per-commit job is given a runner. **Observable:** the shards' slowest step, the manifest, the soaks' reports, the job counts.
- **Outcome:** pending

## Sabotage ids

New series: S-retire (P14-01), S-group (P14-03), S-broad (P14-05). S-margin-2 follows its series. Each
id's `sabotage/<id>/` entry lands in the same commit as the check it proves and is run on that commit
before it is pushed.

- **S-retire-1:** retiring an entry is accepted while its target still exists, so a live check can be
  silenced.
- **S-group-1:** every effect list executed alone, the batching undone.
- **S-group-2:** a batch's sends released as they are reached, before the batch's sync.
- **S-group-3:** a batch's files synced in reverse of the order they were first written.
- **S-group-4:** after a failed batch sync, the batch's responses released.
- **S-group-5:** a rename inside a batch run without the pending syncs first.
- **S-broad-1:** the broad-targets observer takes the listed tests as the observed set.
- **S-margin-2:** the margins observer reads each row's commit as current.

## Register rows

Promised here, at phase 13's acceptance: the leader's serial work per write (P14-03, P14-04), the
broad-targets premise (P14-05), the margins tables' premise (P14-06), and the target's figures
(P14-02). Each closes with its task. The rows promised to phase 0 (the person's walk) stay the
person's.
