# Phase 7 — report

**Status: accepted** (reviewer, on run 37299866345: the head green on every harness shard, the
build and both soaks; one per-commit job red, below). Decisions at acceptance: KL-1's removal and
KL-3 approved, KL-1 described as vanishing, not becoming decidable (the history no longer occurs);
the red commits accepted, not rewritten (listed under "Commits never verified in CI"); the harness
shards moved from the local run to CI only, the fast stages kept local (AGENTS.md, spec §12), with
its cost stated: a wrong-reason sabotage or a shard over its ceiling is found after the push; both
floor declarations approved; the §10 limit test kept out of the suite with its manual result; the
harness fixed-cost row dropped, closed with the measurement that killed it. The reviewer's reading
of the phase's finding: invariants check the log, linearizability checks the answers, and nothing
checked the state machine until the replay, a third oracle neither subsumes (P5-04 generalised).

Log compaction (spec §11 phase 7): P7-00 to P7-12, as approved. Three deviations from the
breakdown's order: the snapshot record format landed with the checkers (P7-04), not with the file
(P7-05), because the checkers had to read a compacted file; compaction in `RaftNode` and
InstallSnapshot landed in one commit (P7-06, P7-07), because no construction with a follower left
behind can run without InstallSnapshot; and P7-09's dimensions landed with P7-11's soaks, because
their floors are measured there. One recorded known limit, **KL-3**, is approved in this report,
against the membership soak; **KL-1** and **KL-2** are removed.

This report certifies the commit that contains it. `gates reports` checks that commit's CI run on
the next push.

## The result of the phase

### A compacted node and an uncompacted one agree, and the soaks run with compaction

The done criterion (P7-08), checked against the committed entries, never against another node:
every node's key-value state, at every restore and at the end of every incarnation, equals a replay
of the entries committed in fact, from the empty state, to the index the node had applied.

- **The agreement sample** (100 executions, n1 never compacting, n2 and n3 every 20 applied
  entries): 849 states compared, no disagreement; an install in 79 executions, a restart from a
  snapshot in 78; every history linearizable.
- **The baseline soak with compaction** (10,000 executions, every node compacting every 20 applied
  entries): no invariant violated; 90,310 node states compared with the replay, no disagreement;
  10,000 histories linearizable, **none undecided**. Compacted in every execution, a follower
  installed a snapshot in 9,431, a node restarted from a snapshot in 7,161, a crash met a compaction
  in flight in 752, an install kept the suffix in 1,229 and discarded it in 63.
- **The membership soak with compaction** (10,000 executions of five nodes): no invariant violated;
  112,095 states compared, no disagreement; 9,999 linearizable, 1 undecided (KL-3, below); a
  compaction during joint consensus in 274, an install in 8,461, a restart from a snapshot in 6,287.

The positive control (P7-10): a node that compacts past its commit index turns State Machine Safety
and No Spurious Commit red in the construction, where the next leader overwrites the compacted
entry, and is caught in 120 of 300 generated executions (40%). The real node holds in both.

### What the phase found

**Invariants over what was applied do not check what the state is.** Every log invariant compares
ghost ids: which entry a node applied at an index. A restore that drops one key (S-agree-1) applies
the right entries and holds the wrong state: over 100 executions the invariants went red in none,
linearizability in 7, the replay comparison in 97. The positive control told it from the other
side: caught by the invariants in 18 of 300 executions, by the replay in 118. Compaction is the
first mechanism that moves state without applying entries, so it is the first the ghost-id
invariants cannot see; the replay now runs in every soak execution. This is the phase's finding.

**The rename barrier held only one side.** P7-00 held a rename until the writes before it were
durable and released the writes behind it as soon as it was issued. The compaction crash test went
red on its first run with the barrier: a crash kept a truncation computed for the compacted file and
lost the rename, and the truncation cut the old file (P7-05). The world now holds every write after
a rename until the rename is durable; the barrier test checks both sides. The model's leniency pair
from the breakdown's approval (P6-15 and the rename) is in findings, and this is its sequel: the
fix restricts the world, not the model, and is part of the simplification spec §8 records.

**The running count: the checker wrong about a correct node seven times, the node wrong twice.**
Phase 7 added three, all in how the checker read a world that holds writes: a follower's restore
counted before the held rename that installs it (P7-06, the scale suite); a follower's applied
entries whose appends were held, while the checker's intended log followed the disk's issue order
(P7-08, the agreement sample; `IssuedObservation` was documented as the node's decision and
recorded at the disk's issue); and the membership soak's seed 434 (P7-11), where the checker copied
a crashed node's other files before the crash's own report of what the disk kept, so a file the
crash removed survived in its view and a later file of the same name was read after the stale
bytes. Each was found by generated executions, none by the hand-built traces. The world was wrong
once (the one-sided barrier). The node was not found wrong by a generated execution this phase.

### KL-1 and KL-2 removed, KL-3 approved here

With compaction on, every history changed. **The baseline soak has no undecided search**: KL-1's
seed (7723) now yields a history decided in 168 states (key k3: 47 operations, 22 indeterminate,
50 states). This is not KL-1's history becoming decidable: that history no longer occurs, because
compaction changes when every write and message happens. There is nothing to re-record and no
curve to take; the file is empty, and the soak fails on any undecided search.

**KL-3**, seed 8741 of the membership soak, key k5 (the same seed and key as KL-2): 34 operations,
22 indeterminate; sub-history digest `7dbd0893…51b8`. Undecided at every budget measured locally:

| States | Peak memory | Time |
|---|---|---|
| 8,000,000 | 1,864 MB | 60 s |
| 16,000,000 | 3,653 MB | 128 s |
| 32,000,000 | 7,189 MB | 287 s |
| 40,000,000 | 8,719 MB | 376 s |

Its memory curve is within 3% of KL-2's on the GitHub runner at every budget (1,805, 3,606, 7,139
and 8,673 MB), at the same operation count. Seed 8741's key k5 has now been undecided with three
histories (P6-10's 30 operations, KL-2's and KL-3's 34). Promised to the register's
structural-measure row (phase 11), whose prediction is about concurrency width; a key undecided
across three histories of the same seed is a data point for it. Its linearizability is unknown.

## Done criteria

| Criterion (spec §11 phase 7, and the breakdown's additions) | Where it is shown |
|---|---|
| A compacted node and an uncompacted node agree on state | P7-08: the construction, the agreement sample (849 states), and both soaks (90,310 and 112,095 states), each compared with the replay of the committed entries |
| Every property the log satisfies enumerated before anything removed data, and each said to be preserved or how | P7-02: `docs/design/log-properties.md`, ten properties, six beyond §8's four, each with a test over generated and compacted logs |
| The snapshot replaces the log atomically, through a crash at every point | P7-05: the crash test with compactions, 1,800 crashes, the rename in flight at 126; without the barrier 25 fail |
| The checkers judge the logical log, from the simulator's coverage, not the node's claim | P7-04: coverage as ghost ids recorded at compaction; four hand-built traces and two observation tests |
| InstallSnapshot under reordering, duplication, loss and stale chunks | P7-07: four constructions on a follower fed by hand, one in a cluster |
| The phase's positive control | P7-10: red in the construction, 40% of the sample |
| Compaction in both soaks, the known limits re-measured | P7-11: both soaks above; KL-1 removed, KL-2 replaced by KL-3 with its curve |
| The register rows promised to phase 7 | Key-value snapshots: done (P7-03). The configuration in the snapshot: done (P7-06). The harness's fixed cost: argued again with the measurement (below) |

## Predictions

**Evidence 12, forcing 1.** Right 8, partly 2, wrong 3. Each outcome is in the breakdown.

| Task | Outcome | In one line |
|---|---|---|
| P7-00 | right | the control showed nothing until the disk model was strict: 0 of 60 lenient, 15 of 60 strict, 0 with the barrier |
| P7-01 | right | KL-2 undecided on the runner at every budget to 40,000,000, peaking at 8.7 GB |
| P7-02 | right | six properties beyond §8; the chain check broke first, by the predicted mechanism, at P7-04; the configuration part not observed |
| P7-03 | right | the canonical-bytes test failed first, the round trip passed |
| P7-04 | wrong | Leader Append-Only went red first, then Log Matching and Entry Uniqueness; committed-durable never did |
| P7-05 | wrong | not green on the first run (the one-sided barrier); the rename in flight at 7.0% of crashes, not under 5% |
| P7-06 | wrong (forcing) | the named construction passed: P7-05's log store already read the last term from the snapshot, written because the prediction named it |
| P7-07 | right | the stale chunk written into the newer snapshot's file and the install discarded; the key by index and term fixed it |
| P7-08 | right | no disagreement in the sample; S-agree-1 caught by the replay in 97, by linearizability in 7 |
| P7-09 | partly | the discarded-suffix install the rarest in both samples; below the floor in one of them |
| P7-10 | right | red on State Machine Safety in the construction; caught in 40% of the sample |
| P7-11 | right | the undecided searches changed identity, not number (baseline 1 to 0, membership 1 to 1); no invariant violated |
| P7-12 | partly | the ready time fell by 40 s, more than predicted, but not for the reason given: one worker runs the baseline, contending with the others' entries; the total rose |

## What exists

| Task | Delivered | Sabotages |
|---|---|---|
| P7-00 | The world holds a rename until every earlier write is durable, and every later write until the rename is; the disk leaves an empty file for a rename whose source was lost | S-barrier-1..4 |
| P7-02 | The log's ten properties, each a test over generated and compacted logs | S-logprop-1, S-logprop-2 |
| P7-03 | Key-value snapshots: canonical bytes, restore replaces | S-kvsnap-1, S-kvsnap-2 |
| P7-04 | The snapshot record in `entries.log`; the checkers over the logical log, coverage as ghost ids | S-compact-1, S-compact-2 |
| P7-05 | Compaction as one uniquely named file and one rename; the crash test with compactions | S-snapfile-1, S-snapfile-2 |
| P7-06 | Compaction in `RaftNode` at `SnapshotThreshold`; the configuration in the snapshot; restart from a snapshot | S-compact-3, S-compact-4 |
| P7-07 | InstallSnapshot in chunks, keyed by the snapshot's index and term; codec messages | S-install-1, S-install-2 |
| P7-08 | The agreement check against the replay of committed entries, in constructions, a sample and both soaks | S-agree-1 |
| P7-09 | Compaction dimensions read from the receiving node's disk | S-cov-13 |
| P7-10 | The positive control (`CompactPastCommit`, off in every real configuration) | S-compact-5 |
| P7-11 | Compaction in both soaks; KL-1 removed, KL-3 recorded; the checker's crash-files defect fixed | S-soak-10, S-compact-6 |
| P7-12 | Every entry in a baseline unit; a barrier mode to measure contention | S-harness-4 |

**Test projects** (at the start of phase 7 in brackets): Architecture 46 (46), Gates 119 (118),
Checker 167 (167), Simulation 110 (108), Core 815 (141), Scale 18 (16), Budget 3 (3), Membership 3
(3). Core's growth is mostly the property tests over compacted logs (640 cases).

## Deviations and choices for the reviewer

- **Two declarations in the soaks' floors (P7-11).** An install that discarded the suffix is 63 of
  10,000 in the baseline soak (0.63%) and 26 in the membership soak, below the 1% rate floor; it is
  declared below the soak floor (never below 3), with the construction that exercises it directly
  (`AFollowerWhoseLogDisagreesWithTheSnapshotDiscardsIt`), and rare in the membership sample (0 of
  300). An install by a follower is declared always-on: 95.3% of the baseline sample, 94.3% of its
  soak. The alternative for the first is a generator change to make followers diverge past
  snapshots more often; I did not make it.
- **The §10 limit test for the threshold is not in the suite (P7-06).** At the bound, 2,047
  commands of 1 MB in a 2,146,500,576-byte log, recovery, compaction and the compacted file's
  recovery succeeded in one manual run, in 45 s with an 8.7 GB peak. The harness runs whole test
  projects on four parallel workers, and four such runs exceed the machine. The suite keeps the
  bound's refusal test. Keep it out, or give it a project of its own outside the harness.
- **The threshold's bound depends on the command size.** At the largest command (33 MB), the
  threshold may be at most 64: its entries must fit the log file, which recovery reads into one
  array. Entries past the threshold that are not yet applied are not bounded by it; that was true of
  the whole log before compaction.
- **`CompactPastCommit` is a production option that exists for the positive control**, as
  `DisruptionRule` exists for P3-06's measurement.
- **The harness's fixed-cost row** (P7-12): its premise was out of date (one worker per project per
  shard since P4-12); the barrier that removes the remaining contention makes the shard slower.
  Re-promised to phase 8 with the measurement; keep it there, or drop it.
- **The two-way barrier is part of the simplification spec §8 records** (decision 1): a real file
  system sends a write made after a rename to the new file, so the world holding it is the model's
  need, not reality's.

## CI and its cost

- **The baseline soak with compaction, locally:** 1,162 s for 10,000 executions (1,457 s at
  phase 6), checking 399 s (604 s): KL-1's undecided search is gone.
- **The membership soak with compaction, locally:** 1,363 s (1,295 s at phase 6), checking 561 s
  (405 s), most of it KL-3's 287 s search.
- **The harness:** 273 container entries in 10 shards (28 per shard; 9 at phase 6).
- **P7-12, measured on shard 7 locally, both from cold builds:** normally, the `Raft.Scale.Tests`
  baseline 76 s, the slowest worker ready at 137 s, the shard 539 s; with every baseline check done
  before any entry, 42 s, 97 s and 552 s. The contention is real, but removing it costs more than it
  saves, so the harness is unchanged. What is left of the fixed cost is the price of running a
  target's own project once before its entries, beside the other workers. I argue the row again,
  re-promised to phase 8, for you to keep or drop.
- **The first push of this phase was red** (run 37278405824, on `96d2fe1`): both soaks green, every
  other job green, and harness shard 10 over its 15-minute ceiling, about 1,000 s, at the head and at
  the two commits before it. Both causes were mine. P7-10's control sample ran all 300 executions to
  count catches; under S-dur-2, whose patch also cuts compacted files, those executions slowed until
  the test took 10 minutes, and S-dur-2's entry, which runs the Scale project's tests as its
  neighbours, took 713 s (122 s at P7-04). The sample now stops once the control is caught three
  times (2 s); the 300-execution measurement stays in the breakdown. And P7-11 re-picked the budget
  tests' seeds with the second-hardest decided history (7248, 7,607,718 states), which made the
  budget project's baseline check 174 s locally; it is left out with 3044, for the same reason, and
  9886 takes its place. My full local run had shown shard 10 at 879 s of 900, and I pushed on it.
  The fixes are two commits before this revision of the report. The red run stays in the branch's
  history: those commits are pushed, and I have not rewritten them.
- **The full local run without the soaks took 105 minutes** (6,317 s on the first push's head), past
  the 90 minutes at which AGENTS.md says it comes back to you. Ten harness shards (nine at phase 6)
  and the per-shard baseline checks of `Raft.Scale.Tests` and `Raft.Budget.Tests` are most of it.
  That decision is yours: a shorter local run, or the run as it is.
- **The full local run without the soaks** passed on this commit before the push. The per-commit
  matrix and both soaks run in CI only.

## Findings added this phase

In `docs/findings.md` under Phase 7: the model's leniency as the dangerous direction (P6-15 and the
rename, the pair); the barrier that held only one side; invariants over what was applied do not
check what the state is (the phase's finding); "issued" meant two things, and they came apart when
the world began to hold writes; a construction that passes against the bug it targets tests
nothing.

## Still the person's

- `soak-membership` beside `soak` in branch protection; delete `prerewrite-b96fc4b`, the probe
  branches (`claude/blissful-goodall-358smj-probe-kl2` among them) and the sabotage branch; the cold
  walk of the README; P0.

## Commits never verified in CI

Accepted, not rewritten (reviewer, at acceptance): the commits are pushed, their fixes are forward,
and a rewrite would cost another round of the force-push range problem. Each listed job is harness
shard 10 failing with its step at about 16.5 minutes, over the 15-minute ceiling. The job logs
could not be read from this environment (GitHub serves them from another host); the cause was
reproduced locally instead: S-dur-2's entry at 713 s, from P7-10's control sample running to the end
under that sabotage, and P7-11's budget seed. No other run will check these jobs: the per-commit
matrix covers only a push's new commits.

| Commit | What it is | Jobs with no passing verdict |
|---|---|---|
| `5008e44` | P7-10 positive control | shard 10 over the ceiling (run 37278405824) |
| `749fdff` | P7-12 harness guard | shard 10 over the ceiling (run 37278405824) |
| `96d2fe1` | P7 report (that push's head) | harness shard 10 over the ceiling (run 37278405824) |
| `0d3fa98` | the budget-seed fix | shard 10 over the ceiling (run 37299866345): the sample fix is the next commit |

