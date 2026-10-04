# Phase 6 — report

**Status: for review.** Membership changes by joint consensus (spec §11 phase 6): P6-00 to P6-15,
without P6-02 (promised to phase 11). P6-00 to P6-09 were accepted at P6-10's measurement; the
reviewer's three decisions then are applied: the membership soak is a soak of its own (P6-12), the
generator is adjusted and not declared (P6-11), and S-ran-2 is narrowed (P6-13). Two tasks were
added by what the work found: P6-14 (a spare's initial configuration) and P6-15 (a torn record
followed by a later one). One recorded known limit, **KL-2**, is approved in this report, against
the membership soak only.

This report certifies the commit that contains it. `gates reports` checks that commit's CI run on
the next push; from this report on it requires the `soak-membership` job as well as `soak`.

## The result of the phase

### Joint consensus under partition preserves every invariant

The membership soak, 10,000 generated executions of five nodes (three configured, two spares) with
membership requests among the clients' operations, the soak's faults over all five:

- no invariant violated (27,694 elections, 28,862 membership requests);
- 3,817 executions completed a change, 765 of them while a partition or an isolation was in force,
  1,861 removed the leader, 243 crashed a node in a joint configuration, 1,154 ended still joint;
- 9,999 histories linearizable, 0 rejected, 1 undecided (KL-2, below);
- every effect between 1.39% and 87.16% of executions, none at 95% or more, every floor cleared.

The baseline soak is unchanged in what it runs: three nodes, its workload, KL-1. Its 300-execution
report is byte-identical before and after the soak's code moved into a shared run (P6-12).

The positive control (P6-09): a node that changes directly from `C_old` to `C_new` elects two
leaders in one term in the Figure 10 construction ("term 2: elected n3 at 360 and n4 at 668"), and
in 12 of 300 generated executions (4%, re-measured after P6-14); the real node elects one.

### What the measures got wrong, and what they found

**P6-07's measure was wrong four times before it was right**, each found by reading the cases it
flagged, never by changing it until it passed:

1. It counted adoptions by servers with no live leader (120 with the rule on), which the rule
   allows and which is how a cluster recovers.
2. It attributed a later term record to an earlier delivery of a request that was retried after the
   leader went quiet (21, seed 194).
3. It described the runs without the rule as the same executions replayed, and counted them only on
   the seeds where the rule's run had a removed server stand. They are other executions: the rule
   changes who leads, and so what is removed and when.
4. After P6-14 quieted the spares, it counted the rule's window in the simulation's time; a node
   whose clock runs 20% fast waits 150 of its own units, 125 of the simulation's (seed 84).

With all four fixed: over 200 executions with removals, a removed server stood in 27 and no server
with a live leader adopted its term; without the rule, a removed server stood in 27 of the same
seeds' executions and its terms were adopted 1,580 times, in all 27.

**The checker wrong about a correct node, the running count: four.** P3 twice (the silent
step-down, the in-flight grant), P4 once (the barrier-release message), and P6's: invariant 6 asked
a committed entry to stay on a quorum of the configuration it was committed under, forever (seed
152 of the first membership sample); it now asks a quorum of the latest committed configuration.

**This phase also ran the other way: generated executions found the node wrong, twice.**

- **P6-14.** `RaftNode` took itself plus its peers for its initial configuration, so a spare, given
  the three members, believed it belonged to a four-server configuration and stood from the start
  of every membership execution. `ManualCluster` never ticked its spares. Every membership
  measurement from P6-07 to P6-10 ran with it, and it is why split-vote and requestvote-ignored sat
  at 100% in P6-10's measurement: the reviewer's §13.30 reading (a rate at 100% means the other case
  cannot be produced) was right, one layer deeper than the generator.
- **P6-15.** The membership soak's first local run stopped at seed 1462 on a node that refused to
  start. It had recovered a torn term-vote record, issued the cut and then a record, and a crash kept
  the record and lost the cut, which the disk model allows (any subset of writes in flight
  survives) and P3-08's fix assumed away. The node never learns what is durable, so no order of
  issue helps; recovery now tells a torn write from corruption by structure (a valid record follows
  a torn one within a record's length; a corrupted whole record has no such successor), for both
  files. Before the fix every torn length refused: availability lost, no data.

**The isolation detector counted only a leader's isolation** (P6-11): it asked that every peer had
sent to the isolated node, which an isolated follower's peers have no reason to do. 0.1% of
membership executions and 10.8% of baseline ones counted; 31.3% and 30.9% once the node's own view
decides. Isolation during a change, where joint consensus earns its keep, was effectively untested
before; it is in 30.16% of the membership soak.

### KL-2, approved here

Seed 8741 of the membership soak, key k5: 34 operations, 22 of them indeterminate; sub-history
digest `081204a0…087f`. Undecided at every budget measured locally: 2,000,000 states (575 MB,
14 s), 4,000,000 (1,008 MB, 29 s), 8,000,000 (1,864 MB, 61 s), 16,000,000 (3,655 MB, 137 s),
32,000,000 (7,188 MB, 314 s). The GitHub runner's curve was not measured. Promised to the register's
structural-measure row (phase 11). Recorded in `ci/known-limits-membership.txt` and judged only by
the membership soak: the two soaks run the same seeds, and seed 8741's key k5 was undecided in
P6-10's measurement too, with another history (30 operations). Its linearizability is unknown. Any
other undecided search fails the membership soak, and KL-2 fails it if its history changes or it
becomes decidable.

## Done criteria

| Criterion (spec §11 phase 6, and the breakdown's additions) | Where it is shown |
|---|---|
| Joint consensus under partition preserves every invariant | The membership soak above: 10,000 executions, 765 changes completed under a partition, no violation; the constructions of P6-05 and P6-06, each with a step where only one configuration's majority is reachable |
| The invariants stated over a quorum of the configuration in effect, both during joint consensus | P6-04: every checker counts the configuration in effect at the index (or the candidate's, for elections), against hand-built traces before any joint-consensus code; invariant 6 corrected at P6-08 |
| Linearizability keeps holding with membership changes | The membership soak: 9,999 of 10,000 accepted, 1 unverified (KL-2) |
| The phase's positive control | P6-09: the direct change elects two leaders, in the construction and in 4% of the sample |
| The register rows promised to phase 6 | `each-commit-list`'s fallback: done (P6-00). The harness's fixed cost: re-promised to phase 7 (below). The structural measure: phase 11 |

## Predictions

**Evidence 14, forcing 1.** Right 5, partly 5, wrong 5. Each outcome is in the breakdown.

| Task | Outcome | In one line |
|---|---|---|
| P6-00 | partly | the range right; the mechanism moot (`rev-list old..new` already meant it); "tests unchanged" wrong |
| P6-01 | wrong | the queueing was 3-20 s per shard, not at least 20 in every scale shard; the cost is S-ran-2's baseline |
| P6-03 | right | the cached configuration survived the truncated entry, the one test red |
| P6-04 | right | the latest-configuration variant accepts trace (b), after my first trace (b) failed to separate them |
| P6-05 | wrong (forcing) | writing `Configuration.IsQuorum` first removed the trap the prediction named |
| P6-06 | wrong | three to five never needs the new servers; the replacement does |
| P6-07 | right | no adoption with the rule, adoptions in every execution without it, once the measure was right (four errors) |
| P6-08 | wrong | changes under a partition the commonest dimension (149 of 300), not the rarest |
| P6-09 | partly | the construction red at once; the sample catches the direct change in 3-4%, not under 1% |
| P6-10 | partly | states explored down 50% (decided), not up under 10%; a new undecided search, right |
| P6-11 | wrong | isolation 47% of membership executions, not 6-14%; the baseline's up 20 points; split votes at 100% for a reason I did not guess |
| P6-12 | partly | no violation, one undecided search, right; seed 8741 survived the generator change; the CI ratio still to read |
| P6-13 | right | S-ran-2 8.8 s locally; the GitHub half from this push's run |
| P6-14 | partly | the construction failed as predicted, the two effects fell; P6-07's test went red on the measure's fourth error |
| P6-15 | right | both recoveries refused at every torn length; fixed; the baseline soak's executions unchanged, KL-1 holding |

## What exists

| Task | Delivered | Sabotages |
|---|---|---|
| P6-00 | `each-commit-list` fetches a force-pushed previous head by SHA and fails rather than widens | S-range-1, S-range-2 |
| P6-01 | The harness's fixed cost measured per worker on GitHub; nothing changed | — |
| P6-03 | A configuration is a log entry, in effect on append, recovered from the log | S-cfg-1, S-cfg-2 |
| P6-04 | Every checker counts quorums of the configuration in effect | S-joint-1..3 |
| P6-05 | Joint consensus in `RaftNode`: `C_old,new`, `C_new`, a removed leader steps down, one change at a time (`busy|`) | S-member-1..3 |
| P6-06 | New servers catch up as non-voting members before `C_old,new` | S-member-4 |
| P6-07 | Removed servers' candidacies measured against the disruption rule | S-member-5, S-disrupt-4 |
| P6-08 | The simulator with more nodes than the configuration; membership requests in the workload; membership dimensions | S-cov-11, S-joint-4, S-adapt-4 |
| P6-09 | Figure 10, constructed: the positive control | S-member-6 |
| P6-11 | Isolation counted as the node saw it; network fault rates per cluster, not per link | S-cov-12, S-gen-1 |
| P6-12 | The membership soak: a profile of the soak's shared run, its own CI job, floors and known limits; `gates register` reads both files | S-soak-7..9, S-kl-4, S-reg-7 |
| P6-13 | S-ran-2 on `Raft.Core.Tests` | S-ran-2 (narrowed) |
| P6-14 | `NodeContext.Members`: a spare knows it is outside the configuration | S-member-7 |
| P6-15 | Both recoveries skip a torn record a valid one follows | S-pstate-4, S-logfile-4 |

**Test projects** (at the start of phase 6 in brackets): Architecture 46 (44), Gates 118 (113),
Checker 167 (167), Simulation 108 (106), Core 141 (116), Scale 16 (16), Budget 3 (3), Membership 3
(new at P6-08).

## Deviations and choices for the reviewer

- **A sample-only declaration in the membership soak** (P6-12): writes completed out of order at a
  crash, two log writes in flight on one node when it crashes, is 139 of 10,000 in the membership
  soak (1.39%, over the floor) and 2 of its 300-execution sample (floor 3). It is declared rare in
  the membership sample only, by P3's mechanism, and the 10,000 must still clear 1%. The baseline's
  rules are untouched. The alternative is a generator change for five nodes only; I did not make it.
- **The harness's fixed-cost row re-promised to phase 7.** P6-01 found the queueing small and
  P6-13 removed S-ran-2's baseline; what remains is each scale shard's `Raft.Scale.Tests` baseline
  check (86-131 s on GitHub), which no test closes. Keep it at phase 7, or drop it.
- **The disk model and real file systems.** P6-15's case exists because a simulated append lands
  at the end of whatever file survives the crash. On a real file system the append's offset is fixed
  when it is issued, after the cut, and the same crash would leave a clean file. The node is fixed
  for the model, which is the contract; whether the model should place appends at their issue-time
  offset is a question for the disk-acknowledgement row (phase 10), where I added the data point.
- **The membership sample runs in its own project** (P6-08), so its 300 executions are not a cost
  of every scale harness entry.
- **Every membership measurement before P6-14 ran with the spares campaigning.** The numbers in
  P6-07 to P6-10's outcomes are as measured then; P6-14's outcome gives the re-measured ones.
- **Branch protection:** `soak-membership` is a new required job through `build`; adding it to
  branch protection is yours, beside `soak`.

## CI and its cost

- **The membership soak, locally:** 1,295 s for 10,000 executions, checking 405 s of it (KL-2's
  search 314 s). Its CI job has a 40-minute timeout (the baseline's: 30).
- **The baseline soak, locally, on this phase's code:** 1,457 s for 10,000 executions, checking 604 s (KL-1's search the most of it); every invariant, KL-1 matching, the same 27,880 elections and 1,785,551 operations as P6-10's measurement of the same seeds; isolation 30.02% with P6-11's detector.
- **The harness:** 256 container entries in 9 shards (28 per shard); S-ran-2's command baseline 5 s, from 281 s on GitHub.
- **The full local run without the soak** passed on this commit before the push; its stage times
  go to the reviewer with the push. The per-commit matrix and both soaks run in CI only.
- **P6-12's third number** (the membership soak job against the baseline soak job, within one run)
  and P6-13's GitHub half are read from this push's run.

## Findings added this phase

In `docs/findings.md` under Phase 6: the first membership sample found the checker wrong (invariant
6); a sabotage that removes a dimension can fail an invariant first (S-cov-11); a measure that goes
red on correct code is read case by case (P6-07, three errors, then a fourth); a distribution at
100% was a bug, not a fact about five servers (P6-14); the isolation detector counted only a
leader's isolation; the membership soak found the node wrong (P6-15); and the running count.

## Still the person's

- `soak-membership` beside `soak` in branch protection; delete `prerewrite-b96fc4b` and the probe
  and sabotage branches; the cold walk of the README; P0.
