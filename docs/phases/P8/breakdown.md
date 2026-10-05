# Phase 8 — task breakdown

Client sessions and read-only queries (spec §11 phase 8, §5 item 6, paper §8). Format as in
`docs/phases/P7/breakdown.md`, parsed by `gates breakdown`; outcomes will say `(evidence)` or
`(forcing)`.

**Done when** (spec §11): a retried `Append` applies once, and the checker accepts every history
that includes reads. In addition, carried forward:

- spec §5 item 6: "the session table is part of the snapshot; otherwise a retry after compaction is
  applied again". Compaction exists now (phase 7), so this is checkable from the start;
- phase 7's finding: the invariants compare which entry was applied where, linearizability compares
  the answers, and the replay compares the state. Sessions add a fourth thing none of them sees
  directly: whether a command's *effect* happened once. A deduplication bug in the state machine is
  reproduced exactly by a replay that runs the same state machine, so the replay cannot be the
  oracle for it (spec §10: a party whose answer came from the other proves nothing). The oracle for
  "applies once" has to come from outside the state machine: the client history, with a retry
  recorded as the operation it is;
- read-only queries that bypass the log are invisible to every log invariant and to the replay, by
  construction. Linearizability is the only oracle for them, and its rejecting half for stale reads
  must be shown on reads served the new way, not only on the known-bad catalogue's hand-written ones;
- the reviewer's rule from phase 7's acceptance: a local timing within 5% of a deterministic ceiling
  is a failure on a slower runner. The harness shards run in CI only now; any new sample test that
  measures a rate stops at its floor unless the rate itself is the assertion.

**Approved** (reviewer): P8-00 to P8-10, all five decisions; stop and report after P8-10. Decision
1 is the phase's content: the replay runs the same state machine, so it would reproduce a
deduplication bug exactly, and recording a retry once puts the oracle outside the thing tested, the
only position from which it can see this (findings, pattern 1, its fifth instance). Decision 2: a
client-chosen id is a claim, an index is a fact. Decision 4: stale reads shown rejected before
ReadIndex exists, the P6-04 pattern. **One addition to decision 3:** an unbounded session table is
state that grows without limit and is snapshotted, so it has a size relationship with phase 7's
compaction. A limit test at the largest legitimate session count, with what that count does to the
snapshot's size measured and reported, so that phase 9 inherits a measurement, not a guess (P8-01).

## Ordering

The oracle first, because tests come first for `Raft.Core` and every later task is judged by it:
P8-00 (a retry is the same operation in the client history, with the known-bad history it must
reject). Then the state machine, which is the deduplication (P8-01), the session commands and
registration through the log (P8-02), and sessions across compaction (P8-03). Then reads: the
known-bad stale reads served the new way, against the checker (P8-04), ReadIndex in `RaftNode`
(P8-05), reads during membership changes (P8-06). Then the system: the simulator's session and read
dimensions, observed from the party they happen to (P8-07), the two positive controls (P8-08), the
done criteria at scale in both soaks with their known limits (P8-09), and the per-commit cost of the
new work (P8-10).

**Blocking set:** P8-00 to P8-09. P8-10 blocks nothing; it is a measurement the reviewer asked to see
whenever a phase grows the harness.

## Decisions for review

1. **A retry is the same operation.** Today the history adapter records a retry as a new operation
   with the same bytes (P4-07), so a retried `Append` applied twice is linearizable: two operations,
   each with its own effect. That makes "applies once" uncheckable by the checker. Proposed: the
   client carries a session id and a sequence number with every command, a retry keeps both, and the
   adapter records one operation per (session, sequence): invoked at the first attempt, answered by
   whichever attempt was answered, indeterminate if none was. A second application then shows as a
   value appended twice, which no ordering explains. Alternative: keep retries as separate
   operations and count duplicate applications from the log. Rejected: the count would be read
   through the state machine whose deduplication it is checking.
2. **Sessions are registered through the log** (the paper's dissertation, §6.3): a client's first
   command is `Register`, a log entry; its session id is that entry's index, unique across the
   cluster and every restart because the log decides it. Alternative: client-chosen ids (a random
   64-bit value). Simpler, but uniqueness becomes the client's promise, which phase 9's real clients
   would have to keep; registration makes it the log's. Recommended: registration.
3. **The session table keeps one response per session, for its latest sequence number**, with one
   outstanding command per session (the simulated clients are sequential). A command whose sequence
   number is at or below the session's latest is not applied: the cached response is returned if
   the number is equal, and `stale|` (a definite failure) if it is lower. Sessions do not expire in
   this phase: the table grows with the number of sessions, bounded in the simulator by the clients
   and their timeouts. A register row is opened for expiry, promised to phase 9, where clients are
   real and can be abandoned.
4. **Read-only queries by ReadIndex** (dissertation §6.4), not leases (spec §2: leader leases are a
   performance optimisation, out of scope before phase 10). The leader records its commit index,
   waits until an entry of its own term is committed (its no-op), confirms it is still leader with a
   round of heartbeats acknowledged by a quorum of the configuration in effect (both quorums while
   joint), then answers once it has applied up to the recorded index. A follower redirects, as for
   writes. Reads carry no session: they change nothing, so a retry is harmless. Alternative: keep
   reads in the log, as today. They are already linearizable that way, and would make the done
   criterion's second half vacuous: there would be no read the log does not order.
5. **The soaks get sessions and ReadIndex reads, and their known limits are re-measured.** Every
   history changes again (retries merge, reads leave the log), so KL-3's digest stops matching.
   Proposed as at phase 7: the soaks run the system as it is, KL-3 re-measured and re-recorded with
   its curve, or removed if no search stays undecided. Merging retries removes operations from every
   history, so the cost of checking should fall; whether KL-3's key stays undecided is the
   measurement.

## Tasks

### P8-00 — A retry is the same operation (the oracle, first)

- **Task:** Decision 1, in the checker's adapter before any session code: client calls carry (session, sequence) when they have one, and `ClientHistory` records one operation per (session, sequence), invoked at its first attempt and answered by its answered attempt, indeterminate if none was. Known-bad, hand-built: a retried `Append` applied twice (the value appears twice in a later read) must be rejected; the same log with the retry recorded as a separate operation is accepted, which shows why the merge is needed. Operations without a session are recorded as today.
- **Vacuity:** An adapter that merges by bytes, not by (session, sequence), merges two distinct operations that happen to carry the same command; the generated values are unique per operation, so it would pass every test and be wrong for phase 9's clients. Guarded by a hand-built history with two distinct operations of identical bytes in different sessions. Sabotage: S-adapt-5, a retry recorded as a new operation; the double-application history is accepted.
- **Sabotage:** S-adapt-5
- **Verifiable here:** yes — hand-built histories
- **Prediction:** The double-application history is rejected by the checker with no change to the WGL search, because two appends of one value are already unexplainable when they are one operation; the adapter is the whole change. The first adapter written fails the identical-bytes history, because I will key the merge on what the client log makes easy to see, the bytes. **Observable:** which of the two hand-built histories fails first against the first adapter.
- **Outcome:** partly (evidence) — the double-application history was rejected with no change to the WGL search, and its twin with the retry under a new sequence number accepted, as predicted: the adapter is the whole change. But the first adapter did not fail the identical-command history. I keyed the merge on the session and sequence number parsed from the envelope, which is what decision 1 names, not on the bytes; and the session id is in the bytes anyway, so a merge on the whole request could not have merged two sessions either. The risk the vacuity line named is a merge on the command inside the envelope, which nothing tempted.

### P8-01 — The session table in the state machine

- **Task:** Decision 3. `KvStateMachine` holds a session table: per session, the latest sequence number applied and its response. A session command at the latest number returns the cached response without applying; below it returns `stale|`; above it applies and records. The table is part of the canonical snapshot (spec §5 item 6), sorted by session id, and `Restore` replaces it. Unit tests: duplicate, stale, interleaved sessions, snapshot round trip with sessions, canonical bytes. The reviewer's addition: the session count is bounded (`KvStateMachine.MaxSessions`, a `Register` past it refused with `sessions-full|`, a definite failure), and a limit test (§10) registers exactly that many, snapshots and restores, and records the snapshot's size per session and at the bound, for phase 9.
- **Vacuity:** A table that is never consulted for `Put` passes every `Put` test, because a duplicated `Put` is invisible (spec §6); guarded by testing with `Append` and `Cas` first. Sabotages: S-sess-1, the table left out of the snapshot (a retry after a restore applies again); S-sess-2, a duplicate applied when its number equals the latest (an off-by-one in the comparison).
- **Sabotage:** S-sess-1, S-sess-2
- **Verifiable here:** yes — unit tests
- **Prediction:** The snapshot round trip with sessions passes on the first run, and the first red test is a `Cas` retried after the value it compared changed: returning the cached `true` is right and re-evaluating it gives `false`, and I expect the first implementation to re-evaluate, because "return the cached response" is easy to implement for writes and easy to forget for a conditional. **Observable:** the first red test by name.
- **Outcome:** partly (evidence) — the snapshot round trip with sessions passed on the first run, as predicted, but no test went red first: the retried `Cas` returned its first answer because the first implementation kept every session command's reply, conditional or not. The limit test (the reviewer's addition): 1,000,000 sessions (`MaxSessions`), each holding a `Cas` reply, make a snapshot of 28,000,017 bytes, 28.0 bytes per session (an 8-byte id, an 8-byte sequence number, a 4-byte length and the reply: `ok|true` or `ok|false`). It registers, snapshots and restores in 2.2 s, and the 1,000,001st `Register` is refused. A reply's size is bounded only by what the command returns: a `Get` sent inside a session would keep the value it read, but reads carry no session (decision 4), so a session's reply is at most 8 bytes in this workload. For phase 9: 28 MB of snapshot at the bound, about 1.3% of the log file's limit.

### P8-02 — Sessions through the log, and clients that use them

- **Task:** Decision 2. A `Register` command, appended and committed, answers with its index as the session id; session commands carry the id and a sequence number in a canonical envelope the node never interprets beyond refusing malformed ones; `RaftNode` and the state machine as today for everything else. The simulator's clients register once per logical client, retry a timed-out command with the same (session, sequence) to a node drawn afresh, up to a bound, then give up (the operation stays indeterminate) and continue with the next sequence number. `RaftWorkload` and `MembershipWorkload` use sessions.
- **Vacuity:** A client that never retries never exercises deduplication; guarded by the dimension in P8-07 ("a retried command committed twice and applied once") with a floor. A registration that never completes leaves a client with no session, sending session-less commands that are never deduplicated; guarded by counting session-less commands in the generated sample and requiring zero. Sabotage: S-sess-3, the retry sent with a new sequence number (the client's mistake, not the server's): the P8-00 known-bad shape appears in generated executions and linearizability rejects it.
- **Sabotage:** S-sess-3
- **Verifiable here:** yes — constructions and the simulator
- **Prediction:** Merging retries lowers the indeterminate operations per history by more than half, because today every timed-out operation is indeterminate and most are retried and answered, and the cost of checking falls with them: the soak sample's most states explored, 866,019 at phase 7, falls below 200,000. **Observable:** the sample's indeterminate count and its hardest history's states, before and after.
- **Outcome:** pending

### P8-03 — Sessions across compaction

- **Task:** Spec §5 item 6's sentence as a construction: a client's `Append` commits, its response is lost, the cluster compacts past it, a follower installs the snapshot and becomes leader, and the client retries: the retry returns the cached response and applies nothing. The same with a restart from a snapshot. Agreement (P7-08) still holds, the replay now running a session-aware state machine.
- **Vacuity:** A construction whose retry reaches the original leader, whose session table never left memory, says nothing about the snapshot; guarded by asserting the retry is answered by a node that restored a snapshot covering the original entry. Sabotages: S-sess-4, a restore that keeps the session table it had and merges the snapshot's into it (a node that installs a snapshot answers from a table older than the snapshot); S-sess-1 again (the table out of the snapshot), caught by this construction as well as P8-01's unit test.
- **Sabotage:** S-sess-4; shared: S-sess-1
- **Verifiable here:** yes — constructions
- **Prediction:** The construction passes once P8-01 is in, and the replay agrees with the node at every restore, because the replay and the node run the same state machine: this is the case the replay cannot judge (the carried-forward note), and only the client history (P8-00) catches S-sess-1 in generated executions. **Observable:** S-sess-1 against the agreement check alone: it survives.
- **Outcome:** pending

### P8-04 — Stale reads served the new way, rejected by the checker

- **Task:** Before ReadIndex exists, the histories it could produce if wrong: a deposed leader answering a read after a new leader committed a write (the read returns the old value, after the write completed); a leader answering before its term's no-op committed (missing a write committed by its predecessor); a read answered during a joint configuration by a quorum of the old configuration only. Each as a hand-built history the checker must reject, and its correct counterpart it must accept.
- **Vacuity:** Known-bad histories written by the same hand that will write the node share its model of what ReadIndex prevents (spec §6's reason for the brute-force oracle); guarded by checking each against the brute-force oracle as well as WGL. Sabotage: S-wgl-13, the checker treating a read with no write between it and an earlier read of the same key as unconstrained by real time.
- **Sabotage:** S-wgl-13
- **Verifiable here:** yes — hand-built histories and the oracle
- **Prediction:** All three are rejected by today's checker unchanged, and the oracle agrees, because a stale read is a stale read whatever path served it; the task produces no checker change, only the evidence that none is needed. **Observable:** the three verdicts before any change.
- **Outcome:** pending

### P8-05 — ReadIndex in `RaftNode`

- **Task:** Decision 4. A `Get` is answered without an entry: the leader records its commit index as the read's index, waits for an entry of its term to commit, sends a heartbeat round tagged with the read, counts acknowledgements from a quorum of the configuration in effect, then answers once applied up to the read's index. Followers and candidates redirect. Constructions: a partitioned old leader cannot answer; a new leader's read waits for its no-op; a read with a slow follower in the quorum waits for it.
- **Vacuity:** A construction in which no other leader exists answers correctly with or without the heartbeat round; guarded by the partitioned-old-leader construction, where a second leader has committed a write the first has not seen. Sabotages: S-read-1, the heartbeat round skipped (the old leader answers a stale value); S-read-2, the wait for the term's own commit skipped (a new leader answers without its predecessor's last write).
- **Sabotage:** S-read-1, S-read-2
- **Verifiable here:** yes — constructions and the simulator
- **Prediction:** The first construction to fail is the new leader's read before its no-op: the commit index a new leader starts with can be behind entries its predecessor committed, and I expect my first implementation to take the commit index as the read index without waiting for the term's entry, since the heartbeat round is the part every description leads with. **Observable:** the first red construction by name.
- **Outcome:** pending

### P8-06 — Reads during membership changes

- **Task:** The heartbeat round of a read counts a quorum of every configuration in effect (both while joint), as commitment does (P6-05). Constructions: a read during `C_old,new` with only the new configuration's majority reachable must wait; a leader removed by `C_new` stops answering reads once it steps down.
- **Vacuity:** A construction whose two majorities overlap answers whichever quorum is counted; guarded by the replacement shape of P6-05 (`{n1,n2,n3}` to `{n1,n4,n5}`), where they do not. Sabotage: S-read-3, the read's quorum counted in the new configuration alone.
- **Sabotage:** S-read-3
- **Verifiable here:** yes — constructions
- **Prediction:** Passes on the first run, because the read's quorum will reuse `ConfigurationAt(...).IsQuorum`, the call commitment uses, and the bug phase 6 found there (a quorum of one configuration) cannot recur through a shared call. **Observable:** the construction's verdict before any fix.
- **Outcome:** pending

### P8-07 — The simulator's session and read dimensions, from the party they happen to

- **Task:** Dimensions, each read where the effect happens: a retried command committed twice and applied once (two committed entries with one (session, sequence), observed in the log, the second answered from the cache); a retry answered from a session table restored from a snapshot; a stale sequence number refused; a read answered while another node led a later term (observed from the election history at the read's answer time); a read that waited for a quorum's acknowledgements past a heartbeat interval. Each with a floor in the samples and the soaks.
- **Vacuity:** A "deduplicated" dimension counted from the client's retry measures the client, not the server (phase 6's lesson); guarded by reading it from the committed log and the state machine's answer. Sabotage: S-cov-14, deduplication counted when a retry is sent: in a sample where every retry's original was lost before commit, the dimension stays up while nothing was deduplicated.
- **Sabotage:** S-cov-14
- **Verifiable here:** yes — the simulator
- **Prediction:** "A retry answered from a restored session table" is the rarest and below the floor in the samples: it needs a commit, a lost response, a compaction past the entry, and the retry landing on a node that restored, in that order, within one client timeout. **Observable:** each dimension's count over the 300-execution samples.
- **Outcome:** pending

### P8-08 — The positive controls: no deduplication, and reads without a quorum

- **Task:** Two variants that must turn the checker red, in a construction and in the sample: a state machine that applies every session command (no deduplication), and a leader that answers reads from its own state without the heartbeat round. The real node must not. Each sample test stops at its floor (phase 7's acceptance); the full rate is measured once and recorded.
- **Vacuity:** A construction where the retried command's original never committed shows no duplicate; guarded by asserting two committed copies before reading the verdict. Sabotage: S-ctl-1, the controls' options on in the real node.
- **Sabotage:** S-ctl-1
- **Verifiable here:** yes — constructions and the simulator
- **Prediction:** The deduplication control is caught by linearizability in more executions than the stale-read control, because every committed retry applied twice is visible to the next read of its key, while a stale read needs a deposed leader still answering, which needs a partition that isolates a leader while clients still reach it. **Observable:** each control's caught count over the full 300 executions.
- **Outcome:** pending

### P8-09 — The soaks with sessions and reads, and their known limits

- **Task:** Decision 5. Both soaks with sessions and ReadIndex reads; every invariant, agreement, linearizability with retries merged, the P8-07 dimensions with their floors. KL-3 re-measured: re-recorded with its curve if still undecided, removed otherwise; any new undecided search measured and put to the reviewer.
- **Vacuity:** A soak whose clients never retry and whose reads never wait checks neither criterion; guarded by the P8-07 floors. Sabotage: S-soak-11, reads routed back through the log in the baseline soak: the read dimensions go below their floors.
- **Sabotage:** S-soak-11
- **Verifiable here:** partial — the soaks run locally; the CI jobs only in CI
- **Prediction:** Deterministic quantities only: the baseline soak's checking time falls by more than a third (fewer indeterminate operations per history after merging), and the membership soak's seed 8741 stays undecided at k5 with another history, because this is the third workload change it has survived (P6-11's generator, phase 7's compaction) and whatever makes it hard is not timing. **Observable:** both soaks' checking time and undecided list.
- **Outcome:** pending

### P8-10 — The new work's cost in CI

- **Task:** Measure what phase 8 adds to CI on GitHub: each harness shard's time against phase 7's certifying run, the soaks' times, and the per-commit matrix's longest job, with the runner's ratio to the local machine applied to every local number quoted (phase 7's acceptance rule).
- **Vacuity:** A measurement taken on a run with cancelled or retried jobs compares different work; guarded by using only runs whose every job completed once.
- **Sabotage:** ; manual: the comparison is made once against a run with a re-run job, to see its numbers differ and be refused
- **Verifiable here:** partial — CI's numbers only in CI
- **Prediction:** No harness shard passes 75% of its ceiling on GitHub, because the new entries go to every shard round-robin and no new sample test runs to the end. **Observable:** the slowest shard's harness clock in the certifying run.
- **Outcome:** pending

## Sabotage ids

New series: S-sess (P8-01, P8-02, P8-03), S-read (P8-05, P8-06), S-ctl (P8-08). S-adapt-5 follows
S-adapt-4, S-wgl-13 follows S-wgl-12, S-cov-14 follows S-cov-13, S-soak-11 follows S-soak-10. Each
id's `sabotage/<id>/` entry lands in the same commit as the check it proves and is run on that
commit (`gates sabotage --only`) before it is pushed; the shards run in CI.

## Register rows opened here

Session expiry (decision 3): sessions never expire in phase 8, and a real client that is abandoned
leaves its session in the table for good. Promised to phase 9.
