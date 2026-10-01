# Phase 5 — task breakdown

The checker's accepting half (spec §11 phase 5). Format as in `docs/phases/P4/breakdown.md`,
parsed by `gates breakdown`; outcomes will say `(evidence)` or `(forcing)`.

**Approved** (reviewer): all six decisions, with P5-00 added first. Decision 1 is safe rather than
assumed because P5-04's variant 3 plants the one case where dropping a refusal is wrong. Decision 3
makes a slow checker visible rather than quietly permissive.

**Done when** (spec §11): the checker accepts the histories a correct cluster produces, including
indeterminate operations. In addition, carried forward:

- phase 4 decision 3's limit is closed: a node that applied the wrong entry and answered consistently
  with it passes every phase-4 check, and the linearizability check over client histories is what
  must catch it;
- the accepting half keeps its own vacuity guard: a checker that accepts every history a correct
  cluster produces is only meaningful if it still rejects the histories a broken cluster produces
  (spec §6: "the checker's own vacuity risk is the sharpest in the project").

## Ordering

P5-00 first, a tooling task (reviewer, at approval): the breakdown gate must reject a sabotage id
cited by two tasks. This breakdown's first draft cited S-hist-1..3 for P5-01, ids phase 0 already
owns, and the gate accepted it. Then P5-01 (the history the checker is given: the adapter decides what the checker can see, and
every later task inherits its choices). Then P5-02 (indeterminate operations, constructed exactly,
where the spec says the two naive treatments reject correct histories), P5-03 (the accepting half
over generated runs), P5-04 (the rejecting side, on broken Raft: phase 4 decision 3's limit),
P5-05 (the soak with the checker, and its cost).

The harness-cost row the register promised to phase 5 closed early, as P4-12, so it is not a task
here. Phase 4's finding on tooling order applies: any tooling-cost change this phase needs lands
first, and costs are measured on GitHub's runners, the machine that enforces the ceiling.

**Blocking set:** all six. P5-00 is tooling, but it lands first by phase 4's tooling-order rule, and
every later task's sabotage ids are checked by it.

## Decisions for review

1. **Refusals are definite failures and are left out of the history.** A `redirect|…` or
   `too-large|…` answer comes from a node that did not append the command, so the operation did
   not take effect. The adapter drops it and counts it. The risk is a node that refuses after
   appending (a bug), whose effect would then appear with no operation to explain it; P5-04 plants
   exactly that and the checker must reject it.
2. **A timed-out operation is indeterminate, and its client's later operations belong to a fresh
   logical client** (Knossos and Jepsen, as `KvCodec.History` already does for the phase-2 stores).
   A simulated client moves on after a timeout while its operation may still take effect, which a
   sequential client cannot express. A retried write is a new operation with the same bytes; both
   may take effect (18.5% of soak runs committed a duplicate in phase 4), and the checker must
   accept that.
3. **The checker's budget never counts as acceptance.** A history whose search exhausts the budget
   is `Unknown`. It fails the run unless its rate is declared below the floor with a reason, as the
   distribution's rules already do for effects.
4. **The adapter is test code, shared by link** (as the checkers are since P4-11). `Raft.Checker`
   stays protocol-agnostic: it knows operations, not Raft's reply format.
5. **The workload gains `Delete`.** Spec §6 lists five operations and phase 4's workload issued
   four; a checker accepting histories that never contain a `Delete` says nothing about `Delete`.
   This is the distribution lesson (phase 2: an effect never produced is never tested) applied to
   the workload instead of the faults. The workload is the one dimension nothing has measured yet,
   so P5-03's report counts each operation kind and each must clear the floor, as effects do.
6. **No new dependencies.**

## Tasks

### P5-00 — A sabotage id is owned by one task (tooling, first)

- **Task:** `gates breakdown` fails when a sabotage id appears in the `Sabotage` list of more than one task, naming every task that cites it. A task that proves its claim with an entry another task owns says so explicitly, as `; shared: S-x-n`, and a shared id must be owned by exactly one other task. The one existing double citation, S-pre-4 (owned by P0-10, preflight; cited by P0-05 because preflight is what checks the workflow's image pin), becomes P0-05's `shared:`.
- **Vacuity:** A rule that counts citations per breakdown file, or only among pending tasks, misses exactly the case that happened: a pending phase-5 task citing a completed phase-0 task's id. Guarded: the hand-built cases put the two citations in different phases, one done and one pending, and the run over the real breakdowns must name S-hist-1..3 on the commit that had them (`9f8f28c`).
- **Sabotage:** S-bd-7, S-bd-8, S-reg-5
- **Verifiable here:** yes — the gate runs locally over the real breakdowns and hand-built ones
- **Prediction:** Known before writing it, so not evidence: the rule finds S-pre-4 in the current breakdowns and S-hist-1..3 at `9f8f28c`, nothing else. **What the collision would have cost, if not caught:** P5-01's `sabotage/S-hist-1/` would have collided with phase 0's. Either the new entry overwrites phase 0's, which deletes a checker sabotage with every gate still green (the id exists and is cited), or the existing entry stands and P5-01's sabotage requirement is met by phase 0's checker entries, so the harness reports green against a mechanism P5-01 never touched. The evidence part, on the sweep of other identifiers the gates resolve: `gates register` accepts a done row whose evidence names a test that exists in the sources but is never executed, because it resolves the name textually, not against a test run. **Observable:** the gate's output at `9f8f28c` and now, and the register's behaviour on a done row citing a test that `gates testcount` would report unexecuted.
- **Outcome:** partly (evidence) — the known part held as stated: run over the breakdowns at `9f8f28c` the rule names S-hist-1..3 (P0-15 and P5-01) and S-pre-4 (P0-10 and P0-05), nothing else; not evidence, I had counted them before writing it. The sweep half was right that `gates register` accepts a done row citing something that never runs, and wrong about why: it does not resolve the name textually, it resolves it against every method definition in the built test assemblies, so a private helper (`HarnessScopeTests.Spec`) passed as evidence. Fixed in the same commit: evidence must be a method marked [Fact] or [Theory], and `gates testcount` already requires every such method to run (S-reg-5). **And the rule caught its author on its first run:** I had given the register sabotage the id S-reg-3, which P0-08 has owned since phase 0; the gate named it before anything was committed. S-bd-7, S-bd-8 and S-reg-5 caught on `862e872`.

### P5-01 — The client history a correct cluster produces, as the checker sees it

- **Task:** An adapter from the simulator's client log to a checker history, under decisions 1–2: refusals dropped and counted, timeouts indeterminate, a fresh logical client after each timeout, retries as separate operations, `Get`/`Cas` outputs from the reply. Proven on hand-built client logs, each rule rejecting a malformed mapping and accepting its twin one step away.
- **Vacuity:** An adapter that drops operations can make any history pass (a lost write whose operation is dropped leaves nothing to contradict), and one that drops indeterminate operations can make correct histories fail. Guarded: every client operation is accounted for (mapped, refused, or indeterminate, with the three counts summing to the log), and the hand-built logs include each case.
- **Sabotage:** S-adapt-1, S-adapt-2, S-adapt-3
- **Verifiable here:** yes — hand-built logs and the simulator run locally
- **Prediction:** Keeping a timed-out client's later operations on the same logical client, instead of a fresh one, rejects correct histories: at least 5% of the 300-execution sample goes red under that variant, because a client's next operation then appears to follow its indeterminate one in real time, which forbids the indeterminate one from taking effect after it. **Observable:** rejected runs in the sample under the variant, against none with the adapter as decided.
- **Outcome:** wrong (evidence) — over the 300-execution sample (soak configuration, seeds 1..300) the variant rejected no run, against the predicted 5% or more; the adapter as decided rejected none either. The mechanism I named does not exist in this checker: WGL orders operations by responses only (a precedes b iff a responded before b was invoked), an indeterminate operation never responds, so it constrains nothing, whichever logical client it belongs to. Client identity reaches only `History.Problems`, the structural check that a client's operations do not overlap, and there the variant fails 287 of 300 runs. So decision 2 is what keeps the history well formed, not what keeps correct histories accepted, and P5-03 must assert the structural check as well as the verdict, or the decision is untested in the sample. Also measured, for later tasks: indeterminate operations in 292 of 300 runs, refusals in all 300 (96,445 refusals against 53,725 operations: clients send to a random node), no unexplained reply, and two runs whose search exhausted the 1,000,000-state budget (P5-03's prediction says none). S-adapt-1..3 caught on `8ed1976`; S-ran-2's patch, stale on the scale project's new link, regenerated in the same commit (`gates patches` named it).

### P5-02 — Indeterminate operations, constructed exactly

- **Task:** Three exact executions in a `ManualCluster` with clients, each checked by the WGL checker and the brute-force oracle: (a) a write times out, commits later, and a read observes it; (b) a write times out and is truncated with its deposed leader, and a read does not observe it; (c) a retried `Append` committed twice. All three must be accepted. The two treatments spec §6 names as wrong are measured on the same executions: indeterminate-as-failed and indeterminate-dropped.
- **Vacuity:** A construction whose steps do not happen as intended (the write never commits, the truncation never happens) passes without testing anything. Guarded: each step asserted by what the disks and the client log hold, as P4-04 did.
- **Sabotage:** S-indet-1, S-indet-2
- **Verifiable here:** yes — exact constructions run locally
- **Prediction:** Both wrong treatments reject (a), as spec §6 says, and both accept (b). Indeterminate-as-failed also rejects (c), because the second copy of the value has no completed operation that wrote it. **Observable:** each treatment's verdict on each construction.
- **Outcome:** partly (evidence) — the three constructions hold as built (each step asserted on the disks and the client log) and both the WGL checker and the oracle accept all three. Of spec §6's treatments, "as failed" and "dropped" reject (a) and accept (b), as predicted, and "as failed" rejects (c), as predicted. The prediction's implied contrast, that dropping would accept (c), is wrong: in this checker both names are one transformation (the write has no effect), so dropping rejects (c) too, and the spec's two treatments are one. Measured beside them, what a naive recorder does, closing the operation at the moment its client gave up: it accepts (a) and (c) and rejects (b), the reverse pattern, because a write forced to take effect before the timeout must show in the later read even when the cluster truncated it. None of the three naive treatments accepts all three histories; only "at any point after invocation, or never" does. S-indet-1 (the checker drops indeterminate operations) and S-indet-2 (construction (a)'s write never reaches n2, which leaves a history that is still linearizable and is caught only by the step assertions) caught on `e2f2f41`.

### P5-03 — The checker accepts the histories of generated runs

- **Task:** The WGL checker over every execution of the soak sample (300 in the suite), with the adapter of P5-01. The report adds: operations per history, indeterminate operations, refusals, retried duplicates, reads, `Cas` true and false, `Delete`, the largest per-key sub-history, states explored at most, and `Unknown` verdicts (decision 3).
- **Vacuity:** Histories with no indeterminate operations, no concurrency or no reads are accepted by weak checkers too. Guarded: each of those mechanisms must clear the rate floor in the sample, as effects (P3-10's rules), and the budget's headroom is reported.
- **Sabotage:** S-lin-1, S-lin-2
- **Verifiable here:** yes — the sample in the suite; the soak locally
- **Prediction:** Every history in the sample is accepted, with no `Unknown`. The largest per-key sub-history stays under a tenth of the default budget in states explored, because keys are few (three) but think time keeps concurrency at about three operations. Indeterminate operations appear in at least 30% of executions, since every crash or partition of a leader times out its clients. **Observable:** the verdict counts, the maximum states explored, and the indeterminate rate.
- **Outcome:** partly (evidence) — over the 300-execution sample every history is well formed and accepted, with no undecided search, as predicted, and indeterminate operations are in 292 of 300 executions (predicted at least 30%). The budget half is wrong: the most states explored is 381,104, 38% of the default budget, against the predicted tenth (median 190, 90th percentile 447, 99th 220,486; the largest per-key sub-history has 82 operations). The cost is a tail, not a typical history, and it sits close to the edge: with phase 4's workload (no `Delete`) the same 300 seeds had two searches that exhausted the budget, which decision 3 fails. Every history-content dimension clears the floor: concurrency on a key, completed reads, puts, appends and deletes, and `Cas` both ways in all 300; retried writes in 266. Histories hold 53,725 operations and 96,445 refusals were left out. The structural check (P5-01's lesson) runs on every history. S-lin-1 (an undecided search counted as acceptance) and S-lin-2 (the workload without `Delete`, caught by the content floor) caught on `193a2cc`; every other scale entry gave its expected result on the new workload.

### P5-04 — The accepting half still rejects broken Raft (phase 4 decision 3's limit)

- **Task:** Broken variants of `RaftNode`, each a sabotage, over the same sample: (1) a node applies the entry after the committed one and answers consistently with what it applied; (2) the leader answers a client when it appends, before commit; (3) a node refuses a client after appending its command. **What must hold:** the phase-4 invariants stay green under (1) (the limit as stated at approval: state machine safety is only as strong as the node's report), and the checker rejects it.
- **Vacuity:** A variant that also breaks an invariant proves nothing about the checker, because the invariant would catch it first. Guarded: for (1), each run records whether any phase-4 invariant failed, and the claim is made only on runs where none did.
- **Sabotage:** S-lin-3, S-lin-4, S-lin-5
- **Verifiable here:** yes — the sample runs locally
- **Prediction:** Under (1), every phase-4 invariant stays green in at least 95% of the sample, and the checker rejects at least half of the runs: a wrong apply changes a later `Get` or `Cas` answer whenever the two entries touch the same key, and with three keys that is most runs. (2) is rejected only in runs where a leader crashes between answering and committing, well under half. **Observable:** per variant, runs with an invariant red, runs the checker rejects, and runs where only the checker catches it.
- **Outcome:** pending

### P5-05 — The soak with linearizability, and its cost

- **Task:** The 10,000-execution soak runs the checker on every history, with P5-03's report at soak scale and decision 3's rule for `Unknown`.
- **Vacuity:** A soak whose checker never meets a hard history proves little about the budget. Guarded: the maximum states explored is reported beside the budget, and the histories at the top of the distribution are kept as named seeds.
- **Sabotage:** S-soak-6
- **Verifiable here:** partial — a local soak run; the CI job only in CI
- **Prediction:** Checking adds under 20% to the soak job's time on GitHub's runners, because the per-key sub-histories are short. **Observable:** the `soak` job's duration in CI against the same job on the phase-4 head (run [36810331470](https://github.com/nooree00/raft-sim/actions/runs/36810331470)), same seeds; the local figure is reported beside it, not used.
- **Outcome:** pending

## Sabotage ids

S-bd-7..8 follow S-bd-1..6, S-reg-5 follows S-reg-1..4. New series: S-adapt, S-indet, S-lin (S-hist is taken: S-hist-1..7 are phase 0 and 2's checker entries). S-soak-6 follows S-soak-1..5. Each id's
`sabotage/<id>/` entry lands in the same commit as the check it proves, and is run on that commit
before it is pushed.
