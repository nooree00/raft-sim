# Phase 4 — task breakdown

Log replication and log persistence (spec §11 phase 4). Format as in
`docs/phases/P3/breakdown.md`, parsed by `gates breakdown`; outcomes will say `(evidence)` or
`(forcing)`.

**Done when** (spec §11): invariants 2 (Leader Append-Only), 3 (Log Matching), 4 (Leader
Completeness), 5 (State Machine Safety), 6 (committed entries are durable), 7 (no spurious commit)
and 10 (entry uniqueness) hold; the election restriction (§5.4.1) is re-tested now that logs are
non-empty; a crash-during-write test passes; committed entries survive any crash schedule,
including all nodes crashing at once. In addition, carried from phase 3:

- invariant 11 gains its second clause, "a submitted command commits" (P3 decision 3), and with it
  the disruption churn P3-06 measured becomes visible to the invariant itself;
- invariants 1, 8, 9 and 11 keep holding over the 10,000-execution soak, now with logs, and the
  soak reports the new distribution as effects under the rate floor (P3-10);
- the positive control reaches the third invariant spec §5 names, Leader Completeness.

## Ordering

P4-10 first, the reviewer's fix at approval for the red acceptance run: a report commit that is not
the head of its push fails `gates each-commit`, locally. Then P4-01, before any log exists in Core (spec §12, tests first): the instruments spec §5 names,
ghost entry ids and commitment in fact, and the checkers for 2–7 and 10, proven against hand-built
traces. Then the pieces replication needs underneath it: P4-02 (the log file, with
crash-during-write at every offset). Then P4-03 (replication in Core, tests first), P4-04 (the
election restriction and Figure 8, the rule a log makes testable), P4-05 (durability under every
crash schedule), P4-06 (invariant 11's commit clause, with clients), P4-07 (the soak and its
distribution), P4-08 (the positive controls), P4-09 (every configured limit exercised at its
largest legitimate value, spec §10).

**Blocking set:** all eleven (P4-11 added at the ceiling, before the push). P4-09 is the only one that could slip without leaving a phase-4
criterion unmet (it is §10's rule, not §11's), and I do not propose it slips.

## Decisions for review

1. **Ghost entry ids are assigned by the checker from observations, not carried in the bytes.**
   Spec §5: "when a leader creates an entry, the simulator records a unique id for it". Carrying an
   id inside the entry would make it the node's report about itself, and two distinct `Put x 1`
   entries must still differ. So each persist the simulator observes records the step that issued
   it: the input being handled, and its position among that step's effects. The simulator still
   does not learn Raft (P3 decision 1).
   - **Creation:** in `Raft.Core.Tests`, an entry a node appends while it is the leader of that
     entry's term, at an index it did not hold, is a creation, and gets a fresh id keyed by (node,
     step, position).
   - **Provenance:** a follower's entry takes the id of the leader's entry it came from, traced
     through the `AppendEntries` delivery that carried it (message ids are observed) and the
     leader's log at the time it sent.
   - **Why this shape:** entry uniqueness (10) is then a property of creations, and Log Matching (3)
     compares ids, never bytes.
2. **Commitment in fact is computed from `Durable` observations only** (spec §5: "stored durably
   (fsynced) on a quorum"). The checker never takes commitment from `Issued` writes or from a
   node's `commitIndex`; claimed commitment comes from the node's `LeaderCommit` on the wire and
   from what it applies. P3-08 showed the durable view lags the node by one disk latency. Here that
   lag is the point: an entry is committed in fact only once it is durable.
3. **"Applied" is the one observation with no state to derive it from in phase 4, and is declared
   so** (spec §9).
   - **The problem:** a node applies committed entries to the key-value state machine (spec §6's
     five operations, in Core) and emits `apply index=i`. That event is a self-report. In phase 4
     no state machine output is persisted, so nothing independent can confirm it.
   - **The mitigations:** the checker cross-checks each `ClientResponse` against the entry the
     response's request created. State Machine Safety (5) is stated over applies, as the spec
     requires.
   - **What waits:** the history those responses form is recorded now and judged for
     linearizability in phase 5.
   - **The limit (reviewer, at approval):** a node that applied the *wrong* entry and responded
     consistently with what it applied passes both the self-report and the client cross-check. The
     cross-check catches a node that responds without applying, not one that applies wrongly.
     Phase 5's linearizability check over the recorded history is what closes that gap; until
     then, phase 4's state machine safety is exactly as strong as the node's own report.
4. **The log gets its own file, separate from the term-and-vote record.**
   - **Records:** the framing already proven in P3-03 (length, payload, checksum). Each record
     carries (index, term, command).
   - **Conflict resolution:** truncate to a record boundary, then append.
   - **Recovery:** rebuilds the log from the records in order, and a record for index *i*
     discards everything at *i* or above. A reordered loss of a truncate and a later append
     therefore cannot resurrect a stale suffix (P4-02's prediction).
   - **Torn and corrupt records:** recovery rules unchanged (spec §8).
5. **Clients talk to any node; a non-leader refuses with the leader it last heard from.** There is
   no forwarding, and no sessions until phase 8.
   - **Duplicates are expected:** a timed-out client retries, so a duplicated `Append` is expected
     in phase 4 and is not a phase-4 failure (phase 8's criterion is the one that forbids it).
   - **What the commit clause counts:** invariant 11's commit clause counts a command committed by
     its ghost id, not by its bytes.
6. **A leader appends a no-op entry at the start of its term** (paper §8). Without it, entries from
   earlier terms commit only when a client command arrives, and the commit clause of invariant 11
   would depend on client timing. The no-op is an entry like any other: it gets a ghost id and it
   counts for every invariant.
7. **The P3-10 declaration is expected to go.** Writes completed out of order at a crash sits below
   the soak's rate floor (8 of 10,000), declared, because a node with no log writes only on term
   and vote changes. With log appends, two writes in flight at a crash should be common. P4-07
   predicts it clears 1% and removes the declaration. If it does not, the choice returns to you.
8. **No new dependencies.**

## Tasks

### P4-01 — Ghost entry ids, commitment in fact, and the log invariants as checkers

- **Task:** Before any log exists in Core, build the instruments and checkers. **Observation:** the simulator's `Issued` observation gains the issuing step (input and effect position). **Instruments** in `Raft.Core.Tests`: ghost ids by creation and provenance (decision 1), each node's durable logical log decoded from `Durable` observations, and commitment in fact (decision 2). **Checkers:** invariants 2 (over the logical log), 3 (the (term, index) → ghost id map), 4 (committed in fact ⇒ in every later leader's log), 5 (by ghost id, globally across restarts), 6 (every committed-in-fact entry survives in the durable logs of a quorum after every crash), 7 (claimed ⊆ in fact) and 10. **Proof on hand-built traces:** each checker rejects a violating trace and accepts a twin one step away, and each asserts its mechanism with a count (entries committed, leaders with non-empty logs, terms with a commit).
- **Vacuity:** Every one of these checkers is satisfied by empty logs, one term, or no commits (spec §5). Guarded: each checker reports its mechanism counts and each hand-built accepting trace must have commits across at least two terms; a checker that sees no entries reports it, and the simulated tests of P4-03 on assert the counts.
- **Sabotage:** S-loginv-1, S-loginv-2, S-loginv-3, S-loginv-4, S-loginv-5, S-loginv-6, S-loginv-7, S-ghost-1
- **Verifiable here:** yes — hand-built traces need no Raft log
- **Prediction:** Assigning ghost ids by bytes instead of provenance misses a real violation. Two leaders of different terms each create `Put x 1` at the same index; one overwrites the other on a follower, and by bytes it is the same entry. A bytes-based State Machine Safety accepts that trace. Provenance rejects it. **Observable:** the hand-built trace with two byte-equal creations is accepted by a bytes-keyed variant of invariant 5 and rejected by the ghost-id version; the count per checker of traces a bytes-keyed variant misses.
- **Outcome:** right (evidence) — the hand-built trace with two byte-equal `Put x 1` creations at one index, in different terms, one overwriting the other on a follower, is accepted by the bytes-keyed variant of invariant 5 and rejected by the ghost-id version. Only invariant 5 has a bytes-keyed variant to compare: the other checkers compare ghost ids by construction, so the count of traces a bytes-keyed variant misses is 1 of 1. Every checker rejects its violating trace and accepts its twin (S-loginv-1..7, S-ghost-1 caught).

### P4-02 — The log file: append, truncate a conflicting suffix, recover from any crash

- **Task:** A generalised record file with the P3-03 framing, plus the log's own format (decision 4). **Operations:** append a batch of entries; truncate the log to an index (a record boundary) before appending after a conflict. **Recovery:** truncates a torn final record, refuses any earlier checksum or length failure, and applies later-index-wins. **The crash-during-write test (spec §11):** a log is written through `SimDisk` and crashed after each write in turn, in every loss mode (pending, torn at every byte offset, reordered). Each crash must recover to a prefix of what was written, never refuse, and never contain an entry that was not written at that index.
- **Vacuity:** A crash test that only crashes between whole records never exercises the torn path, and one that never truncates never exercises a stale suffix. Guarded: the test counts recoveries per path (truncated torn tail, whole-record prefix) and per operation that was in flight (append, truncate), and each count must be non-zero.
- **Sabotage:** S-logfile-1, S-logfile-2, S-logfile-3
- **Verifiable here:** yes — the simulator and the unit tests run locally
- **Prediction:** Without an index in each record, a reordered loss that drops a truncate and keeps the append after it recovers the new entry at the wrong index, after the stale suffix it was meant to replace. I expect the crash-mode test to find this on its first run against an index-free format; with indices and later-index-wins it cannot. **Observable:** the crash-mode test's failures against an index-free record format, run once and recorded, then zero with decision 4's format.
- **Outcome:** partly (evidence) — the index-free format failed the crash-during-write test on its first run, 60 failures, as predicted. But indices with later-index-wins were not enough: 3 reordered crashes recovered a surviving append after a lost truncate-and-append, joining two logs that never coexisted (findings). Each record now also carries the previous entry's term and recovery takes only records that chain; the test then passes with 0 failures. The record format deviates from decision 4 by that field.

### P4-03 — Log replication in Core, tests first

- **Task:** Replication and commitment in `RaftNode`, with every behaviour written first as a unit test over hand-built inputs and expected effects, and run against a stub. **Replication:** `AppendEntries` with the consistency check on (prevLogIndex, prevLogTerm); conflict resolution by truncating the follower's suffix; `nextIndex` backtracking on rejection; `matchIndex` from successful responses. **Commitment:** commit by majority with Figure 2's rule `log[N].term == currentTerm`. **Persistence ordering:** a follower's entries are durable before it replies success, and a leader counts its own entry only once it is durable (spec §8). **Leadership:** the no-op at the start of each term (decision 6). **Apply and clients:** committed entries are applied in order to the key-value state machine; client requests are handled per decision 5. **In the simulator:** fault-free three-node runs, where every invariant from 1 to 10 holds and each run commits entries in more than one term.
- **Vacuity:** Fault-free runs where one leader holds office for the whole run satisfy every log invariant trivially (one term, no conflicts). Guarded: the simulated test asserts commits in at least two terms per run, and at least one conflicting suffix truncated in some run of the sample; the unit tests assert the effect order (persist before send) for every reply.
- **Sabotage:** S-repl-1, S-repl-2, S-repl-3, S-repl-4, S-repl-5, S-repl-6, S-repl-7, S-repl-8
- **Verifiable here:** yes — the simulator and the unit tests run locally
- **Prediction:** Backtracking `nextIndex` one entry per rejection is fast enough here. The leader retries on each rejection, not on the next heartbeat. So a follower 1,000 entries behind catches up within one election timeout (150 ticks) of rejoining, and the optimisation the paper mentions (the follower returning its conflict term) is not needed. **Observable:** catch-up time in ticks for a follower isolated while the leader commits 1,000 entries, measured once in a constructed run and written beside the assembly.
- **Outcome:** wrong (evidence) — one entry per rejection was not fast enough: a follower 3,008 entries behind took 1,033 ticks to catch up, far beyond one election timeout. With the rejection carrying a hint (the follower's last index, or the index before the leader's probe), the same follower caught up in 599 ticks, and in the committed test a follower 1,201 entries behind catches up 399 ticks after healing. The hint is the follower's log length, not the paper's conflict term.

### P4-04 — The election restriction and Figure 8, now that logs are non-empty

- **Task:** Re-test the election restriction and the current-term commit rule with real logs. **Unit tests for §5.4.1:** a voter denies a candidate whose log is less up to date, by last term and then by length, and grants one whose log is at least as up to date. **Simulated:** a node with a stale log times out first, and must not win. **Figure 8, state-placed on three nodes:** The leader of term 2 writes an entry that reaches one follower. It crashes, and a leader of term 3 writes a different entry at that index on itself only. The term-2 leader returns in term 4 and replicates its term-2 entry to a majority. The term-3 node wins again. **What must hold:** the term-2 entry must not be claimed committed in step 3. Invariants 4, 5 and 7 must hold. **The sabotages the construction exists for:** committing by counting copies, and dropping the up-to-date check.
- **Vacuity:** A Figure 8 construction whose steps do not happen in the intended order (the returning leader never replicates the old entry, or the term-3 node never wins again) passes without testing the rule. Guarded: the test asserts each step by observation (the term-2 entry durable on a majority in term 4; a later leader of term 5 or above whose log lacks it) and counts the runs where the full shape occurred, which must clear the floor.
- **Sabotage:** S-commit-1, S-commit-2, S-restrict-1
- **Verifiable here:** yes — the simulator and the unit tests run locally
- **Prediction:** With the commit-by-counting sabotage, the construction turns invariant 7 (no spurious commit) red in every run where step 3 happens: the claim is made the moment the old entry reaches a majority. Invariants 4 and 5 go red in fewer than half of those runs, because they need step 4 as well, and step 4 needs another election the term-3 node must win. So invariant 7 is the one that catches the bug at its first step, as spec §5 says. **Observable:** per invariant, the red count under the sabotage over 200 runs of the construction, and the count of runs where each step happened.
- **Outcome:** partly (evidence) — under S-commit-1 the construction goes red on invariant 7 at the step where the old entry reaches a majority, as predicted. The other half has no sample: the construction is exact (a hand-driven `ManualCluster`), one execution rather than 200 runs, so "fewer than half" cannot be counted. Found while building it: with the default batch the old entry travels with the new leader's no-op in one write, is committed legitimately, and the Figure 8 shape cannot occur (a test records that the default masks it; the Figure 8 test uses a batch of 1).

### P4-05 — Committed entries survive every crash schedule, including all nodes at once

- **Task:** Invariant 6 under crashes. **Generated schedules:** the soak's generator with `CrashAll` and `CrashMajority` included (P2-02's state-placed faults). **The constructed case:** entries are claimed committed; every node then crashes at once, with unsynced writes lost; all restart; every entry committed in fact must be in the recovered logs of a quorum and in every later leader's log. **Recovery:** also runs recovery on every crash of a node whose log write was in flight, the P4-02 paths in a live cluster.
- **Vacuity:** A crash-all test where nothing had been committed, or where no write was in flight at the crash, proves nothing about durability. Guarded: the test requires committed-in-fact entries at the moment of the crash, and counts the crashes that caught a log write in flight, which must clear the floor.
- **Sabotage:** S-dur-1, S-dur-2, S-dur-3
- **Verifiable here:** yes — the simulator and the unit tests run locally
- **Prediction:** Acknowledging `AppendEntries` before the entries are durable does not violate invariant 6 in generated runs at the default crash rate. The window is one disk latency, and a majority must crash inside it. It does in more than half of the constructed crash-all runs, where every node crashes right after acknowledging. **Observable:** red counts, generated against constructed, with the ack-before-durable sabotage.
- **Outcome:** wrong (evidence) — S-dur-1 is a follower acknowledging before its write, S-dur-2 recovery cutting the final whole record, S-dur-3 (added) a leader sending a client's entry before persisting it. Under S-dur-1, 100 of 100 generated crash runs went red, and 50 of 50 constructed crash-all runs (every node down one tick after the first commit claim at or after 1500). Every one was red on invariant 7 (no spurious commit), none on invariant 6. The prediction assumed a copy counts once acknowledged. The checker counts only durable copies (commitment in fact), so an early acknowledgement is a claim ahead of the fact, visible at every claim with or without a crash, and a crash can never lose what was never counted. Unsabotaged: 100 generated runs green, entries committed before every crash, 31 crashes catching a log write in flight; 50 constructed runs green, each rerun making the probe's claim before the crash. S-dur-2 red on invariant 6 in all 100 runs. S-dur-3 survived the crash runs (findings: the latency bounds close its window) and is held by a unit test on the order of effects.

### P4-06 — Invariant 11's commit clause, with clients

- **Task:** Invariant 11 in full: after the last fault heals, with a majority up, a leader exists within K election timeouts *and a submitted command commits* (by ghost id, decision 5). **Clients:** P2-08's simulated clients submit commands to the Raft cluster, through the same network path as nodes. **The rule re-measured:** P3-06's one-way partition construction, with and without the §6 rule, now under the full clause.
- **Vacuity:** A clause checked only in runs where clients happen to submit during the stable suffix, or that counts a command committed because its bytes appear, can pass vacuously. Guarded: clients submit throughout the run, the clause is checked in most runs (asserted, as in P3-08) and matched by ghost id, and each run reports submitted and committed counts.
- **Sabotage:** S-live-1, S-live-2, S-disrupt-3
- **Verifiable here:** yes — the simulator and the unit tests run locally
- **Prediction:** Invariant 11's commit clause makes the disruption visible that its leader clause could not (P3-06). With the §6 rule removed, the one-way partition construction fails the commit clause in at least half of 200 runs: each deposition interrupts the in-flight command, and a leader that lasts about 900 ticks at a time loses commands to client timeouts. With the rule, in none. P3-06 predicted this for the leader clause and was wrong; this is the same prediction for the clause the churn actually breaks. **Observable:** commit-clause failures, rule on and off, over the same 200 seeds.
- **Outcome:** wrong (evidence) — over the 200 seeds of P3-06's construction, now with three clients and the log checkers: with the rule off, the commit clause failed in 49 runs, and with the rule on, in the same 49. Every one is a run where n3 was the leader when the one-way partition began: a leader that can send and not hear keeps its followers loyal, never learns of an acknowledgement, and commits nothing while the partition lasts. Only CheckQuorum ends that, which spec §2 excludes, and invariant 11 asks for a commit only after faults heal (findings). In the 151 other runs the clause failed 0 times with the rule off: leaders elected after the first window 4,236 (21.2 per run, against 0 with the rule), commands committed 74,470 against 148,810, but never a 3,000-tick stretch without a commit. The churn halves throughput and stays invisible to the clause. The suite runs 100 of the 200 seeds (74 s for 200 runs of both sides, each harness entry on Core pays it); it asserts the clause with the rule on where n3 did not lead, and S-disrupt-3 records the blind spot as a wrong-reason entry. The clause itself is proven on hand-built executions (`CommitLivenessTests`: an old entry with the same bytes, S-live-1; a new leader's no-op, S-live-2). The generated runs with a stable suffix are P4-07's soak.

### P4-07 — The soak with logs, and the replication distribution

- **Task:** The 10,000-execution soak runs invariants 1–11 (membership aside), with clients. **The new distribution, as effects:** entries committed per run; a follower caught up by backtracking; a conflicting suffix truncated; a leader crashed with uncommitted entries; an entry committed in a later term than it was created; a command retried and duplicated; a crash with a log write in flight. **Rules:** P3-10's (rate floor, 95% declaration, identical sets), with the rate beside every count. **The sample:** the 300-execution sample in the suite asserts the mechanism counts.
- **Vacuity:** Executions in which nothing is replicated under faults (clients idle, or a single term) pass every log invariant. Guarded: every effect above must clear the soak's rate floor, and each run must commit entries.
- **Sabotage:** S-soak-4, S-soak-5, S-cov-10, S-repl-9
- **Verifiable here:** partial — the sample and a local soak run; the CI job only in CI
- **Prediction:** Two outcomes, one about the distribution and one about cost. **Distribution:** writes completed out of order at a crash rises above the soak's rate floor (1%) once logs are persisted, so decision 7's declaration can be removed. A node now writes on every replicated batch, not only on term changes. **Cost:** the soak still fits one job under 15 minutes, although the per-execution cost roughly triples with logs and clients (P3's soak took 4 min 16 s on GitHub). **Observable:** that effect's rate in the soak, and the soak job's duration.
- **Outcome:** partly (evidence) — the distribution half was right and is the evidence; the cost half was wrong, then forced, and is counted as forcing. **Distribution:** writes completed out of order at a crash reached 165 of 10,000 (1.65%), above the 1% floor, and both declarations are removed. All invariants 1–11 held over 10,000 executions (27,880 elections, 1,649,740 entries committed in fact, fewest 92 in one execution); the commit clause was checked in 9,297, time to a committed command after the suffix at most 1,007 (window 3,000). Every replication effect cleared the floor: backtracking 69.8%, suffix truncated 21.2%, leader crashed with uncommitted entries 18.3%, committed in a later term 10.8%, retried and duplicated 18.5%, crash with a log write in flight 38.3%. **Cost:** with the load as planned (three clients, no pause) an execution cost 7.6 times a client-free one, and the soak would have taken about an hour, not three times P3's 4 minutes. A 100-tick client pause (`ClientCall.After`) brought it to 12 min 1 s locally for 10,000, about three times P3's local 4 minutes: the prediction's number, reached by changing the work, so it tests nothing. **Found on the way (findings):** the log checker judged a message by the sender's log at its release rather than its composition (fixed: `SentObservation.Step`); state-placed crashes now fire, which shortened the stable suffix, so runs go to 22,000; `clock-rate-diverged` now measures perceived time (57.6%, no longer near-always, its declaration removed); duplicates are counted over writes only, since a `Get` carries no unique value. A first S-soak-4, a leader counting a stale term's acknowledgement, survived every test, the soak sample included: it is now S-repl-9, held by a unit test, and S-soak-4 reinstates P4-03's term-after-truncation bug.

### P4-08 — The positive controls reach Leader Completeness and durability

- **Task:** Extend the fsynced-then-lost control (spec §5 names Election Safety, Vote Uniqueness *or* Leader Completeness). **The existing lost vote, state-placed:** now in runs with logs. **A lost log record:** a node loses its fsynced record of a committed entry at a crash right after acknowledging it, with the other follower unable to reach the leader, so that node was part of the committing quorum. **What must go red:** invariant 6 (committed entries durable) and, once a later leader lacks the entry, invariant 4. **The twin:** the same crash losing only unsynced writes is green in every run.
- **Vacuity:** A control that is red for a reason other than the lost fsync proves nothing about the checkers (P3-07's guard). Guarded: the twin runs, and each red must name the node and index that lost the record.
- **Sabotage:** S-pos-4, S-pos-5
- **Verifiable here:** yes — the simulator and the unit tests run locally
- **Prediction:** Losing the fsynced log record turns invariant 6 red in every run where the node was needed for the quorum, since the committed entry is then on a minority. It turns Leader Completeness red in fewer runs. A later leader must be elected without the entry, which needs the lying node's vote and not the leader's, so this is the same second-election dependence as P3-07 and P4-04. **Observable:** red counts per invariant over 200 runs, and the twin green in all 200.
- **Outcome:** partly (evidence) — over 200 runs, n3 isolated so that every commit needs n1 and n2, n2 losing its next fsynced write after 1000: invariant 6 went red naming n2's disk in 85 runs, and in the other 115 in none. The 85 are exactly the runs where n2 followed. Where n2 led, its own write completes before n1's copy, so the record it loses is of an entry not yet committed in fact, and the entry is then on n1 alone and never committed: "needed for the quorum" was not enough, the lost record must also be the one that completed the quorum. Leader Completeness went red in 0 runs: n2, restarted without the entry, cannot win n1's vote, and n3 is isolated (the second-election dependence the prediction named, here absolute). The twin was green in all 200. The lost-vote control now runs with three clients: term monotonicity names n2 in 200 of 200, vote uniqueness and election safety in 17, time-placed in 2 (39 and 9 at P3-07; Core has changed since, with the no-op and heartbeat-first, so the counts are not like for like).

### P4-09 — Every configured limit, exercised at its largest legitimate value

- **Task:** Spec §10: for every configured value, one test that performs the largest legitimate thing and asserts it succeeds. **Configured values:** the largest batch of entries per `AppendEntries`, the largest command, the codec's length fields for both, the log record's length field, the election timeout and heartbeat interval at their extremes, and K. **What each test does:** it uses the value at the limit, and the value one past it is refused where a refusal is the design.
- **Vacuity:** A limit test that passes a value well inside the limit does not notice the limit is in the wrong place. Guarded: each test names the constant it exercises and uses exactly that value, and a test fails if the constant changes without the test changing (the value is read from the constant, and the one-past value must be refused).
- **Sabotage:** S-limit-1, S-limit-2
- **Verifiable here:** yes — the simulator and the unit tests run locally
- **Prediction:** At least one limit is in the wrong place. My candidate: the message codec's length field for a batch caps the entries per `AppendEntries` below the batch size the leader uses. A full catch-up batch then fails to encode, and the follower never catches up. **Observable:** the first run of the largest-batch test.
- **Outcome:** wrong (evidence) — the codec does not cap the batch: its count and length fields are 32-bit, and a full batch of 64 commands of the largest size (64 MiB) encodes, decodes, is taken by a follower and recovered from its file on the first run. What was wrong was that two of the limits did not exist. There was no configured largest command (only the codec's and the record's 32-bit fields, about 2 GiB, and a full batch of those cannot encode), and no option was checked at all: a heartbeat of 149 against a 150 minimum was accepted and elected again in 57 of 100 fault-free runs, and a spread of one tick (150 to 151) was accepted and never elected a leader in 100 runs. Now: `MaxCommandBytes` (1 MiB, the leader answers `too-large|<max>`), and a node refuses options outside stated bounds: heartbeat at most a third of the minimum timeout (measured stable up to 141 here, so the bound keeps a margin), a spread of at least one heartbeat (measured: 2 ticks left 3 of 100 runs without a leader, 3 or more none), a batch of at least 1, and a largest command a full batch can encode. Each bound is tested at its value and one past it, the timing bounds together with a lost heartbeat (100 runs, one election each), and K's comparison is inclusive. These bounds are my choices, put to the reviewer in the report.

### P4-10 — A report commit must be the head of its push (at approval)

- **Task:** Reviewer's decision at P4 approval, for the red P3 acceptance run. `gates each-commit` (local), `each-commit-list` and `each-commit-collect` (CI) fail any non-head commit in the pushed range that changes a `docs/phases/P*/report.md`. A report certifies the commit that contains it and GitHub runs CI only for a push's head, so such a report can never be certified, and `gates reports`, the one gate the local sequence skips, is the only other place it would show. The local staged run gains the check as its own stage.
- **Vacuity:** A rule tested only on histories where the report is already at the head passes whether or not it exists. Guarded: a fixture push with the report commit followed by another must fail, naming the commit and the report; the same push with the report last must pass; and a non-head commit that changes only other docs must pass, so the rule cannot be a blanket ban on docs before the head.
- **Sabotage:** S-each-5, S-each-6
- **Verifiable here:** yes — the rule runs on fixture histories, and on this branch's own history
- **Prediction:** Run over the P3 acceptance push as it happened (`ff27d8e..b4ff572`), the check fails on exactly `2f9b8bc` and nothing else; run over the push that fixed it (`b4ff572..9c9e170`) it passes. This is known from the red run, so it is weak evidence; the part that could be wrong is a second report touched in that range, and I do not expect one. **Observable:** the check's failures on the two recorded ranges.
- **Outcome:** right (evidence) — over `ff27d8e..b4ff572` the check fails on exactly `2f9b8bc`, naming it and the report, and nothing else; over `b4ff572..9c9e170` it passes. Weak evidence, as stated: the result was known from the red run, and the part that could have been wrong (a second report in the range) was not.

### P4-11 — Simulation-scale tests in their own project, and a gate that every written test ran (at the ceiling)

- **Task:** The head's full local run failed the harness ceiling: shard 1 took 22.1 min for 27 entries (499 s fixed cost, about 31 s per entry), because every entry runs its target's whole project and `Raft.Core.Tests` now takes 81 s, most of it simulation-scale tests that unit-targeted entries do not need. **The split (reviewer's option A):** the cluster tests that run the simulator at scale (the soak sample, durability, disruption, positive controls, replication and election simulations, shrinking) move to a new project, `tests/Raft.Scale.Tests`; the checkers stay in `Raft.Core.Tests`, where they are proven on hand-built traces, and are compiled into the new project by link. Harness entries aimed at moved tests are retargeted; the soak script names the new project; the layering table gains its row. **The gate (reviewer's addition):** `gates testcount` also requires that every `[Fact]` and `[Theory]` written in a test project's sources was executed in that project's results, that no test is written under `tests/` outside a test project, and that every harness entry's target was executed in the project the entry names.
- **Vacuity:** The split's own risk is the CRDT project's npm-test finding: a test moved out of the project people run is a test that no longer runs, and the counts can be edited to match. Guarded by the written-versus-ran check, proven by a sabotage that drops a moved test file from compilation and edits the baseline to agree, which today's count check would pass.
- **Sabotage:** S-ran-1, S-ran-2, S-ran-3
- **Verifiable here:** yes — the harness shards run locally
- **Prediction:** Two parts. **Cost:** with the simulation-scale tests out, `Raft.Core.Tests` runs in under 20 s, and every harness shard fits the 15-minute ceiling locally, shard 1 included, in under 12 minutes: most entries target unit tests or other projects, and the fixed cost falls with the baseline checks. **The gate:** S-ran-2 (a moved file excluded, the baseline edited to match) passes the count check as it stands and fails the written-versus-ran check, naming the file's tests. **Observable:** the local shard times, and S-ran-2's result against the gate with and without the new check.
- **Outcome:** pending

## Sabotage ids

New series: S-loginv, S-ghost, S-logfile, S-repl, S-commit, S-restrict, S-dur, S-live, S-limit.
S-each-5..6 follow S-each-1..4, S-soak-4..5 follow S-soak-1..3, S-cov-10 follows S-cov-1..9, S-disrupt-3 follows S-disrupt-1..2 and
S-pos-4..5 follow S-pos-1..3. S-ran is new at P4-11. Each id's `sabotage/<id>/` entry lands in the same commit as the
check it proves, and is run on that commit before it is pushed.
