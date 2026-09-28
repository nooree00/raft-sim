# Phase 2 — report

**Status: submitted for review** (`docs/phases/status.md`: `P2: in progress`). Phase 0 stays
`in progress`: its one open row is your cold walk.

This report certifies the commit that contains it. `gates reports` verifies that commit's CI run
on the next push; the run ids below are for earlier commits.

## Done criteria

| Criterion (spec §11 and the amendments at P1 acceptance) | Where it is shown |
|---|---|
| The harness is sharded, each shard under its 15-minute ceiling | P2-01: four shards of 272–430 s on GitHub (run 36381210815); `ShardPlanTests` |
| Every coverage dimension is an effect the algorithm depends on | P2-02: 15 effect dimensions; `CoverageTests`; the traceability table's "stressed by" column uses only these names, and a test ties them |
| The three events at zero appear above the floor | reordered at a crash 5, isolated 83, all down 70 of 200 (floor 3); reachable by construction in `TheThreeEventsPhaseOneNeverProducedAreReachable` |
| Every Core input and effect is driven by a simulator test | P2-03: `InterfaceCensusTests.EveryCoreInputAndEffectReachesTheWorldInSomeRun` measures use in test traces, not existence |
| WGL rejects every known-bad history | P2-04/05: `KnownHistoryTests`, `WglCheckerTests` (11 rejected histories, each with an accepted twin one operation away) |
| WGL agrees with the brute-force oracle on random small histories | P2-06: 1500 histories, 66% linearizable, no disagreement |
| WGL rejects a broken non-Raft store's simulated histories | P2-09: store (a) 27/40, store (b) 7/40 with the state-placed crash; the correct store (c) 0/40 in both schedules |

## What exists

| Task | Delivered | Sabotages (all give their expected result) |
|---|---|---|
| P2-01 | `gates sabotage --shard i/n`, n = ⌈entries / 28⌉ from `ci/sabotage-shard-size.txt`; `ShardPlan` refuses a stale n; build-core + `sabotage i/n` + `build` (collect); the per-commit matrix is commit × shard; ceiling 15 min; every CI script marks its checkout safe | S-shard-1..3, S-ci-2 (S-each-3/4, S-pre-4 regenerated) |
| P2-02 | 15 effect dimensions; floor 3 of 200; ≥ 95% must be declared always-on; identical execution sets fail; state-placed faults `CrashWhenInFlight`, `Isolate`, `CrashAll`, `CrashMajority`, traced `UNFIRED` if they never fire | S-cov-3..6, S-rare-1, S-rare-2 |
| P2-03 | Census of Core input and effect types over test traces; the client path (Submit → REQUEST → RESPONSE); write-at, rename, delete under every crash mode; the rename-undo bug, test first | S-iface-1..3 (S-iface-1 reinstates the bug) |
| P2-04 | Anomaly taxonomy (`docs/design/anomaly-taxonomy.md`); three empty categories filled; a near-miss twin for every reject | S-hist-6, S-hist-7 |
| P2-05 | `WglChecker`: DFS with memo on (linearized set, model fingerprint), per-key decomposition, a state budget with Undecided, the smallest failing sub-history | S-wgl-1..3 |
| P2-06 | Differential test against the oracle; constructed generator with two in three mutated | S-diff-1, S-diff-2 |
| P2-07 | Cost in states explored, both sides of the budget, the table below | S-wgl-4 |
| P2-08 | Simulated clients n101+ over the same network path as nodes; timeouts → indeterminate; client log text form, byte-identical from the schedule | S-client-1..3 |
| P2-09 | Stores (a) async primary-backup, (b) ack-before-durable, (c) durable single node; KV codec; history with timed-out clients as fresh logical clients | S-store-1..3 |
| P2-10 | `WglChecker.Signatures` (`linearizability@key`, `undecided@key`) as the shrinker's signature; both stores' failures shrunk | S-shrink-4 |

114 sabotage entries: 110 run by the harness in four shards, 4 (`runner: host`) by
`scripts/host-sabotages.sh`. Test projects: Architecture 38, Checker 130 (P1: 53), Gates 80 (55),
Simulation 100 (56).

## CI

- **P2-01, first push: red**, run [36374522047](https://github.com/nooree00/raft-sim/actions/runs/36374522047). Every `sabotage i/n` job failed in under a second on "dubious ownership": the new shard jobs ran git before anything marked the checkout safe. Not reproducible with my local wrapper, which marks it; reproduced in a container with a copy owned by another uid. Fixed in `c8a5124` (every CI script marks its checkout safe, `CiScriptTests`, S-ci-2). Findings log.
- **The fix: green**, run [36381210815](https://github.com/nooree00/raft-sim/actions/runs/36381210815) at `c8a5124`. Shards 1–4: 297, 430, 272, 340 s total; fixed cost 177, 232, 177, 144 s. The whole run took 8.7 min wall clock, against 15.7 min for P1's unsharded build job.
- **P2-02..P2-10, the S-det-1 fix and the outcomes: green**, run [36461204951](https://github.com/nooree00/raft-sim/actions/runs/36461204951) at `5ddcf9f`, 23 min wall clock, 50 jobs, all passing. The four head shards took 403–635 s as whole jobs (including container start). `each-commit-list` named 10 commits, and the matrix ran 40 `commit SHA i/n` jobs, one per (commit, shard), of 288–621 s each. `each-commit` collected one result per pair.
- **Local, before each push:** preflight, build, gates, tests, the harness (all shards in turn), host sabotages, secret scan, README walk, and every non-head commit's own full checks. `gates reports` needs the GitHub API and runs only in CI. At the P2 head, the four shards took 306–416 s, with fixed costs of 95–211 s.
- **Landing P2-01..P2-03 first paid off inside the phase.** The first local harness run on the finished stack found two sabotages not giving their expected result (below). One, S-rare-1, had never been caught on its own commit. The per-commit matrix exists because of P2-01, and it would have turned that commit red on GitHub. Had the checker work gone first on an unsharded harness verified only at the head, S-rare-1 would have been one more green entry proving nothing.

## Predictions

Ten predictions, all evidence, one with a forcing clause I do not count:

- **Evidence (10):** right 1 (P2-05), partly 5 (P2-01, P2-04, P2-07, P2-09, P2-10), wrong 4 (P2-02, P2-03, P2-06, P2-08).
- **Forcing:** none whole. P2-06's second clause, that the constructed generator gives 25–75% linearizable, became the test's vacuity guard; the first construction gave 77%, and I changed the mutated share to bring it inside. It is excluded from the count above.

The instructive ones:

- **P2-02**, wrong in direction: restating "delayed" as an effect made it *rise* (128 → 187), not fall. The injection count had under-counted, because it counted only the Delay fault. And a state-placed crash with two writes in flight still gives reordered writes in only 5 of 81: two in flight is necessary, not sufficient, because the Reordered loss must also keep the later write and drop the earlier one.
- **P2-07**, the one-key half: decomposition matters only for a history the search must exhaust. A linearizable one-key history of 200 operations took 200 states, the same as over 20 keys, because the first path found a linearization.
- **P2-10**, wrong for a good reason: the store-(b) failure shrinks to one event, the state-placed crash. Phase 1's timing enablers (5 events against 3) were a property of time-placed crashes, not of shrinking. A crash placed by state needs nothing to put it in its window. For phase 3, a shrunk schedule with a time-placed fault may still carry enablers; one with a state-placed fault should not.

## The checker's cost (P2-07)

States explored; U = undecided at the suite's 10⁵ budget.

```
200 operations, one key, ending in an unexplainable read:
  put     c=1:599 c=2:1589 c=3:3953 c=4:9441 c=5:21921 c=6:49857 c=7:U
  append  c=1:U
  mixed   c=1:595 c=2:1892 c=3:5920 c=4:15881 c=5:46053 c=6:U
mixed, concurrency 3, linearizable: 20 keys 200 states, 1 key 200
mixed, concurrency 3, one unexplainable read: 20 keys 162 states, 1 key 5920
```

One run at 10⁶: Put-only reaches 246,785 states at concurrency 8, mixed 883,438; Append-only is
undecided even at concurrency 1. For phase 3: bound Appends per key, or spread them over many keys,
where decomposition keeps rejection cheap.

## Findings (Phase 2 section of `docs/findings.md`)

- **A coverage list derived from the injection mechanism cannot contain an event the mechanism does not name** (your general form). P2-03's census measures use, not existence, and is the pattern going forward. On its first run it found a gap in its own scenario list: `PersistTruncate` was driven by none of the runs chosen to exercise every member.
- **The rename bug is a recorded failure**, as you asked. Its test was written first and run red against the old `SimDisk`, and the red output is in the findings log. The fix and the test landed together, with S-iface-1 reinstating the bug on every push. A red commit on the branch would have broken "every commit green".
- **An equivalent mutant:** forcing indeterminate operations to be linearized does not change any verdict, because an unchecked output can always be placed last. S-wgl-1 now excludes them instead, and is caught.
- **Append at scale is infeasible** for a search whose state is the whole string: undecided at 10⁵ states from concurrency 1 on a 200-operation key. Phase 3's workload should keep Appends per key short, or the checker needs a smaller Append state.
- **Two history-modelling rules.** A client that times out becomes a fresh logical client, as in Knossos: its abandoned operation may still take effect after its next one begins. Real-time order is strict: a client that issued its next operation at the instant its reply arrived made its own operations concurrent, and gave the checker room to explain anomalies away. Store (a)'s rejection rate doubled (13 → 26 of 40 fault-free runs) once clients waited one unit. `History.Problems` caught the overlap on the first recorded history.
- **State-placed faults remove timing enablers** (P2-10 above).
- **Two broken sabotages that fail in opposite directions.**
  - *S-det-1 was caught 35 runs in 36, since P1-08.* The check proving determinism was itself nondeterministic: it relied on a string hash .NET randomizes per process. A flaky sabotage is worse than a flaky test. A test that passes wrongly gets investigated, while a sabotage that survives once reads as "the guard has a gap" and sends the search to the wrong place. It now writes the hash into the trace, so it is caught with certainty. Rule: a random element in a patch needs a reason why it cannot come out equal.
  - *S-rare-1 never caught its target.* The cause is process, not code: I chose the target by reading, and ran the harness only at the stack head. It now targets the check that exercises `MinPending` directly. By your count this is the fourth time, across the two projects, that choosing a target by reading rather than running aimed a sabotage at the wrong thing.
  - *The general form, and it scales badly:* a sabotage verified only at the stack head is verified against the final state of the code, not against the commit it belongs to. Every intermediate commit is unverified. The manifest only grows, so the gap grows with it. The per-commit matrix closes it in CI; locally, the stack's harness must run on each commit, not once at the head.
- **P2-06's forcing clause is the failure mode the classification exists to catch.** I changed the generator to bring its rate inside the range I had predicted. That made the prediction a target rather than a claim, and it reads more like a success than any other outcome does.
- **A stale sabotaged binary after a manual check** gave me a wrong result once. Rule: rebuild after every manual revert.
- **The first P2-01 push was red on GitHub** (above).

## Your question: are other targets chosen the same way?

Partly I know, partly I don't. So there is a register row.

- **What I know.** Every target is watched failing. The harness requires each entry's named target to go red under its patch. At the head, all 110 harness entries do, and CI now shows it on every commit. S-rare-1's defect, a target that passes under the patch, cannot hide any more: the harness reports it as `survived`, which is how it was found.
- **What I don't know.** Whether each target fails *because of the mechanism the sabotage describes*, or only incidentally. The harness checks that the target goes red, not why. Many entries list the golden trace among their red neighbours, and it goes red at almost any simulator change. A target that fails only through a side effect of the patch proves as little as S-rare-1 did. I have not audited the 110 entries for this, and several targets were chosen by reading.
- **Lineage:** the CRDT project's §13.31 shape (two mechanisms, one observable, the weaker substituting silently), now in the sabotage manifest itself.
- **Register row (P3, open):** audit each sabotage's target for failing on the described mechanism, not incidentally. For each, show a variant of the patch that keeps the incidental change but removes the mechanism, and check the target then passes. Any target that still fails is retargeted or given a narrower patch.

## Deviations from the breakdown

- **Store (b)'s floor** is the coverage floor (3 of 40), not the predicted "more than half": measured 7 of 40, for the reason in P2-09's outcome. The test asserts the floor and that the crash raises the rate above the default mix (0).
- **Anomaly categories are not classified automatically.** Each known-bad history carries its category by hand, and a test requires every category to be non-empty and every reject to have a twin.
- **Unsharded commits get one `1/1` job** in the per-commit matrix: a commit from before the sharding cannot run `--shard`.
- **`System.Char` added to the simulator's ambient allowlist** (hex text in the client log; no ambient state).
- **`Raft.Simulation.Tests` references `Raft.Checker`** (P2-09/10 check simulated histories). The layering test and the lock file record it. No new package.
- **The rename test** was shown red and recorded, then landed with its fix and a sabotage, rather than as a red commit.
- **The cost table runs at a 10⁵ budget** in the suite, because it runs in every Checker sabotage. One run at 10⁶ is recorded below the table.
- **The differential generator** mutates two histories in three, not one in two, to meet the vacuity guard (P2-06, forcing).

## Open items for you

1. Your cold walk of the README (P0).
2. Deleting `claude/blissful-goodall-358smj-sabotage` (refused here with 403).
3. Branch protection: required checks `build`, `each-commit`, `secrets`, `readme-walk`, unchanged in name. `build` is now the collect job over `build-core` and the shards; `each-commit` collects the commit × shard jobs.
4. Larger runner: not pursued (billed as far as I can tell; additive only if free).
