# Phase 5 — task breakdown

The checker's accepting half (spec §11 phase 5). Format as in `docs/phases/P4/breakdown.md`,
parsed by `gates breakdown`; outcomes will say `(evidence)` or `(forcing)`.

**Done when** (spec §11): the checker accepts the histories a correct cluster produces, including
indeterminate operations. In addition, carried forward:

- phase 4 decision 3's limit is closed: a node that applied the wrong entry and answered consistently
  with it passes every phase-4 check, and the linearizability check over client histories is what
  must catch it;
- the accepting half keeps its own vacuity guard: a checker that accepts every history a correct
  cluster produces is only meaningful if it still rejects the histories a broken cluster produces
  (spec §6: "the checker's own vacuity risk is the sharpest in the project").

## Ordering

P5-01 first (the history the checker is given: the adapter decides what the checker can see, and
every later task inherits its choices). Then P5-02 (indeterminate operations, constructed exactly,
where the spec says the two naive treatments reject correct histories), P5-03 (the accepting half
over generated runs), P5-04 (the rejecting side, on broken Raft: phase 4 decision 3's limit),
P5-05 (the soak with the checker, and its cost).

The harness-cost row the register promised to phase 5 closed early, as P4-12, so it is not a task
here. Phase 4's finding on tooling order applies: any tooling-cost change this phase needs lands
first, and costs are measured on GitHub's runners, the machine that enforces the ceiling.

**Blocking set:** all five; each carries part of §11's criterion or its vacuity guard.

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
6. **No new dependencies.**

## Tasks

### P5-01 — The client history a correct cluster produces, as the checker sees it

- **Task:** An adapter from the simulator's client log to a checker history, under decisions 1–2: refusals dropped and counted, timeouts indeterminate, a fresh logical client after each timeout, retries as separate operations, `Get`/`Cas` outputs from the reply. Proven on hand-built client logs, each rule rejecting a malformed mapping and accepting its twin one step away.
- **Vacuity:** An adapter that drops operations can make any history pass (a lost write whose operation is dropped leaves nothing to contradict), and one that drops indeterminate operations can make correct histories fail. Guarded: every client operation is accounted for (mapped, refused, or indeterminate, with the three counts summing to the log), and the hand-built logs include each case.
- **Sabotage:** S-adapt-1, S-adapt-2, S-adapt-3
- **Verifiable here:** yes — hand-built logs and the simulator run locally
- **Prediction:** Keeping a timed-out client's later operations on the same logical client, instead of a fresh one, rejects correct histories: at least 5% of the 300-execution sample goes red under that variant, because a client's next operation then appears to follow its indeterminate one in real time, which forbids the indeterminate one from taking effect after it. **Observable:** rejected runs in the sample under the variant, against none with the adapter as decided.
- **Outcome:** pending

### P5-02 — Indeterminate operations, constructed exactly

- **Task:** Three exact executions in a `ManualCluster` with clients, each checked by the WGL checker and the brute-force oracle: (a) a write times out, commits later, and a read observes it; (b) a write times out and is truncated with its deposed leader, and a read does not observe it; (c) a retried `Append` committed twice. All three must be accepted. The two treatments spec §6 names as wrong are measured on the same executions: indeterminate-as-failed and indeterminate-dropped.
- **Vacuity:** A construction whose steps do not happen as intended (the write never commits, the truncation never happens) passes without testing anything. Guarded: each step asserted by what the disks and the client log hold, as P4-04 did.
- **Sabotage:** S-indet-1, S-indet-2
- **Verifiable here:** yes — exact constructions run locally
- **Prediction:** Both wrong treatments reject (a), as spec §6 says, and both accept (b). Indeterminate-as-failed also rejects (c), because the second copy of the value has no completed operation that wrote it. **Observable:** each treatment's verdict on each construction.
- **Outcome:** pending

### P5-03 — The checker accepts the histories of generated runs

- **Task:** The WGL checker over every execution of the soak sample (300 in the suite), with the adapter of P5-01. The report adds: operations per history, indeterminate operations, refusals, retried duplicates, reads, `Cas` true and false, `Delete`, the largest per-key sub-history, states explored at most, and `Unknown` verdicts (decision 3).
- **Vacuity:** Histories with no indeterminate operations, no concurrency or no reads are accepted by weak checkers too. Guarded: each of those mechanisms must clear the rate floor in the sample, as effects (P3-10's rules), and the budget's headroom is reported.
- **Sabotage:** S-lin-1, S-lin-2
- **Verifiable here:** yes — the sample in the suite; the soak locally
- **Prediction:** Every history in the sample is accepted, with no `Unknown`. The largest per-key sub-history stays under a tenth of the default budget in states explored, because keys are few (three) but think time keeps concurrency at about three operations. Indeterminate operations appear in at least 30% of executions, since every crash or partition of a leader times out its clients. **Observable:** the verdict counts, the maximum states explored, and the indeterminate rate.
- **Outcome:** pending

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

New series: S-adapt, S-indet, S-lin (S-hist is taken: S-hist-1..7 are phase 0 and 2's checker entries). S-soak-6 follows S-soak-1..5. Each id's
`sabotage/<id>/` entry lands in the same commit as the check it proves, and is run on that commit
before it is pushed.
