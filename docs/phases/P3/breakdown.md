# Phase 3 — task breakdown

Leader election, with `currentTerm`/`votedFor` persistence and the §6 disruption rule (spec §11
phase 3). Preceded by the sabotage-target audit, the register row opened at P2 acceptance. Format
as in `docs/phases/P2/breakdown.md`, parsed by `gates breakdown`; outcomes will say `(evidence)` or
`(forcing)`.

**Done when** (spec §11): invariants 1 (Election Safety), 8 (Vote Uniqueness), 9 (Term
monotonicity) and 11 (bounded liveness in a stable suffix) hold across 10,000 seeded executions
including partitions, asymmetric ones included, and crashes. The fsynced-then-lost positive control
goes red. The distribution is reported. In addition:

- every harness entry's target is shown to go red on its sabotage's mechanism, not incidentally
  (the P2 register row);
- the message codec's canonical form is decided and tested (the register row promised to P3).

## Ordering

P3-01 first: until the audit is done, every harness entry is verified to turn its target red and
nothing verifies why, and phase 3 is where the invariants start to matter. Then the pieces Raft
needs before any Raft exists: P3-02 (codec), P3-03 (the term-and-vote record), P3-04 (the four
invariant checkers, proven against hand-built traces). Then P3-05 (election, tests first), P3-06
(the disruption rule), P3-07 (the positive control), P3-08 (10,000 executions and the
distribution), P3-09 (shrinking an election-safety failure end to end).

**Blocking set:** all nine. P3-09 is the only one that could slip to phase 4 without leaving a
phase-3 criterion unmet, and I do not propose it slips.

## Decisions for review

1. **The invariant checkers live in a new test project, `Raft.Core.Tests`** (spec §4 names it:
   "Figure 3's properties as executable invariants"). It references `Raft.Core` and
   `Raft.Simulation`. No production project changes shape, and the simulator does not learn Raft.
2. **The 10,000 executions run in their own CI job, `soak`, not in the test suite.** The suite is
   run by every harness entry that targets a Simulation or Core test (about 60 of them). 10,000
   executions there would multiply the harness's cost by that many. The suite runs a fixed sample
   (300 executions) with the same checkers; `soak` runs 10,000 at the head of each push, sharded
   like the harness if one runner cannot do it inside 15 minutes, and becomes a required check.
   Commits below the head get the 300-execution sample through the per-commit matrix. The phase's
   criterion is met by `soak` on the report's certified commit. **Approved with a consequence (reviewer):** the soak is the only
   place the invariants run at scale, and the sample is what every harness entry checks, so a
   sabotage that breaks only at execution 4,000 is caught by the soak and not by the harness. The
   harness's verdict is a claim about the sample. This is written into spec §12, where the harness
   is described. The soak is required and never waived; skipping it is a spec change argued first.
   Its distribution is reported as effects, as P2-02 restated coverage: 10,000 executions that
   never isolate a node are 300 at extra cost.
3. **Invariant 11 in phase 3 is "a leader within K election timeouts".** Its second clause, "a
   submitted command commits", needs a log. It joins in phase 4, and the checker is written so that
   adding it changes the property, not the observation.
4. **Leadership is observed two ways, and disagreement is its own failure.** "Elected" is a quorum
   of granted votes for (candidate, term), counting the candidate's own durable vote. "Acting" is a
   node sending leader messages (heartbeats) in that term. Election Safety is checked on "elected".
   A node acting as leader without an observed quorum fails separately, and so does the reverse
   beyond a bound. Two mechanisms with one observable, where the weaker silently substitutes, is the
   CRDT project's §13.31 shape. Here both are checked and must agree.
5. **Timing parameters:** election timeout drawn uniformly from [150, 300) ticks, heartbeat every
   50, K = 10 election timeouts, network delay as today (1–10). K is generous on purpose (spec §5);
   P3-08 measures the real distribution and the report states the margin.
6. **No new dependencies.**

## Tasks

### P3-01 — Sabotage-target audit: each target goes red on its mechanism

- **Task:** Every harness entry gains a `mechanism:` line naming the behaviour its patch breaks. For each entry I record, in a committed audit table (`docs/design/sabotage-audit.md`), its target, the mechanism, any incidental path by which the patch could turn the target red (a trace-format change, an extra random draw, a shifted golden value), and a verdict: *direct* (the target tests the mechanism and nothing else plausibly reds it), *retargeted* (a narrower existing or new test catches the mechanism), or *controlled*. A controlled entry carries `control.diff`: the patch with its mechanism removed and its incidental changes kept. The harness applies it and requires the target to stay **green**; a control that turns the target red means the entry measures something else. Any entry whose target is on a committed list of broad tests (`ci/broad-targets.txt`: the golden trace, byte-identity across processes, the generated-distribution and coverage tests) must be retargeted or controlled. Closes the register row.
- **Vacuity:** A control that is empty, or identical to nothing the patch touched, leaves the target green trivially. Guarded: a control must be non-empty, must differ from the patch, must share at least one hunk with it, and for a code entry must change at least one assembly (the existing not-compiled-in check). Also, an audit that marks everything *direct* passes the mechanical gate. That judgement is not scripted; the table records the reason for every *direct* verdict, and the report says how many there are.
- **Sabotage:** S-audit-1, S-audit-2, S-audit-3
- **Verifiable here:** yes — the harness runs controls locally like any entry.
- **Prediction:** Of the 112 harness entries, at least 10 name a broad target. At least 3 of those have no existing narrower test that catches their mechanism, so the audit adds tests, not only retargets. At least one currently green entry's control turns its target red: an entry whose "caught" is incidental today, as S-rare-1's would have been if its target had gone red at all. **Observable:** the audit table's counts: broad targets, entries needing a new test, controls that go red on first run.
- **Outcome:** pending

### P3-02 — The message codec, and its canonical form

- **Task:** `RequestVote`, `RequestVoteResponse`, `AppendEntries` (entries empty in phase 3, but the field exists) and `AppendEntriesResponse` as Core types, with a codec in Core: a type byte, fixed-width big-endian integers, a length for every variable part. Decoding rejects trailing bytes, out-of-range enums and booleans other than 0 or 1. The rule decided here: **every byte string decodes to at most one message, and a message has exactly one encoding.** It is tested both ways: `decode(encode(m)) = m` over generated messages, and `encode(decode(b)) = b` for every `b` that decodes, over byte strings built by mutating valid encodings. Written down in `docs/design/node-interface.md`. Closes the register row.
- **Vacuity:** The second property is vacuous if almost nothing decodes: random bytes rarely parse. Guarded: the mutated inputs start from valid encodings, and the test requires a floor of them to decode (reported), so the property is exercised on thousands of distinct decodable inputs.
- **Sabotage:** S-codec-1, S-codec-2, S-codec-3
- **Verifiable here:** yes — the codec and its properties run in the local suite
- **Prediction:** Canonical form does not make corruption visible, which is the risk the register row names. A corruption that yields a valid alternative encoding is still invisible, and with fixed-width integer fields most single-byte corruptions do exactly that. **Observable:** among single-byte flips of valid `RequestVote` encodings, at least 80% decode to a different valid message. If so, the report states that canonical form removes only the two-encodings ambiguity, and that detecting corruption would need a checksum. The simulator does not inject corruption (§7), so none is added in this phase.
- **Outcome:** pending

### P3-03 — The term-and-vote record: durable, checksummed, recoverable

- **Task:** `currentTerm` and `votedFor` persisted as an append-only file of length-prefixed records, each with a checksum (§8). Recovery takes the last valid record, truncates a torn **final** record, and refuses to start on a checksum failure in any earlier record (corruption, not a torn write). Written through `PersistAppend`, so the barrier orders it before any dependent send. Tested under every `SimDisk` crash mode (pending, torn, reordered) and against hand-built files: a torn tail, corruption in the middle, an empty file, a record with a valid checksum over the wrong length.
- **Vacuity:** Recovery tested only on files the writer produced never meets a torn or corrupt record, and "takes the last valid record" is untested. Guarded: hand-built files for each case, and the crash-mode tests assert which recovery path ran (truncated, refused, clean), with a count per path over the runs.
- **Sabotage:** S-pstate-1, S-pstate-2, S-pstate-3
- **Verifiable here:** yes — hand-built files and SimDisk crash modes run locally
- **Prediction:** Under the simulator's crash modes, only torn loss ever exercises the truncation path. A reordered loss that keeps a later append and drops an earlier one leaves a file of whole records, because `SimDisk` applies survivors in issue order at the file's end. So reordering never produces a checksum failure, and the refuse path is reached only by the hand-built corruption test. **Observable:** over the crash-mode test runs, the recovery-path counts: truncated only under torn, refused zero times outside the hand-built test.
- **Outcome:** pending

### P3-04 — Invariants 1, 8, 9 and 11 as checkers, proven against hand-built traces

- **Task:** Four checkers over a simulated execution, reading only what the simulator observes: decoded messages on the wire (sent and delivered) and each node's durable term-and-vote records, never a node's report about itself. Election Safety: at most one candidate *elected* per term over the whole run, where elected is a quorum of granted votes including the candidate's own durable vote (decision 4). Acting-without-quorum fails separately. Vote Uniqueness: per node and term, at most one candidate across its durable vote records and its granted responses, restarts included. Term monotonicity: per node, the term in its durable records and its sent messages never decreases, across restarts. Liveness (11): after the schedule's last fault heals, with a majority up, a candidate is elected and acts within K election timeouts. Each checker returns counts (elections, grants, term advances, time to leader) alongside its verdict. Before any Raft exists, each is proven on hand-built traces, every rejecting trace with an accepted near-miss twin (the P2-04 pattern).
- **Vacuity:** Every one of these has a degenerate execution that satisfies it: no leader, one term, no votes (spec §5). Guarded: the checkers report their mechanism counts, and every test that runs Raft asserts them (elections > 0, term advanced, at least one vote denied), never only the absence of a violation. Liveness is the anti-vacuity property for the other three, and its own vacuity (no stable suffix in the schedule) is guarded by requiring a stable suffix of at least 2K timeouts in every checked execution.
- **Sabotage:** S-inv-1, S-inv-2, S-inv-3, S-inv-4, S-inv-5, S-obs-1
- **Verifiable here:** yes — hand-built traces run locally
- **Prediction:** The obvious observation is wrong for at least one of the four. The wire alone gives a false negative for Vote Uniqueness: a granted vote whose response is dropped never appears on the wire, so a node that grants A (response dropped) and then B shows one grant. Only its durable vote records show two. **Observable:** among the hand-built traces, at least one rejecting trace that a wire-only version of a checker accepts. The count is reported per checker.
- **Outcome:** pending

### P3-05 — Leader election in Core, tests first

- **Task:** The three states; a randomized election timeout from `IRandomSource`; `RequestVote` and its response; step down on any higher term; heartbeats as empty `AppendEntries`; the term-and-vote record persisted before any send that depends on it, including the candidate's vote for itself (§8's example: persisted after the send, it gives two leaders). The unit tests in `Raft.Core.Tests` are hand-built input sequences with expected effect lists. They are written first and run against a stub node that returns no effects; the red output is recorded in the findings log, and they land with the implementation, never as a red commit. The Figure 2 checklist rows for elections become *implemented* in the traceability table, each naming its test. The README's "there is no Raft yet" changes.
- **Vacuity:** Unit tests that assert only the absence of a wrong effect pass on a node that does nothing, which is exactly the stub. Guarded: they are run against the stub first, and every one must fail there. A test that passes against the stub is rewritten before the implementation starts.
- **Sabotage:** S-elect-1, S-elect-2, S-elect-3, S-elect-4, S-elect-5
- **Verifiable here:** yes — unit tests and simulated runs run locally
- **Prediction:** In fault-free runs of three nodes, split votes are rare. A term with a candidate and no winner happens in fewer than 5% of elections, because the timeout spread (150 ticks) is fifteen times the largest network delay. **Observable:** over 200 fault-free executions, the checkers' election counts: terms with candidates, terms with a winner, and the ratio.
- **Outcome:** pending

### P3-06 — The §6 disruption rule, and liveness under asymmetric partitions

- **Task:** A node ignores a `RequestVote`, without updating its term, within the minimum election timeout of hearing from a current leader (§6 of the paper). Invariant 11 is asserted under generated asymmetric partitions, and under a constructed one: a node that can send to its peers but receive from none.
- **Vacuity:** A disruption-rule test where no disruptive `RequestVote` ever arrives inside the window passes with the rule deleted. Guarded: the test counts the `RequestVote`s the rule ignored, and requires them to be above zero; the generated coverage gains the effect "RequestVote ignored by the disruption rule" (P3-08).
- **Sabotage:** S-disrupt-1, S-disrupt-2
- **Verifiable here:** yes — constructed and generated partitions run locally
- **Prediction:** Without the rule, the constructed one-way partition fails invariant 11 in at least half of 200 executions: the node that hears nothing times out, raises its term, and its `RequestVote`s depose each new leader. With the rule, in none. **Observable:** the invariant-11 failure count with the rule on and with it removed, over the same 200 seeds.
- **Outcome:** pending

### P3-07 — The fsynced-then-lost positive control goes red

- **Task:** An expected-failure test: the simulator's `LoseSynced` disk fault, which Raft cannot survive, must turn Election Safety or Vote Uniqueness red (§5). Placed by state, as in P2-02: a node loses its durable term-and-vote record at a crash that follows a granted vote, then restarts inside the same term's election. Time-placed injection is measured too, for the record.
- **Vacuity:** A positive control that is red for a reason other than the lost vote (any bug) proves nothing about the checkers' power. Guarded: the same seeds with the fault removed must be green, and the red run's failing invariant must name the node that lost its record.
- **Sabotage:** S-pos-1, S-pos-2
- **Verifiable here:** yes — the expected-failure runs are local
- **Prediction:** Time-placed `LoseSynced` at the default crash rate turns an invariant red in fewer than 5% of executions: the loss has to land between a granted vote and the end of that term's election. The state-placed version does it in more than half. **Observable:** red counts over 200 executions for each placement.
- **Outcome:** pending

### P3-08 — 10,000 seeded executions, and the election distribution

- **Task:** The `soak` job (decision 2) runs the four checkers over 10,000 generated executions with partitions (asymmetric included) and crashes, sharded if needed. It reports the distribution as effects, measured by use and not existence (P2's pattern): elections per run, split votes, a leader crashed and replaced, a vote denied because one was already cast, a `RequestVote` ignored by the disruption rule, a leader stepping down on a higher term, a candidate elected after restarting from disk, an election during an asymmetric partition, and time from stable suffix to leader. The P2 rules apply: the floor, the 95% always-on declaration, identical execution sets. The 300-execution sample in the suite applies the same checkers and asserts the mechanism counts.
- **Vacuity:** 10,000 green executions with no leader changes, no split votes and no crashed leaders prove little. Guarded: the coverage floor fails any effect below it unless it is declared rare with a reason, and each checker's mechanism counts are asserted per run.
- **Sabotage:** S-soak-1, S-soak-2, S-cov-7
- **Verifiable here:** partial — the sample and a local soak run; the sharded CI job only in CI.
- **Prediction:** One runner cannot do 10,000 executions of 20,000 ticks inside 15 minutes, because a Raft node's step is heavier than the echo node's: the P1 echo cluster ran at about one step per tick per node. I expect at least two `soak` shards. The time to a leader after the stable suffix stays under 3 election timeouts in every execution, so K = 10 has more than threefold margin. **Observable:** the soak job's per-execution cost and shard count, and the maximum time-to-leader over the 10,000.
- **Outcome:** pending

### P3-09 — Shrinking an election-safety failure end to end

- **Task:** The positive control's failure, shrunk with the invariant as its signature (`election-safety@term` or `vote-uniqueness@node`), the way P2-10 shrank a linearizability failure. The shrunk schedule reproduces from its text form in another process.
- **Vacuity:** A shrink that returns the input unchanged, or keeps a different failure, passes "shrinks and still fails". Guarded: the signature must match, the result must be 1-minimal (the existing check), and the result must be strictly smaller than the input.
- **Sabotage:** S-shrink-5
- **Verifiable here:** yes — the shrinker runs locally
- **Prediction:** With a state-placed loss, the shrunk schedule is at most 2 events, the state-placed crash and nothing whose role is timing (P2's finding: state placement removes enablers). A time-placed loss keeps at least one extra event whose only role is timing. **Observable:** the shrunk sizes for both placements.
- **Outcome:** pending

## Sabotage ids

New series: S-audit, S-codec, S-pstate, S-inv, S-elect, S-disrupt, S-pos, S-soak. S-cov-7 follows
S-cov-1..6 and S-shrink-5 follows S-shrink-1..4. Each id's `sabotage/<id>/` entry lands in the
same commit as the check it proves, and is run on that commit before it is pushed.
