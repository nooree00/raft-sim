# Phase 5 — report

**Status: submitted for review.** The checker's accepting half (spec §11 phase 5): P5-00 to P5-07,
with P5-00 added first at approval, P5-06 at the checker's ceiling and P5-07 at the harness's. The reviewer's decisions during the
phase are applied: decision 3's declaration path closed (an undecided search always fails), the
checker's budget set to what the enforcing runner holds in usable memory, a structural measure of
checking cost promised to phase 6, and one recorded known limit, **KL-1**, approved in this report.

This report certifies the commit that contains it. `gates reports` checks that commit's CI run on
the next push.

## The result of the phase

### Phase 4's limit is closed by linearizability alone

Phase 4 decision 3 left a stated limit: a node that applied the wrong entry and answered
consistently with what it applied passes every phase-4 check. P5-04 planted three broken variants
of `RaftNode` and ran each over the 300-execution sample:

| Variant | Runs with a phase-4 invariant red | Rejected by linearizability |
|---|---|---|
| applies the entry after the committed one, answers consistently | 0 | 288 of 300 |
| answers a write when it appends, before commit | 0 | 3 of 300 |
| refuses a client after appending its command | 0 | 283 of 300 |

No invariant went red in any run of any variant. Every catch was linearizability's. The three
early-answer catches were each examined: a write answered at append and lost before commit, the
leader crashed once (seed 28) and was deposed twice (seeds 37, 288).

### The checker's limit is real and measured

On the six-key workload, 5 of 10,000 soak histories exhaust a million states. Reductions that are
sound (per-key budgets, indeterminate reads removed, indeterminate appends no observation depends on)
decide none of the five: of the 46 indeterminate appends on those keys, 31 were observed by a read
and so took effect. The cost is not predicted by any per-key count (60,000 keys measured: one
undecided key had no observed indeterminate append; the costliest decided key had no indeterminate
append at all). The budget is therefore what the runner holds: **32,000,000 states, 12.5 GB on the
GitHub runner** with flat time per state (40,000,000 needs 14.9 GB and is already slower; 48,000,000
swaps; 56,000,000 is killed). Four of the five decide within it. The fifth is recorded:

**KL-1, approved here.** Seed 7723, key k3 (46 operations, 20 indeterminate, 17 of them appends,
16 observed by a read), sub-history digest `39ee3213…97cf`. Undecided at every measured budget: 2,
4, 8, 16 and 32 million states locally (12 GB at 32 million), 8 to 48 million on the GitHub runner
(14.9 GB at 40 million, swapping at 48 million). Promised to the register's structural-measure row
(phase 6). Its linearizability is unknown. It is the only recorded known limit; any other
undecided search fails the soak, and KL-1 itself fails the soak if its history changes or it becomes
decidable (spec §6).

## Done criteria

| Criterion (spec §11 phase 5, and the breakdown's additions) | Where it is shown |
|---|---|
| The checker accepts the histories a correct cluster produces | P5-03: the 300-execution sample, every history well formed and accepted; the soak: 9,999 of 10,000 accepted, 0 rejected, 1 unverified (KL-1) |
| …including indeterminate operations | P5-02: three exact constructions (timed out then committed, timed out then truncated, retried and committed twice), each accepted by the checker and the oracle; indeterminate operations in 95.5% of soak histories |
| Phase 4 decision 3's limit closed | P5-04, above |
| The accepting half keeps its vacuity guard | P5-04's three variants rejected; P5-06's planted histories; the content floor (every operation kind, concurrency on a key, retried writes) in the sample and the soak |

## Predictions

**Evidence 8, forcing 0.** Right 1, partly 6, wrong 1. Each outcome is in the breakdown.

| Task | Outcome | In one line |
|---|---|---|
| P5-00 | partly | the rule finds the known collisions; the register accepted any method as evidence (fixed), not textually as predicted; the gate caught its author's own S-reg-3 |
| P5-01 | wrong | the same-client variant changes no verdict: the checker orders by responses only; it breaks the structural check in 287 of 300 |
| P5-02 | partly | constructions accepted; spec §6's "as failed" and "dropped" are one transformation here |
| P5-03 | partly | sample accepted, contents above the floor; the budget's tail at 38%, not a tenth |
| P5-04 | partly | (1) right; (2) rare as predicted, a leader deposed as well as crashed |
| P5-05 | partly | checking is 580 s of the soak's 1,364 s locally, not under a fifth; (i)-(iv), added before each step, all right |
| P5-06 | partly | the count right (none of five decided), the reason a third of the cases; the planted twin caught my own rule's wording |
| P5-07 | right | every shard under 600 s (slowest 530 s, S-ran-2's); fixed cost 81-191 s without S-ran-2; outside command baselines it did not move |

## What exists

| Task | Delivered | Sabotages |
|---|---|---|
| P5-00 | A sabotage id owned by one task across every breakdown (`; shared:` for another's); register evidence must be a test | S-bd-7, S-bd-8, S-reg-5 |
| P5-01 | `ClientHistory`: the client log as a checker history | S-adapt-1..3 |
| P5-02 | Indeterminate constructions in `ManualCluster`, with a client log; the naive treatments measured | S-indet-1, S-indet-2 |
| P5-03 | The checker over every soak history; history contents with a floor; `Delete` in the workload | S-lin-1, S-lin-2 |
| P5-04 | Three broken variants of `RaftNode`; a forty-seed linearizability test as their harness target | S-lin-3..5 |
| P5-05 | Per-key budgets; indeterminate reads removed; six keys; undecided always fails; the budget measured on the runner; named hardest seeds (`Raft.Budget.Tests`); recorded known limits | S-wgl-5..8, S-lin-6, S-soak-6, S-kl-1..3, S-reg-6 |
| P5-06 | Indeterminate appends no observation depends on, removed; eight planted histories | S-wgl-9..12 |
| P5-07 | Whole-suite harness entries narrowed to the projects their mechanism needs (`scripts/ci-test.sh [project...]`, `testcount --projects`); an unknown name and a CI step given a list both fail; the fixed cost's split printed per worker | S-count-4, S-count-5 |

**Test projects** (at the start of phase 5 in brackets): Architecture 44 (42), Gates 113 (108),
Checker 167 (130), Simulation 106 (106), Core 116 (98), Scale 16 (11), Budget 3 (new at P5-05: the
budget's named seeds, apart because they take a minute and up to 1 GB each).

## Deviations and choices for the reviewer

- **Six keys instead of three** (P5-05, reviewer's order): hard histories rarer, contention per key lower.
- **A new test project, `Raft.Budget.Tests`:** a harness entry runs its target's whole project, so the
  named seeds in `Raft.Scale.Tests` would have cost every scale entry minutes, four workers at once
  beyond the runner's memory.
- **Seed 3044 is not a named seed**, though it is the hardest decided (21,232,606 states, 4 minutes,
  5 GB): five harness entries then ran the whole suite (P5-07 narrowed them), and the first full local run measured shard 1 at
  825 s with it (about 19 minutes on GitHub at the usual ratio, over the ceiling). It is met by every
  soak at the same budget, where an undecided search fails the run. The named seeds are 9302, 8260
  and 7625.
- **Raft.Core.Tests and Raft.Scale.Tests reference Raft.Checker** (P5-01; layering test updated).
- **The probe branch** `claude/blissful-goodall-358smj-probe` measured the runner (reviewer-approved).
  It could not be deleted from this session (the session's git access refuses ref deletion); it is
  inert and is the person's to delete.

## CI and its cost

The full local run of P5-07's code (every stage, on a tree that differs from this commit in
documentation only; the stages documentation can affect were re-run on this commit) and of the
phase-5 head before P5-07, KL-1 included, harness shards side by side. Both trees were measured
before the commits were reordered to put every report change in this commit, so their hashes are
not in the branch. Local seconds; GitHub has run at about ×1.39.
Fixed cost is the harness's own figure (until the slowest worker reaches its first entry); entry
cost is the rest. Before P5-07 the split was not printed: "whole-suite baseline" there is inferred
from the four shards that hold no whole-suite entry (fixed cost 92-192 s).

| Shard | Before: total | fixed | entries | After: total | fixed | entries | After: slowest worker's fixed cost |
|---|---:|---:|---:|---:|---:|---:|---|
| 1 | 736 s ⚠ | 487 s | 249 s | 467 s | 81 s | 386 s | build 47 s, test baselines 33 s |
| 2 | 878 s | 470 s | 408 s | 377 s | 191 s | 186 s | build 59 s, `Raft.Scale.Tests` 119 s, preflight 10 s |
| 3 | 664 s | 442 s | 222 s | 349 s | 130 s | 219 s | build 51 s, `Raft.Scale.Tests` 75 s |
| 4 | 495 s | 111 s | 384 s | 530 s | 275 s | 255 s | build 51 s, `ci-test.sh Raft.Scale.Tests` 209 s (S-ran-2) |
| 5 | 370 s | 93 s | 277 s | 500 s | 160 s | 340 s | build 50 s, `Raft.Scale.Tests` 107 s |
| 6 | 582 s | 446 s | 136 s | 457 s | 112 s | 345 s | build 52 s, `Raft.Scale.Tests` 57 s |
| 7 | 491 s | 92 s | 399 s | 287 s | 88 s | 199 s | build 51 s, test baselines 36 s |
| 8 | 420 s | 192 s | 228 s | 346 s | 81 s | 265 s | build 50 s, test baselines 31 s |

- ⚠ Shard 1 before: two builds of mine ran beside it; an upper bound, not a measurement.
- The after shards do not hold the same entries as the before shards: S-count-4..5 shifted the
  round-robin (S-ran-2 moved from shard 2 to 4). Shards 4-8 after ran on a fresh container (the
  session's container restarted after shard 3).
- **Slowest shard 530 s locally, about 12.3 minutes on GitHub** of the 15-minute ceiling (P4's
  measurement to beat: 12.6). Before P5-07 three shards projected over it (15.4, 17.1, 20.3 min).
- **Outside command baselines the fixed cost did not move:** baseline build 34-59 s per worker,
  setup 0-5 s, test-project baselines up to 119 s (`Raft.Scale.Tests`) and 97 s
  (`Raft.Budget.Tests`). Design 1 removed the whole-suite baselines, about 350 s each, which sat
  inside "fixed cost". The P6 fixed-cost row is untouched, and now has its split (register).
- **The soak:** 10,000 executions, 9,999 accepted, 0 rejected, 1 unverified (KL-1), no other search
  undecided; 1,364 s locally, checking 580 s of it.
- **The whole local run:** about 92 minutes after P5-07 (the restart gap removed) against about 118 before it, and 65 at phase 4. The per-commit matrix runs in CI only.

## Findings added this phase

In `docs/findings.md` under Phase 5: an identifier that resolves to the wrong thing (sabotage ids,
register evidence, and the gate catching its author); a correct fix for a wrong reason, the reason
measurably wrong (per-key budgets); identity by bytes failing twice in one phase; a feature that makes under-testing expressible
shipping with its guard (P5-07); "fixed cost" holding the whole-suite baselines it was meant to exclude.

## Still the person's

- Delete the probe branch `claude/blissful-goodall-358smj-probe`.
- The soak as a required check in branch protection; the cold walk of the README; the sabotage
  branch; P0.
