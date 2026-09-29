# Phase 3 — report

**Status: accepted by the reviewer** (`docs/phases/status.md`: `P3: complete`). Acceptance asked
for four changes. The two-views result is stated as the phase's result (below), and the findings
line on the test that passed against the stub is in this commit. The coverage floor that holds the
rate (P3-10) and the soak required in its own right (P3-11) landed in the push before it, each
predicted first (`feab31d`).

This report certifies the commit that contains it. `gates reports` checks that commit's CI run on
the next push. The run ids below are for earlier commits.

## The result of the phase: the two-views check earned its cost

Decision 4 observed leadership two ways and required them to agree: "elected", a quorum of granted
votes, and "acting", sending heartbeats. It was designed against the CRDT project's §13.31 shape
(two mechanisms, one observable, the weaker substituting silently) before any instance of it had
been found here.

**It fired twice at soak scale, and both times it exposed a checker defect, not a node bug.**

- **The silent step-down.** A leader that sees a higher term in a response steps down without
  sending anything. The checker never counted that as leaving office, so the node looked elected
  and never acting.
- **The in-flight grant.** A grant arrived while the candidate's next record was still being
  written. The checker counted it for the old term, and "elected" a node that had already moved
  on and never acted in that term.

**Without the second view, both errors would have been green.** A checker using only "elected"
agreed with itself and reported green both times, carrying a wrong count of elections. In the
in-flight case it could also have produced a false second leader later, a red for the wrong reason.
Only the disagreement between the two views showed that the checker's idea of "elected" was wrong.

That is a finding applied before the fact and then earning its cost. It has not happened in either
project until now. Every earlier instance of the shape was found after it had hidden something.
It is also the one piece of evidence that the findings log does more than record history.

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
| P3-10 (acceptance) | `Coverage.FloorFor`: max(3, 1% of the executions), the soak's own floor rate held at the suite's, the rate beside every count; one declaration below the soak floor | S-cov-8, S-cov-9, S-soak-3 (S-cov-6 regenerated) |
| P3-11 (acceptance) | `gates reports` requires a successful `soak` job for a phase-3-or-later report | S-run-4, S-run-5 |

**Sabotage entries:** 156 in all. 151 are run by the harness in 6 shards, and 5
(`runner: host`) by `scripts/host-sabotages.sh`.

**Test projects** (counts at the start of phase 3 in brackets, `46ac18d`): Architecture 40 (38), Checker 130 (130), Gates 100 (95), Simulation 105 (102), Core 59 (new).

## CI

- **P3-01..P3-05: green**, pushed earlier in the phase. The last of those runs was run [36508890040](https://github.com/nooree00/raft-sim/actions/runs/36508890040) at `58f3620`.
- **P3-06..P3-09 and the outcomes: green.** Run [36532629155](https://github.com/nooree00/raft-sim/actions/runs/36532629155) at `e069129`: 52 jobs, all passing, 27 min wall clock.
  - **Harness:** six head shards of 7.4–11.5 min each.
  - **Soak:** 4 min 16 s, run in parallel with the harness.
  - **Per-commit matrix:** `each-commit-list` named 7 non-head commits, and 39 (commit, shard) jobs ran them.
- **P3-10 and P3-11: green.** Run [36640620697](https://github.com/nooree00/raft-sim/actions/runs/36640620697) at `ff27d8e`. Its `gates reports` step checked the P3 report's run with the soak required (P3-11's outcome), and the soak job took 5 min 27 s.
- **Local, before each push:** everything CI runs except `gates reports`, which needs the GitHub API. That covers preflight, build, gates, tests, all six harness shards in turn, the soak at 10,000, host sabotages, secret scan, README walk, and every non-head commit's own checks.
  - **For P3-10 and P3-11 the container restarted three times mid-run, and three more runs were stopped by the session's background time limit.** The local sequence was finished in stages on the same commit: preflight through harness shard 4, then shards 5 and 6, then the soak and host checks, then each (commit, shard) of the per-commit stage as its own resumable job. No result was carried across a change of commit. One resumed stage recorded failures while Docker was down after a restart. I discarded them as environment, not code, and reran those jobs with Docker up.
  - **It caught one mistake of mine before it reached GitHub:** the outcomes commit wrote `(evidence).` where `gates breakdown` requires `(evidence) —`, and it was fixed before the push.
- **The shard count went from 5 to 6.** 146 harness entries ÷ 28 per shard, derived from the manifest, as P2-01 designed. No workflow edit was needed.

## Predictions

Eleven predictions, all evidence, none forcing:

- **Evidence (11):** right 6 (P3-02, P3-03, P3-05, P3-09, P3-10, P3-11), partly 4 (P3-01, P3-04, P3-07, P3-08), wrong 1 (P3-06). P3-10 and P3-11 came from acceptance, and P3-10's is weak evidence: its counts were known from P3-08.
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
- **The floor was absolute; it now holds the rate (P3-10, your decision at acceptance).**
  - **The problem:** a floor of 3 hits gave the same verdict for 3 in 300 and 3 in 10,000, which
    are different facts about the generator. At 10,000 an effect at 0.08% cleared it, so the soak's
    extra 9,700 executions bought coverage the gate could not tell from noise.
  - **Now:** an effect fails below max(3, 1% of the executions). 1% is the suite's own rate, 3 in
    300. The soak has its own floor rate, held at the suite's: 100 of 10,000. The absolute 3 stays,
    so a tiny sample cannot clear the rate. Every coverage report and every floor failure prints
    the rate beside the count.
  - **What changed:** nothing in the suite, where the floor is still 3 at 200 and 300 executions.
    At 10,000, exactly one effect fails: writes completed out of order at a crash, 8 (0.08%), as
    predicted.
  - **The one exception:** it is declared below the soak floor, never below 3, with its reason and
    the tests that exercise it directly (`TermVoteLogTests.EveryCrashModeRecoversToTheLastRecordThatSurvived`,
    `CoverageTests.TheThreeEventsPhaseOneNeverProducedAreReachable`). It is a declaration you can
    reject; the P4 breakdown predicts it goes once logs are persisted.
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
- **The coverage floor was absolute, and weak at soak scale.** It now holds the rate (P3-10).
- **A test that passes against a stub is a test whose subject is not in the code under test.** The one election test that passed against the injected stub, `ACorruptTermVoteFileRefusesToStart`, did so because its subject lives in the constructor, which that stub kept. The stub was the wrong stub for that test.

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
- **Writes completed out of order at a crash, declared below the soak floor** (P3-10). It is expected to clear the floor once logs are persisted (the P4 breakdown, decision 7); if it does not, the choice returns to you.

## Open items for you

1. **Your cold walk of the README (P0).**
2. **Deleting `claude/blissful-goodall-358smj-sabotage`.**
3. **Branch protection: add `soak` to the required checks** (your decision at acceptance), beside `build`, `each-commit`, `secrets` and `readme-walk`. On the repository side, `gates reports` now requires a successful `soak` job in the run certifying any report from phase 3 on (P3-11), so the requirement no longer lives only inside `build`.
