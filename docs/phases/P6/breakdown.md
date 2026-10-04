# Phase 6 — task breakdown

Membership changes by joint consensus (spec §11 phase 6, §5 item 4). Format as in
`docs/phases/P4/breakdown.md`, parsed by `gates breakdown`; outcomes will say `(evidence)` or
`(forcing)`.

**Done when** (spec §11): joint consensus under partition preserves every invariant. In addition,
carried forward:

- the invariants are stated over "a quorum of the configuration in effect — both quorums during joint consensus" (spec §5, commitment in fact); every checker that counts a majority today counts a fixed `ClusterSize / 2 + 1`, and each becomes configuration-aware before any Raft code changes (tests first for `Raft.Core`, spec §12);
- the accepting and rejecting halves of the linearizability check keep holding with membership changes in the workload (a reconfiguration must not lose or duplicate an acknowledged write);
- the two register rows promised to phase 6 are closed or argued: `each-commit-list`'s silent fallback and the harness's fixed cost. The third, a structural measure of linearizability-checking cost, is promised to its own phase (phase 11, reviewer at approval).

**Approved** (reviewer): P6-00 to P6-10, without P6-02. Decisions 1, 2, 3 and 5 as proposed
(decision 1 is the paper's §6, and it is what makes the transition safe). Decision 6 is the
ghost-id principle applied to membership: a checker that reads a node's claim about which
configuration it uses cannot catch it using the wrong one, so the simulator records it (the
reviewer's A3, holding for the third time). Decision 4 with a condition: `busy` is a definite
failure that the adapter drops and counts, as P5 decision 1 treats refusals; an indeterminate
`busy` would give every membership test P5's hard-history problem for a refusal that never took
effect. P6-02 is promised to its own phase (11). **A constraint on predictions, from P5:** the same
soak took 924 s and 1,275 s on two GitHub runners, so no prediction in this phase is a duration
compared across runs; P6-01 and P6-10 are restated in quantities measured within one run or
deterministic ones.

## Ordering

Tooling first, by phase 4's tooling-order rule: P6-00 (`each-commit-list` fails rather than widens;
the range question it answers wrong is the one every later push depends on) and P6-01 (the harness's
remaining fixed cost, measured on GitHub before a design is chosen). The structural measure, drafted
here as P6-02, is not part of this phase (reviewer: joint consensus is where a mistake corrupts the
cluster, and it gets the phase's whole attention); it is phase 11, its prediction recorded in the
register now.

Then membership in the order the spec's tests-first rule forces: P6-03 (configuration as log
entries, durable and recovered), P6-04 (the checkers made configuration-aware, against hand-built
traces, before any joint-consensus code), P6-05 (joint consensus in `RaftNode`), P6-06 (new servers
catch up before they vote, if decision 3 keeps it), P6-07 (removed servers and a removed leader),
P6-08 (the simulator: more node ids than the configuration, membership requests in the schedule,
coverage dimensions), P6-09 (the classic unsafe change, constructed: the positive control for this
phase), P6-10 (the soak with membership changes, and its cost).

**Blocking set:** P6-00, P6-03, P6-04, P6-05, P6-08, P6-09, P6-10. P6-01 is blocking only
as a register row: the phase cannot be complete with rows promised to it left open (`gates register`).

## Decisions for review

1. **A configuration is a log entry, and takes effect on append, not on commit** (the paper's §6):
   a server uses the latest configuration in its log, committed or not. The alternative (on commit)
   leaves a window in which a leader uses a configuration its successor may not have. Consequence
   for the checkers: "the configuration in effect" at a node is a function of its log at that moment,
   and commitment in fact must use the configuration in effect *for the index being committed* on
   the leader that commits it.
2. **The configuration is recovered from the log, with no separate file.** On restart a node scans
   its recovered log for the latest configuration entry; before any, the initial configuration
   comes from its `NodeContext` (as `Peers` does today). Phase 7 (compaction) must then carry the
   latest configuration in the snapshot; that is a register row for phase 7, opened here.
3. **New servers join as non-voting members and catch up first** (the paper's §6: otherwise a
   joint configuration can be unable to commit while the new servers fill their logs). The leader
   appends `C_old,new` only once each new server's `matchIndex` is within one round of the leader's
   log. Alternative: skip catch-up (simpler; availability, not safety, suffers). Recommended: keep it,
   as P6-06.
4. **One membership change in flight at a time.** A leader refuses a membership request while a
   `C_old,new` or an uncommitted `C_new` is in its log (an answer `busy|…`, a definite failure,
   left out of the checker's history like a refusal, P5 decision 1). **Approved with the condition**
   that the adapter drops `busy` and counts it, never treats it as indeterminate.
5. **A leader not in `C_new` steps down once `C_new` is committed**, and manages the cluster until
   then without counting itself in `C_new`'s majority (the paper's §6). A removed server is not
   told it was removed; the §6 disruption rule (phase 3) is what keeps its elections from deposing
   the new leader, and P6-07 measures that it does.
6. **Membership requests are client operations of their own kind, not faults.** They go through a
   leader like a write, are generated by the workload beside the key-value operations (rarely: at
   most a few per execution), and appear in the client history as operations on a reserved key
   that the KV model ignores; the simulator records the configuration in effect at each node as an
   observation, so the checkers do not read it from the node's own report.

## Tasks

### P6-00 — `each-commit-list` fails rather than widens when it cannot find the previous head

- **Task:** The register row (P5): when a push's previous head is not in the history (a force-push), `each-commit-list` silently checks "commits since the merge-base with `origin/main`", a different question from "commits since the previous push", and wrong often enough (run 37146478876: 143 commits, 13 historic reports rejected, none of the six rewritten commits checked). Resolve the old head by SHA first (`git fetch origin <sha>`; GitHub serves a commit that is no longer on any ref); then start from the merge-base of the old head and the new one, the point where the rewrite forked, and say so. When the old head cannot be resolved at all, fail, naming what could not be determined, instead of widening. `gates each-commit` locally shares `ResolveStart` and gets the same rule. The "could not ask" shape (P2 secret scan, the CRDT project's push gate) in the range computation.
- **Vacuity:** A test repository in which the old head is always reachable never exercises the rewrite path. Guarded: the tests build a fixture repository, rewrite its history, drop the old head from every ref, and require (a) the fork point when the old head is fetchable by SHA and (b) a failure, not a range, when it is not. Sabotages: S-range-1, an unresolvable old head falls back to the merge-base with the base ref again; S-range-2, the start is the old head itself rather than its merge-base with the new head.
- **Sabotage:** S-range-1, S-range-2
- **Verifiable here:** yes — fixture repositories locally; the fetch-by-SHA path against GitHub only in CI, observed on the first force-push after it lands
- **Prediction:** Starting from the merge-base of the old and new heads is right for a rewrite and wrong for nothing else: for an ordinary push the old head is an ancestor of the new, so the merge-base is the old head and the range is unchanged. **Observable:** the existing `EachCommitTests` pass unchanged, and the fixture's rewritten history yields exactly its rewritten commits.
- **Outcome:** pending

### P6-01 — The harness's remaining fixed cost, measured on GitHub, then one change

- **Task:** The register row (P6, "Harness fixed cost"), with the split P5-07 printed: setup 0-5 s, baseline build 34-59 s per worker, and what remains is the baseline checks of the costliest test projects (`Raft.Scale.Tests` up to 119 s on one worker, `Raft.Budget.Tests` 97 s, `scripts/ci-test.sh Raft.Scale.Tests` 209 s for S-ran-2), the case in which the reviewer said neither design (a) nor (b) is the lever. First the split from GitHub's runners (the machine that enforces the ceiling), then one change chosen against it, brought to the reviewer before it is made. Candidates: assign the costliest baseline units to workers first, so they overlap the cheap workers' entries instead of delaying their own; or run a shard's baseline checks of one project once per shard instead of once per worker that holds it (today the unit is already shared, so this is to be measured, not assumed).
- **Vacuity:** A fixed-cost change that removes a baseline check removes the proof that a target passes unpatched, so a red result says nothing about the patch. Guarded: `HarnessScopeTests` requires every baseline unit on exactly one worker, for any worker count (S-harness-2), and any change keeps every target's baseline. Sabotages: S-harness-3, the costliest unit assigned last again, measured: the slowest worker's ready time rises.
- **Sabotage:** S-harness-3
- **Verifiable here:** partial — the order and the split locally; the cost that matters on GitHub's runners only
- **Prediction:** **Revised at approval, before the task starts**, because the same soak varied 38% between two GitHub runners on identical code: a prediction about a duration compared across runs is unfalsifiable at that spread, so this one is stated within a run. On GitHub, in every shard holding a `Raft.Scale.Tests` target, the slowest worker's ready time exceeds its own baseline build plus the costliest single baseline unit by at least 20 s today (other units queued behind or ahead of it on the same worker), and longest-first assignment brings that excess under 10 s in every shard, without changing any entry's cost. **Observable:** the per-worker split lines in one CI run before and one after, each shard's excess computed within its own run.
- **Outcome:** pending

### P6-03 — A configuration is a log entry, durable and recovered

- **Task:** An entry kind for configurations (`C_old,new`, `C_new`) beside commands, in the codec and the log file, canonical as P3-02 requires; a node's configuration in effect is the latest configuration entry in its log, else the initial configuration from its context (decisions 1 and 2). Unit tests first: recovery from every crash point of the log file yields the configuration of the latest durable configuration entry, and a truncated configuration entry takes its configuration with it.
- **Vacuity:** A node that never truncates a configuration entry never exercises reverting to the previous configuration, the case that decides whether "in effect" means the log or a cached value. Guarded: a hand-built log with a configuration entry in a conflicting suffix, truncated by a new leader, must revert the node's configuration. Sabotages: S-cfg-1, the configuration cached on append and not reverted on truncate; S-cfg-2, the configuration taken from the latest committed entry, not the latest appended.
- **Sabotage:** S-cfg-1, S-cfg-2
- **Verifiable here:** yes — unit tests and the crash-during-write test run locally
- **Prediction:** Caching the configuration on append is the natural implementation and is wrong: the truncation path in `EntryLog` (P4-02) does not know about configurations, so the cached value survives the entry that set it. **Observable:** the truncated-suffix test fails against a cached implementation, by name, before the log-derived one exists.
- **Outcome:** pending

### P6-04 — The checkers count quorums of the configuration in effect

- **Task:** Before any joint-consensus code: every checker that counts a majority (`ElectionInvariants` `Quorum`, `LogInvariants` commitment in fact and no-spurious-commit, `CommitLiveness`) takes the configuration in effect from the simulator's observation (decision 6), and during joint consensus requires a majority of both. Election Safety's "elected" becomes "won a majority of every configuration in effect at the candidate". Hand-built traces, each accepted or rejected for a stated reason: (a) a leader elected by a majority of `C_old` alone during `C_old,new` is not elected; (b) an entry on a majority of `C_new` but not of `C_old` during joint consensus is not committed in fact; (c) after `C_new` commits, a majority of `C_new` suffices; (d) a removed server's votes count for nothing.
- **Vacuity:** Every existing trace has one configuration, so a checker that ignores configurations accepts and rejects all of them exactly as before. Guarded: traces (a) to (d) each differ from an existing accepted trace only in the configuration, and each must flip the verdict. Sabotages: S-joint-1, commitment in fact counts the new configuration only during joint consensus; S-joint-2, election counts the old configuration only; S-joint-3, the configuration in effect read from the node's report rather than the observation.
- **Sabotage:** S-joint-1, S-joint-2, S-joint-3
- **Verifiable here:** yes — hand-built traces need no joint consensus
- **Prediction:** Trace (b) is the one an implementation-shaped checker gets wrong: commitment in fact is computed per index from disk contents, and "the configuration in effect" for an index is the leader's at the time it commits that index, which no disk records; a checker that uses the latest configuration on disk instead accepts (b) once `C_new` reaches the disks. **Observable:** trace (b) accepted by a latest-configuration variant and rejected by the per-index one.
- **Outcome:** pending

### P6-05 — Joint consensus in `RaftNode`

- **Task:** A leader given a membership request appends `C_old,new`; while it is in effect, elections and commitment need majorities of both configurations; once `C_old,new` is committed the leader appends `C_new`; once `C_new` is committed, servers not in it are no longer counted (decisions 1, 4 and 5). Unit tests first against `ManualCluster`: a change from three servers to a different three (one replaced), from three to five, and from five to three, each committing a write before, during and after.
- **Vacuity:** A change that completes in a cluster with no failure proves the happy path only; the rules that matter are the ones a partition makes bind. Guarded: each construction includes a step in which only one configuration's majority is reachable, and asserts that nothing commits then and that the change completes once both are. Sabotages: S-member-1, commit by `C_new`'s majority alone during joint consensus; S-member-2, a candidate wins with `C_old`'s majority alone; S-member-3, `C_new` appended before `C_old,new` is committed.
- **Sabotage:** S-member-1, S-member-2, S-member-3
- **Verifiable here:** yes — `ManualCluster` and the simulator run locally
- **Prediction:** The first construction to fail will be five to three with the leader removed, at the step where the leader must commit `C_new` without counting itself: the existing `Quorum()` counts the leader implicitly (`(_context.Peers.Count + 1) / 2 + 1`), so a leader outside `C_new` will count its own vote toward `C_new`. **Observable:** that construction red by name against the first implementation, with the leader counted in a `C_new` majority.
- **Outcome:** pending

### P6-06 — New servers catch up as non-voting members first

- **Task:** If decision 3 keeps it: a server added by a membership request first receives entries as a non-voting member; the leader appends `C_old,new` once each new server's `matchIndex` is within one replication round of its log. Unit test: a change that adds two empty servers to a cluster with a long log commits a write during the change within a bound, which it cannot without catch-up.
- **Vacuity:** A catch-up that is never waited for (because new servers are always already current in the test) is indistinguishable from none. Guarded: the test starts the new servers empty behind a log of at least 100 entries and asserts the leader waited, by the order of events. Sabotages: S-member-4, `C_old,new` appended at once, without waiting.
- **Sabotage:** S-member-4
- **Verifiable here:** yes — `ManualCluster` runs locally
- **Prediction:** Without catch-up, the change from three to five with two empty servers stalls commitment for at least as long as replicating the whole log to one new server, because `C_old,new` needs three of five for `C_new`'s majority and two of those are empty. **Observable:** the time from the request to the next committed write, with and without the wait.
- **Outcome:** pending

### P6-07 — Removed servers, and a removed leader

- **Task:** A leader not in `C_new` steps down once `C_new` commits; a removed server keeps timing out and requesting votes, and the §6 disruption rule keeps it from deposing the new configuration's leader. Measured, not assumed: removed servers' candidacies and the leaders they depose.
- **Vacuity:** A removed server that is also crashed or partitioned never stands, and the disruption rule is never exercised. Guarded: the measurement counts removed servers that stood at least once, and requires a floor. Sabotages: S-member-5, a removed leader keeps leading; S-disrupt-4, the disruption rule off for requests from servers outside the configuration.
- **Sabotage:** S-member-5, S-disrupt-4
- **Verifiable here:** yes — the simulator runs locally
- **Prediction:** With the disruption rule, removed servers depose no leader in 200 executions with removals; without it, a removed server deposes the leader at least once per execution in which it stays up, because its term grows past the leader's with every timeout. **Observable:** deposed leaders by removed servers, with the rule and without.
- **Outcome:** pending

### P6-08 — The simulator: more node ids than the configuration, membership requests generated

- **Task:** The simulator runs a universe of node ids larger than the initial configuration (up to five); the workload generates membership requests (decision 6) beside key-value operations; the configuration in effect at each node is an observation in the trace. New coverage dimensions, as functions of the trace (spec §7): a change completed during a partition, a change with the leader removed, a crash while a node is in joint configuration, a change that started and did not complete before the execution's end. Each with a floor, like P3's.
- **Vacuity:** A generator that produces membership requests only in fault-free stretches never tests the phase's done criterion. Guarded: the dimension "change completed during a partition" has a floor, and a zero fails the build. Sabotages: S-cov-11, membership requests generated only after the last fault heals: the partition dimension falls to zero.
- **Sabotage:** S-cov-11
- **Verifiable here:** yes — the simulator runs locally
- **Prediction:** "A change completed during a partition" will be the rarest dimension and below a 1% floor at first, because a change needs two commits in sequence and the generator's partitions usually isolate the leader's majority for part of that time. **Observable:** the dimension's count over the 300-execution sample on the first generator.
- **Outcome:** pending

### P6-09 — The unsafe change, constructed: the positive control for this phase

- **Task:** The paper's Figure 10 case, built exactly: switching from `C_old` to `C_new` directly, without the joint configuration, allows two leaders in one term when a partition separates a majority of `C_old` from a majority of `C_new`. A variant of `RaftNode` that changes directly must turn Election Safety red in that construction and in the generated sample; the real node must not. This phase's positive control, in the role the fsynced-then-lost disk plays for election (spec §5).
- **Vacuity:** A construction whose partition does not actually separate the two majorities at the moment of the change passes with both nodes. Guarded: the construction asserts both majorities were reachable by different candidates in the same term before asserting the verdict. Sabotages: S-member-6, the direct change in the real node: Election Safety red in the construction.
- **Sabotage:** S-member-6
- **Verifiable here:** yes — `ManualCluster` and the simulator run locally
- **Prediction:** The construction goes red at once; the generated sample catches the direct-change variant in fewer than 1% of executions, because it needs a partition aligned with the change window. So the construction, not the sample, is the harness target. **Observable:** the construction's verdict for both nodes, and the count of red executions for the variant over the 300-execution sample.
- **Outcome:** pending

### P6-10 — The soak with membership changes, and its cost

- **Task:** The 10,000-execution soak with membership requests in the workload: every invariant, linearizability over client histories, the new coverage dimensions above their floors, and the distribution reported. Its duration on GitHub against the phase-5 head's (run 37146478876 or its successor), and against the soak's place outside the local pre-push run (P5 acceptance).
- **Vacuity:** A soak whose membership requests are mostly refused (decision 4) or never complete checks one configuration in most executions. Guarded: the report gives completed changes per execution, and a floor on executions with at least one completed change. Sabotages: S-soak-7, membership requests dropped from the soak's workload: the completed-change floor goes red.
- **Sabotage:** S-soak-7
- **Verifiable here:** partial — a local soak run; the CI job only in CI
- **Prediction:** **Revised at approval, before the task starts:** no duration is predicted, because the same soak took 924 s and 1,275 s on two GitHub runners (P5): at a 38% spread a duration prediction cannot be wrong. Instead, deterministic quantities: membership changes raise the checker's total states explored over the soak, KL-1 excluded, by under 10%, because they are a few operations per execution and the checker ignores the reserved key; and at least one new undecided linearizability search appears, because a change lengthens the window in which writes are indeterminate (a leader stepping down at `C_new` leaves its in-flight writes unanswered). **Observable:** the soak's reported states explored against the phase-5 head's (same seeds, deterministic), and the undecided count with KL-1 excluded; the duration is reported, not predicted.
- **Outcome:** pending

## Sabotage ids

New series: S-range (P6-00), S-cfg (P6-03), S-joint (P6-04), S-member (P6-05..07,
P6-09). S-harness-3 follows S-harness-1..2, S-disrupt-4 follows S-disrupt-1..3, S-cov-11 follows
S-cov-1..10, S-soak-7 follows S-soak-1..6. Each id's `sabotage/<id>/` entry lands in the same commit
as the check it proves, and is run on that commit before it is pushed.

## Register rows opened here

- Phase 7: the snapshot carries the latest configuration (decision 2), or a node restored from a snapshot reverts to its initial configuration.
