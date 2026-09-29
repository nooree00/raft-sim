# Phase 3 — report

**Status: awaiting review.** `docs/phases/status.md` says `P3: in progress`, and stays that way
until you accept the phase.

This report certifies the commit that contains it. `gates reports` checks that commit's CI run on
the next push. The run ids below are for earlier commits.

## Done criteria

| Criterion (spec §11 phase 3, and the breakdown's additions) | Where it is shown |
|---|---|
| Invariants 1, 8, 9 and 11 hold across 10,000 seeded executions with partitions (asymmetric ones included) and crashes | P3-08: `soak` job, seeds 1..10,000, faults generated until tick 12,000, run to 20,000: no violation, 25,855 elections. `ci-soak.sh` refuses a report covering fewer executions. `gates build-collect` requires exactly one passing `soak` job. On GitHub: run [36532629155](https://github.com/nooree00/raft-sim/actions/runs/36532629155) at `e069129`, where the soak job took 4 min 16 s |
| The fsynced-then-lost positive control goes red | P3-07: `PositiveControlTests`. **Election Safety / Vote Uniqueness:** red in 39 of 200 state-placed runs, naming n2. **Term monotonicity:** names n2 in 200 of 200. **Twin:** the same crash without the synced loss is green in all 200. |
| The distribution is reported | Below, as effects (P2-02's rules), 300-execution sample beside the 10,000 |
| Every harness entry's target goes red on its sabotage's mechanism, not incidentally | P3-01: `docs/design/sabotage-audit.md`. Every entry names its `mechanism:`. A broad target needs a `reason:` its failure message must contain, or a `control.diff` under which it stays green (`ABroadTargetNeedsAReasonOrAControl`). The register row is done. |
| The message codec's canonical form is decided and tested | P3-02: `docs/design/node-interface.md` §8a. `MessageCodecTests.EveryByteStringThatDecodesIsTheEncodingOfWhatItDecodesTo`. The register row is done, and a new P9 row covers corruption detection. |

## What exists

| Task | Delivered | Sabotages (all give their expected result) |
|---|---|---|
| P3-01 | Sabotage-target audit of all 120 entries. The harness prints each caught target's own failure message. `reason:`, `control.diff`, `mechanism:`, `ci/broad-targets.txt`. S-cov-5/6 repaired, S-shrink-4's target asserts, S-det-3 and S-rng-1 retargeted to tests of their own mechanism | S-audit-1..3 |
| P3-02 | `MessageCodec`, canonical both ways, for the four RPCs | S-codec-1..3 |
| P3-03 | `TermVoteLog`: length, payload, FNV-1a checksum. A torn final record is truncated; any other checksum or length failure refuses to start | S-pstate-1..3 |
| P3-04 | `ElectionInvariants`: Election Safety (on "elected", with acting-without-quorum and elected-never-acts as their own failures), Vote Uniqueness (sent grants plus durable votes), Term monotonicity, Liveness. The simulator's `Observe` stream: Sent, Delivered, Durable, Issued, Crash, Start | S-inv-1..9, S-obs-1 |
| P3-05 | `RaftNode`: election, persist-before-send (the self-vote included), tests first | S-elect-1..6 |
| P3-06 | The §6 disruption rule; a one-way-partition simulation | S-disrupt-1, S-disrupt-2 |
| P3-07 | `CrashAfterWrite`, a state-placed fault after a write completes; the positive control and its twin | S-pos-1..3 |
| P3-08 | `SoakTests` (300 in the suite, 10,000 in `soak`), `scripts/ci-soak.sh`, the `soak` job required through `build`, spec §12 amended | S-soak-1, S-soak-2, S-cov-7 |
| P3-09 | `ElectionInvariants.Signatures`, `SimRun run --raft`, `ShrinkTests` | S-shrink-5 |

**Sabotage entries:** 151 in all. 146 are run by the harness in 6 shards, and 5
(`runner: host`) by `scripts/host-sabotages.sh`.

**Test projects** (counts at the start of phase 3 in brackets, `46ac18d`): Architecture 40 (38), Checker 130 (130), Gates 98 (95), Simulation 105 (102), Core 58 (new).

## CI

- **P3-01..P3-05: green**, pushed earlier in the phase. The last of those runs was run [36508890040](https://github.com/nooree00/raft-sim/actions/runs/36508890040) at `58f3620`.
- **P3-06..P3-09 and the outcomes: green.** Run [36532629155](https://github.com/nooree00/raft-sim/actions/runs/36532629155) at `e069129`: 52 jobs, all passing, 27 min wall clock.
  - **Harness:** six head shards of 7.4–11.5 min each.
  - **Soak:** 4 min 16 s, run in parallel with the harness.
  - **Per-commit matrix:** `each-commit-list` named 7 non-head commits, and 39 (commit, shard) jobs ran them.
- **Local, before each push:** everything CI runs except `gates reports`, which needs the GitHub API. That covers preflight, build, gates, tests, all six harness shards in turn, the soak at 10,000, host sabotages, secret scan, README walk, and every non-head commit's own checks.
  - **It caught one mistake of mine before it reached GitHub:** the outcomes commit wrote `(evidence).` where `gates breakdown` requires `(evidence) —`, and it was fixed before the push.
- **The shard count went from 5 to 6.** 146 harness entries ÷ 28 per shard, derived from the manifest, as P2-01 designed. No workflow edit was needed.

## Predictions

Nine predictions, all evidence, none forcing:

- **Evidence (9):** right 4 (P3-02, P3-03, P3-05, P3-09), partly 4 (P3-01, P3-04, P3-07, P3-08), wrong 1 (P3-06).
- **Forcing:** none.

The instructive ones:

- **P3-06, wrong.** Removing the §6 rule did not fail invariant 11 in any of 200 runs; half was predicted.
  - **What happens instead:** the node that hears nothing deposes about 20 leaders per run with the rule off, 0 with it on.
  - **Why the invariant misses it:** each deposed leader is replaced within the window. Phase 3's invariant 11 only asks that a leader exists.
  - **What the test asserts instead:** the rule's own effect. The commit clause in phase 4 is what should make this churn visible to the invariant (findings).
- **P3-07, partly.** The state-placed loss turns Election Safety or Vote Uniqueness red in 39 of 200 runs, not a majority.
  - **Why:** a lost vote matters only if a second candidate in the same term reaches the voter after its restart. In fault-free timing, two candidates in one term happen in 1.5% of elections (P3-05).
  - **The count I did not use:** term monotonicity names n2 in every run, but it counts the lost term itself, so it would have made the prediction right by construction.
- **P3-04, partly.** A wire-only checker has a miss, but not the predicted one: it misses the candidate's vote for itself, which is persisted and never sent. The predicted dropped grant is missed only by a delivered-only checker, because a grant is on the wire when it is sent.
  - **The reverse, found later:** the soak (P3-08) found the durable view missing decisions still in flight (findings, fourth false positive).
- **P3-09, right.**
  - **State-placed:** shrinks to 2 events, the crash plus one direction of the partition, with no timing enabler.
  - **Time-placed:** shrinks to 3 events, the extra being the timed restart, with 2 order-sensitive pairs.
  - **What it confirms:** P2-10's finding carries over to Raft: state placement removes enablers.
  - **What it does not show:** the generated noise was small (3 events in 3,000 ticks), so "strictly smaller than its input" is a weak demonstration of the shrinker's reach. P2-10 is where that reach was shown on a larger input.

## Two views of leadership: §13.31 handled prospectively

Decision 4 observes leadership two ways: "elected", a quorum of granted votes, and "acting", sending heartbeats. It checks that they agree.

- **Where the shape comes from:** the CRDT project's §13.31: two mechanisms with one observable, the weaker substituting silently.
- **What is new:** this is the first time, in either project, that the shape was designed against before it was found. Every earlier instance was discovered after it had already hidden something.
- **Why it matters:** it is the one piece of evidence that the findings log does more than record history.
- **Did it do anything?**
  - **Two fires, both real disagreements:** the agreement check fired twice in the soak's first runs.
    - **The silent step-down:** a leader seeing a higher term in a response steps down without sending anything.
    - **The in-flight grant:** a grant arrived while the candidate's next record was still being written.
  - **Both were checker defects, not node bugs.** The checker's idea of "elected" was wrong, and "elected, never acts" is how the error showed.
  - **Without the second view:** in both cases a checker on "elected" alone would have been silently wrong. The in-flight grant could as well have been reported as two leaders in term 11.

## Your P3-01 questions

**Do S-cov-5 and S-cov-6 now fail for their stated reason?** Yes. Both now fail at their assertions, with their reasons confirmed by the harness on every push. Repairing them fixed the sabotages and revealed no untested mechanism.
- **S-cov-5** stops recording the isolation effect. Its target fails with `'node-isolated-for-a-timeout' is hit in only 0`.
- **S-cov-6** lowers the floor in both comparisons. Its target fails with a message naming the `floor rule`.
- **The two targets were never the problem.** The patches were: each threw a `KeyNotFoundException` before reaching the assertion it claimed to test.

**The verdict distribution across all 120 entries:**

| Verdict | Entries | Meaning |
|---|---|---|
| direct | 64 | narrow target; failed at its assertion about the mechanism |
| command | 24 | a command entry; the command's message is required |
| reasoned | 21 | broad target; its failure message must contain the stated `reason:` |
| harness control | 5 | the harness's own controls (for example, a `control.diff` that must come out red) |
| repaired | 3 | the patch changed to reach its assertion (S-cov-5, S-cov-6, and S-shrink-4's target now asserts) |
| retargeted | 3 | aimed at a test of exactly its mechanism (S-det-3, S-rng-1, and S-rare-1 in P2) |

**Two of 120 is not reassurance.** It is also the rate the old harness could report by construction.
- **Why:** any entry whose incidental path to red was a crash, with no message that told it apart, would have been `caught` forever.
- **What `reason:` changes:** it makes the count meaningful rather than a lower bound, and only for the entries that state one.
- **Beyond the stated reasons:** for the 64 direct entries, the claim rests on the message recorded in the audit table having been read once.

## The soak's distribution, as effects

The question each row answers is whether the effect happened, not whether the fault was
injected. 10,000 executions that never isolate a node are 300 at extra cost.

```
effect                                     300 sample   10,000 soak
split-vote                                   207 (69%)   6843 (68%)
leader-replaced-after-crash                  162 (54%)   5071 (51%)
vote-denied-already-voted                    131 (44%)   4164 (42%)
requestvote-ignored                          223 (74%)   6866 (69%)
leader-stepped-down                          218 (73%)   6880 (69%)
candidate-stepped-down                       150 (50%)   5062 (51%)
elected-after-restart                        144 (48%)   4485 (45%)
elected-during-one-way-partition              91 (30%)   2808 (28%)
delivered-after-later-send                   225 (75%)   7447 (74%)
lost-in-transit-to-a-live-receiver           141 (47%)   4661 (47%)
delivered-twice                              143 (48%)   4536 (45%)
delivered-later-than-normal-delay            234 (78%)   7938 (79%)
one-way-reachability                         233 (78%)   7395 (74%)
node-isolated-for-a-timeout                   18 (6%)     958 (10%)
majority-down                                121 (40%)   3733 (37%)
all-down                                      98 (33%)   2986 (30%)
unsynced-write-lost                           48 (16%)   1415 (14%)
writes-completed-out-of-order-at-crash         0 (0%)       8 (0.08%)
partial-record-left-on-disk                   24 (8%)     620 (6%)
write-slower-than-normal-latency              53 (18%)   1603 (16%)
step-after-silence-longer-than-a-timeout     117 (39%)   3707 (37%)
restarted-from-disk                          229 (76%)   7259 (73%)
clock-rate-diverged                          225 (75%)   7398 (74%)

invariants 1, 8, 9, 11: no violation; elections 807 (sample), 25,855 (soak)
liveness checked in 234 / 7,949; no stable suffix in 66 / 2,051
time to leader after the stable suffix: max 278 / 564, mean 36 / 36 (window 3,000)
```

What the table says:

- **The two runs are one distribution.** Every effect agrees between sample and soak within a few points. So the soak's added value is the number of chances at each effect, not new kinds of execution.
- **Rare effects gain the most.**
  - **Out-of-order writes at a crash:** 8 in 10,000. The only effect that is 0 in the sample, and the only one declared rare for the sample, with its reason.
  - **Torn records:** 620 in 10,000.
  - **Isolation:** 958 in 10,000.
  - **Only the soak can meet each of these a few hundred times.** The torn-tail restart bug was found in this band (findings).
- **The floor is absolute, and weak at this scale.**
  - **The rule:** 3 hits, the same number for 300 executions and for 10,000.
  - **Where it bites:** 8 of 10,000 clears it, but it is 0.08% of executions.
  - **Why no change here:** a floor proportional to the execution count would have flagged it. I have not changed the rule, since that is a change to P2-02's rules and yours to decide.
  - **The alternative:** a separate absolute floor for the soak, set high enough that an effect under it is worth a targeted state-placed fault.
- **K = 10 has more than fivefold margin.** The longest time to a leader after the stable suffix was 564 ticks, against a 3,000-tick window.
- **Liveness is unchecked in 21% of soak executions**, which have no stable suffix: faults last until tick 12,000, and a restart or heal can come late. The test requires at least 75% checked; the soak has 79%.

## Findings (Phase 3 section of `docs/findings.md`)

- **S-cov-5 and S-cov-6 were caught for the wrong reason.** Both had been since they were written. The general form is yours: a failure that precedes the assertion is not evidence about the assertion.
- **A test of mine failed before its assertion, tests first.** Two election tests threw on `.Order()` against the stub, and that looked like the expected red. The red runs are below.
- **Invariant 11 in its phase-3 form cannot see disruption** (P3-06). The commit clause in phase 4 should make it visible.
- **The positive control is loud in term monotonicity and quiet in the two invariants the spec names** (P3-07).
- **The soak's first run found a real node bug.** Recovery cut a torn tail in memory only, so the next record was appended after the torn bytes, and the next restart refused as corruption.
- **Four checker false positives, all found at soak scale.**
  - **Order:** events sorted by time, not by observation order.
  - **Stale grants:** a delayed grant was counted toward the candidate's old term.
  - **Silent step-down:** a leader stepping down on a response was not counted as leaving office.
  - **In-flight grant:** judged against the durable record while the next record was being written.
  - **Why they hid:** each is now a twin trace. The hand-built traces were the ones I thought of.
- **The soak costs less than predicted, and its value came in its first minutes.**
- **The coverage floor is absolute, and weak at soak scale** (above, in the distribution).

### The tests-first red runs (P3-05 and the torn-tail fix)

These are the red runs the findings entry promised. First, against a stub `RaftNode` that returned no effects: all 13 failed. Two of the 13 threw on `.Order()` before reaching their assertions:

```
total: 13  failed: 13  succeeded: 0
```

After sorting by value, re-run with the real node's `Handle` replaced by `return []`: 12 failed, and the two repaired tests failed at their assertions:

```
failed ...ACandidateWithAQuorumBecomesLeaderAndSendsHeartbeatsAtOnceAndThenEveryInterval
  System.InvalidOperationException : no election within 400 ticks        (its precondition, Elect)
failed ...AFollowerThatHearsNothingStandsPersistingItsVoteBeforeAskingForVotes
  Assert.Equal() Failure: Collections differ                            (the persisted vote)
total: 13  failed: 12  succeeded: 1
```

The one that passed is `ACorruptTermVoteFileRefusesToStart`: it tests the constructor, which that stub kept. I re-checked this for the report on today's 18 election tests: 17 fail and that one passes. Against the first stub, whose constructor did nothing, it failed.

The torn-tail fix, tests first, against the node before the fix:

```
failed ...AfterATornCrashTheNextRecordIsReadableThroughTheNextRestart
  after torn crash, restart and a new record: Refused (record 1 at byte 20 fails its checksum and is not the last: corruption)
failed ...ATornTailIsCutFromTheFileBeforeTheNodeAppendsAfterIt
  the first effect after recovering a torn tail must cut it off; got:
```

## Deviations from the breakdown

- **The soak is one job, not sharded.** 10,000 executions take about 4 minutes in the SDK container (P3-08's outcome). The job's shape does not depend on that: `build-collect` requires exactly one passing `soak` job.
- **The disruption rule's test asserts its own effect**, not invariant 11: no leader is deposed after the first window. Invariant 11 cannot see the rule (P3-06).
- **The positive control is asserted as a floor**, not as "more than half": Election Safety or Vote Uniqueness red in at least the coverage floor of runs, naming n2, plus term monotonicity naming n2 in every run, and a twin green in every run.
- **One effect is declared rare for the sample only**: `writes-completed-out-of-order-at-crash`, with its reason. The soak must still clear the floor for it.
- **The observation stream gained Issued**, the persist operation as the node issued it. This fixes the fourth false positive. The node's own bytes are observed, not a node-reported state.
- **`SimRun run --raft`** reproduces a Raft schedule in another process and writes the trace. It does not check invariants itself: they live in `Raft.Core.Tests`. So the reproduction is shown by an identical trace rather than a printed violation.
  - **Dependencies:** `Raft.Core.Tests` now references `Raft.SimRun`, recorded by the layering test and the lock file. No new package.
- **Core's ambient allowlist gained `IReadOnlyCollection` and `IEnumerator`.** Other rejected types were avoided instead of allowlisted.

## Carried forward

- **Invariant 11's commit clause (phase 4).** It is the clause the disruption rule protects in practice.
- **Message corruption on a real transport (P9, register):** 95% of single-bit flips decode to another valid message.
- **Whether the soak's floor should scale with its execution count** (above). Yours to decide.

## Open items for you

1. **Your cold walk of the README (P0).**
2. **Deleting `claude/blissful-goodall-358smj-sabotage`.**
3. **Branch protection.** Required checks are unchanged in name: `build`, `each-commit`, `secrets`, `readme-walk`.
   - **`build`** now also requires the `soak` job, so the soak is required without a new name.
   - **Optional:** add `soak` itself, so a skipped soak shows in the UI.
