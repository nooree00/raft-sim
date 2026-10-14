# Phase 15 — task breakdown

The leader's queue, measured first; the instruments and conventions the group commit moved (spec §11
phase 15, added at phase 14's acceptance; its done criterion is P15-00's to write, at this
breakdown's approval). Format as in `docs/phases/P14/breakdown.md`, parsed by `gates breakdown`;
outcomes will say `(evidence)` or `(forcing)`.

**Draft for review.** Nothing below is implemented.

**Where phase 14 left the latency.** The group commit moved the sustained rate in process from 3,125
writes a second to beyond 6,250, the curve's highest, and halved the median at 3,125 (1,973 µs against
the reverted host's 4,119; m:p14-04-ab-fix-3125-1 to -5, m:p14-04-ab-base-3125-1 to -5). Both restated
criteria are missed there (1,198 µs; 5,051 µs). The leader's loop stays 0.96 busy in batches of 1.9
inputs, because a batch is only what queued while the loop was busy. By the rule stated before
measuring, the median's gap against the floor's rate (723 µs) is the queue at the leader: commit-respond
+266 µs, leader-to-send +265, leader-queue-answer +111, leader-queue +95 (m:p14-04-traced-local-1250-1
to -5, m:p14-04-traced-local-3125-1 to -5). The tail's composition is unmeasured: the traced runs' 99th
percentile (41.6 to 51.7 ms) is mostly the trace's own cost, the generator sharing the hosts' process,
against 8.9 to 14.2 ms untraced.

**Two candidate mechanisms for the queue, told apart before anything is built.**
- *The batch's hold* (phase 14's hypothesis): a batch releases every output after its one sync. Under
  node-interface §4 an output may leave once every persist the node emitted before it is durable, so
  an output that no persist precedes in its batch could leave at the batch's start, and today it
  waits about one sync. An output after an earlier list's persist must wait under §4 as written; the
  order in which the loop hands a batch's inputs to the node decides which outputs are which.
- *The replication pipeline* (P11-02): the leader keeps one append in flight per follower, so a new
  entry's append is emitted only when the previous append's answer arrives. Under load an entry waits
  for that round trip before its append exists to be held at all.
Commit-respond and leader-to-send each grew by about one leader sync, which fits the first; leader-to-
send's growth fits the second as well. The records cannot tell them apart: the trace stamps an output
when it is released, not when the node emits it.

**Done when** (proposed for spec §11 by P15-00):
- **the load generator runs in a process of its own** for in-process clusters, its effect on the tail
  measured: a tail is read by P14-04's rule only from traced runs whose 99th percentile is within 1.5
  times the untraced runs' at the same rate;
- **the operating point is re-derived** (decision 4), the group commit's capacity measured;
- **the queue's mechanism is checked before any remedy:** each output's wait split into the time
  before the node emits it and the time it is held after, every held output classed by what it waits
  for, and a check stating the hypothesis shown failing on phase 14's code; a remedy is built only if
  the measurement attributes to it at least half of the median's queue growth, by a rule stated here;
  the curve then measured again, interleaved against phase 14's head: the criteria met, or the gap
  attributed segment by segment beside the floors;
- **the group commit's median above the reverted host's at 1,250 and 2,000 writes a second** (10 and
  17%) explained by the same measurement, or recorded as a second thing;
- **the broad-target rule replaced or retired**, with what breadth meant stated;
- **every bench control's margins measured again** after the host's changes; every row promised to
  phase 15 closed.

Carried forward from phase 14's acceptance:
- **an audit describes the commit it measured:** P14-06's observer fails a report whose margins rows
  predate a change to their controls; it runs on this phase's report as on every report since;
- **the instrument first** (the CRDT project's §13.30): a rule that reads a quantity through an
  instrument is not applied until the instrument's own effect on that quantity is measured;
- **a mechanism gets a check that fails on today's code before a remedy is built against it** (P11-01);
- **unexplained is reported as unexplained:** the 10 to 17% at 1,250 and 2,000 is carried, not
  smoothed;
- **a check that cannot fail is rejected; every input a gate reads is registered** (`gates inputs`);
  every comparison interleaved on one machine in one session against the head it changes.

## Ordering

The spec amendment first (P15-00). Then the instrument (P15-01), because every later number is read
through it. Then the capacity and the operating point (P15-02), because the queue and the tail are
judged at the operating point. Then the queue's mechanism (P15-03) and the tail's composition (P15-04),
both on phase 14's head, both before any remedy. Then the remedy, if P15-03's rule admits one (P15-05).
The broad-target rule (P15-06) is independent and may run beside any of these. The margin audit
(P15-07) after the host's last change, before the report; the cost in CI last (P15-08).

**Blocking set:** P15-00 to P15-07. P15-08 blocks nothing.

## Decisions for review

1. **Phase 15's done criterion** (P15-00), as above.
2. **The queue, measured first** (P15-03, P15-05). The trace gains an emission stamp for each send
   and response, taken when the node returns the effect; the decomposition splits commit-respond and
   leader-to-send each into the time before emission and the hold after it. The executor classes
   every output it holds: behind a persist of its own list (A), behind only an earlier list's persist
   (B), or behind none (C, held needlessly: §4 lets it leave at the batch's start). Untraced records
   carry the classes' counts and hold times. The hypothesis is stated as a check: in a batch whose
   first list has an output and no persist, that output leaves before the batch's first sync. It is
   written and run on phase 14's head before any remedy, and its failure recorded; it lands green with
   the remedy, or not at all. **The rule, stated now:** a remedy is built only if the holds of classes
   B and C account for at least half of the growth in commit-respond and leader-to-send together at
   the operating point over 1,250 writes a second (531 µs at 3,125 in phase 14's runs). Otherwise no
   remedy is built against the batch, and the rest is named by the split: the pipeline's wait or
   something else, a row. **The remedy, if admitted:** outputs that no persist precedes in their batch
   leave before the batch's writes (C), and the loop hands a batch's peer messages to the node before
   its client requests, in their arrival order per source, so answers' outputs come before the
   requests' persists (turning B into C). Core and the simulator unchanged. **Not chosen:** relaxing
   §4 to each list's own persists (a contract change, needing the simulator's world and a positive
   control of its own); a timer (adds latency at low load); a wider replication window (it reverses
   P11-02, and is not judged before the split says the pipeline is the queue).
3. **The generator in a process of its own** (P15-01): the three hosts in one process (`bench
   cluster`, traces on), the load generator in another, joined on loopback. The hosts' counters reach
   the record through a `Stats|` request on the client port (bench mode only, as `Trace|` is), so
   records keep every field they have today. The threshold for reading a traced tail, stated now:
   within 1.5 times the untraced 99th percentile at the same rate, both with the generator separate.
   If the traced tail is still beyond it, the trace's own cost in the hosts is next (it keeps one
   write in N), and the rule reads no tail until it is within. **Not chosen:** decomposing the
   untraced tail some other way (nothing else joins a write's path across the three hosts).
4. **The operating point** (P15-02). Either way the group commit's capacity is measured first, five
   runs a side at rates from 6,250 to 18,750 writes a second, generator separate.
   - **Proposed: pinned to 3,125 writes a second, absolute, with the reason recorded.** A latency
     criterion judges remedies at a load. Pinned to a capacity, the operating point moves whenever a
     remedy moves the capacity, so the criterion would change under the remedy it is judging. That is
     the circularity P14-02 refused for the queueing model, whose service time depends on the design
     under test. 3,125 is also where every curve since phase 10 was measured. The capacity is reported
     beside it with no criterion, as the sustained rate is.
   - **Not proposed, yours to choose:** half the newly measured capacity, restated whenever a phase
     moves the capacity, with the allowance (one leader sync) measured again at the new point and the
     floors unchanged.
5. **The broad-target rule** (P15-06). **What breadth meant** (P3-01): a test that almost any behaviour
   change turns red, so an entry targeting it is caught whatever its patch breaks, and must say why it
   fails (a reason or a control). P14-05 observed breadth from the harness's own runs. Since P11-09
   those runs expose a test only to its own class's patches, so breadth across the code cannot be
   observed there at any threshold. What the rule measured is reach within a class. **Is breadth still
   worth detecting?** The few cross-class broad tests (the golden values, the coverage and checker
   aggregates, the allowlists) are chosen by reading them and change rarely; a judgement list held as
   a decision in the registry is enough. **Proposed:** retire the broad-run observer; keep
   `ci/broad-targets.txt` as that judgement list, a `decision` input; and define the within-class
   property under its own name, *class reach*. A test red beside at least half of its class's other
   entries, and at least four, is specific to none of them, so an entry targeting it needs a reason or
   a control. It found such a target with no reason stated: S-member-2's, red beside 3 of 6 sibling
   patches. The three tests P14-05 added move to the class-reach list. **Not proposed, yours to
   choose:** retire both, the within-class property unguarded.

## Tasks

### P15-00 — The spec's phase-15 row

- **Task:** Spec §11's phase-15 row gets its done criterion (decision 1). The register's rows promised to phase 15 cite the row.
- **Vacuity:** A done criterion that names a remedy rather than a check would admit building before measuring; guarded by the criterion naming the split, the classes and the check before any remedy, and by `gates register`, which reads the phase row.
- **Sabotage:** ; manual: the diff checked against the decisions (§11 only)
- **Verifiable here:** partial — the diff and the register gate here; its approval the reviewer's
- **Prediction:** The amendment changes §11's phase-15 row and no other section. `docs/design/node-interface.md` needs no change in the phase: both parts of decision 2's remedy are within §4 as written. **Observable:** the diff, and the phase's diff of node-interface.md.
- **Outcome:** pending

### P15-01 — The load generator in a process of its own

- **Task:** Decision 3: `bench cluster` runs three hosts in one process with traces on; `bench load --nodes` from another process drives them on loopback and fetches their counters with `Stats|` and their traces with `Trace|`; `bench.sh load-local` and `load-ab` gain the separate mode, and the record names whether the generator shared the hosts' process. Measured on phase 14's head at 3,125 writes a second, five runs each way, interleaved: in process and separate, traced and untraced.
- **Vacuity:** A separate mode that quietly runs in one process measures the old instrument; guarded by the record's process ids, the cluster's from `Stats|` against the generator's own (S-proc-1). Counters read once and differenced against nothing give totals, not a run's share; guarded by a test comparing `Stats|` deltas with the in-process counters over the same run (S-proc-2).
- **Sabotage:** S-proc-1, S-proc-2
- **Verifiable here:** yes — the host suites and the interleaved runs on this machine
- **Prediction:** Separate, untraced, the median at 3,125 is within 5% and the 99th percentile within 25% of in process (1,973 µs, 9.2 ms in phase 14's runs). Traced, the generator's lateness at its 99th percentile falls under 3 ms (31 to 40 ms in process), and the traced 99th percentile comes within 1.5 times the untraced, so the tail rule may read it. The trace's cost inside the hosts keeps the traced median 5 to 15% above the untraced. **Observable:** the records' latency, generator lateness and process fields.
- **Outcome:** pending

### P15-02 — The group commit's capacity, and the operating point

- **Task:** Decision 4: phase 14's head, generator separate, untraced, five runs at 6,250, 7,500, 9,375, 12,500, 15,625 and 18,750 writes a second; the highest rate sustained in every run. The operating point declared in `docs/design/performance-target.md` as decided, with its reason, the capacity beside it.
- **Vacuity:** A capacity read past the generator's own limit measures the generator; guarded by the records' generator lateness and offered-against-dispatched rate at each point, a rate whose generator falls behind reported as the generator's.
- **Sabotage:** ; manual: the highest sustained rate checked against each run's completed rate and 99th percentile
- **Verifiable here:** partial — measured here; GitHub's runners are not the measuring machine
- **Prediction:** The highest rate sustained in every run lies between 9,375 and 12,500 writes a second. At 6,250 the loop was 0.98 busy at 0.33 syncs a write; as batches grow the sync's share of a write shrinks, and the serial work per write falls toward the handling and the sends, about 80 to 100 µs. Past it the leader's loop, not the generator, is the limit: generator lateness at its 99th percentile under 5 ms at every rate up to the capacity. **Observable:** the records.
- **Outcome:** pending

### P15-03 — The queue's mechanism, measured before any remedy

- **Task:** Decision 2: the trace's emission stamp and the decomposition's split of commit-respond and leader-to-send into before emission and hold; the executor's classes A, B and C with counts and hold times in untraced records; the hypothesis's check, run on phase 14's head and its failure recorded. Measured on phase 14's head, generator separate: traced at 1,250, 2,000 and the operating point, untraced at the same rates, five runs each. The rule of decision 2 applied, and the regression at 1,250 and 2,000 set against the same split.
- **Vacuity:** A split that stamps emission and release at the same moment shows no hold whatever the code does; guarded by a recording-file-system test where a planted sync delay must appear in the hold and not before emission (S-held-1). A classifier that calls every hold needless inflates the case for a remedy; guarded by a test of a batch with one output of each class (S-held-2).
- **Sabotage:** S-held-1, S-held-2
- **Verifiable here:** yes — the host suites and the runs on this machine
- **Prediction:** The check fails on phase 14's code: an output that no persist precedes in its batch leaves after the batch's sync, about one sync late. At 3,125, of the leader's client responses held at all, 25 to 40% are held needlessly (C) and 30 to 50% behind only an earlier list's persist (B), each held about one sync (250 to 320 µs). Holds of B and C account for 55 to 80% of the growth in commit-respond and leader-to-send together over 1,250 (531 µs), so the rule admits the remedy. The rest is leader-to-send's time before emission: the pipeline's wait for the previous append's answer, 80 to 200 µs of growth. At 1,250 and 2,000 the same holds account for at least half of the group commit's median excess over the reverted host (117 and 191 µs). If they do not, that excess is a second thing, a row. **Observable:** the split, the classes, the check's recorded failure.
- **Outcome:** pending

### P15-04 — The tail's composition on phase 14's head

- **Task:** Decision 3's threshold applied to P15-01's and P15-03's runs at the operating point; if it holds, P14-04's tail rule (the persists, the loop's queues, the rest, for the writes over the tail criterion) applied to the traced runs there, before any remedy.
- **Vacuity:** A rule applied to a traced tail beyond the threshold reads the trace again; guarded by the threshold stated here and checked first, from the records.
- **Sabotage:** ; manual: the threshold checked against each traced run's 99th percentile and the untraced runs'
- **Verifiable here:** partial — measured here; the reading depends on P15-01's threshold holding
- **Prediction:** The threshold holds. The rule reads (ii): the loop's queues (leader-queue, leader-queue-answer, commit-respond) hold more than half of the tail's excess over the median write, the persists 0.15 to 0.30 of it, the generator's lateness under 0.10. At 0.96 busy the queue's bursts, not the sync's own variance, make the tail. **Observable:** the records' tail shares.
- **Outcome:** pending

### P15-05 — The remedy, if P15-03's rule admits it

- **Task:** Decision 2's remedy, built only on P15-03's admission: outputs that no persist precedes in their batch released before the batch's writes; a batch's peer messages handed to the node before its client requests, each source's order kept. The hypothesis's check lands green with it. The curve interleaved against phase 14's head, generator separate, five runs a side at 100, 625, 1,250, 2,000, 3,125 and the operating point if it differs; traced at 1,250 and the operating point; both rules read as in P15-03 and P15-04. If the rule did not admit it, the task closes with P15-03's measurement and builds nothing.
- **Vacuity:** An early release that lets an output leave ahead of a persist before it breaks §4 while every latency improves; guarded by the barrier test on a recording file system extended to the early outputs (S-early-2). A reorder that swaps two messages from one peer breaks the node's assumptions while counts look right; guarded by a test of a batch with two messages from one source (S-early-4).
- **Sabotage:** S-early-1, S-early-2, S-early-3, S-early-4; shared: S-group-2, S-hostdisk-1
- **Verifiable here:** yes — the host suites, the sabotages, the soaks and the Compose run in CI
- **Prediction:** Admitted and built: at 3,125 the median falls by 200 to 400 µs, to 1.55 to 1.80 ms, still missing 1,198; commit-respond's hold falls to under 100 µs at its median; leader syncs per write move by under 0.05. The median at 1,250 and 2,000 comes within 5% of the reverted host's (P14-04's 1,113 and 1,101 µs). The 99th percentile at 3,125 falls 10 to 30%. The soaks are identical to the count; Core and the simulator untouched; the Compose run and the checker green. **Observable:** the records, the soaks, the CI run.
- **Outcome:** pending

### P15-06 — The broad-target rule replaced or retired

- **Task:** Decision 5: P14-05's broad-run observer retired through P14-01's mechanism (S-broad-1 retired with its target); `ci/broad-targets.txt` a `decision` input, its twelve judgement entries kept; the class-reach list and observer (`class-reach`: red beside at least half of the class's other entries, and at least four), the three P14-05 additions moved to it, its registry line a `currency` line; or, if the reviewer chooses, both retired.
- **Vacuity:** An observer reading its observation from its own list compares the list with itself; guarded by S-reach-1. A class-reach rule that counts an entry's own target as its neighbour reaches every test; guarded by the test that the entry's own method is excluded, carried from P14-05.
- **Sabotage:** S-reach-1
- **Verifiable here:** partial — the counting here; the run's counts on GitHub
- **Prediction:** On the phase's first run with it, class reach finds exactly the three tests P14-05 found and listed, and no others, the reds being deterministic in the patches; the retired observer's notice parts become the class-reach notice unchanged in size (largest under 3,100 characters). **Observable:** build-collect's note.
- **Outcome:** pending

### P15-07 — The margin audit after the host's changes

- **Task:** `scripts/margins.sh` at the phase's last host change, in the three modes, five runs a side, with rows for any new timing or counting control (P15-01's process check, P15-03's classes if they gate anything); the table becomes this phase's, read by P14-06's observer at the report.
- **Vacuity:** An audit at a commit before the host's last change describes another host; guarded by P14-06's observer, which fails the report.
- **Sabotage:** ; manual: `gates margins` over the table, and `gates margins --currency` at the report
- **Verifiable here:** partial — the audit runs here; GitHub's runners give other margins, seen only when a control fails there
- **Prediction:** Every row keeps the audit's 1.5 on both sides; the group commit's control stays under a third of its bound unpatched and at 1.00 with S-group-1; if P15-05 is built, the group commit's syncs per write move by under 0.01. **Observable:** the table and the gates.
- **Outcome:** pending

### P15-08 — The phase's cost in CI

- **Task:** The harness shards, the soaks and the per-commit matrix on the phase's first-attempt runs against phase 14's, the entries added and retired, whether every job was given a runner, and the never-verified commits tabled, failed apart from never ran.
- **Vacuity:** A run with a job that never ran compares less work; guarded by counting only jobs with steps and stating any that never ran.
- **Sabotage:** ; manual: the comparison checked against each run's job list
- **Verifiable here:** no — CI's numbers only in CI
- **Prediction:** The phase adds nine entries if P15-05 is built (S-proc-1, S-proc-2, S-held-1, S-held-2, S-early-1 to S-early-4, S-reach-1) and five if not, and retires one (S-broad-1); no head shard passes 400 s against phase 14's slowest of 367; the soaks' work is identical to the count; every per-commit job is given a runner and none fails for a reason other than its own commit's code. **Observable:** the shards' slowest step, the manifest, the soaks' reports, the job list.
- **Outcome:** pending

## Sabotage ids

New series: S-proc (P15-01), S-held (P15-03), S-early (P15-05), S-reach (P15-06). Each id's
`sabotage/<id>/` entry lands in the same commit as the check it proves and is run on that commit
before it is pushed.

- **S-proc-1:** the load record calls the generator separate without comparing the two processes' ids.
- **S-proc-2:** the hosts' counters taken from one `Stats|` reading, not differenced over the run.
- **S-held-1:** an output's emission stamp taken at its release, so no hold appears.
- **S-held-2:** every held output classed as needless.
- **S-early-1:** outputs no persist precedes held until the batch's sync again (the remedy undone).
- **S-early-2:** an output after a persist in its batch released before the batch's sync.
- **S-early-3:** a batch's inputs handed to the node in arrival order again (the reorder undone).
- **S-early-4:** two messages from one peer handed to the node out of their order.
- **S-reach-1:** the class-reach observer takes its own list as the observed set.

## Register rows

Promised here, at phase 14's acceptance: the median's miss at the operating point as the queue at
the leader (P15-03, P15-05), the tail's miss and the instrument that read it (P15-01, P15-04), the
operating point pinned to a moving quantity (P15-02), the broad-target rule whose premise P11-09
removed (P15-06), and the group commit's median above the reverted host's at 1,250 and 2,000 writes a
second (P15-03). Each closes with its task. The rows promised to phase 0 (the person's walk) stay the
person's.
