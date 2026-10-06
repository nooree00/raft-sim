# Phase 8 — report

**Status: for review.**

Client sessions and read-only queries (spec §11 phase 8, §5 item 6, paper §8): P8-00 to P8-10, as
approved, in the breakdown's order. One task gained a sabotage the breakdown did not name (S-read-4,
P8-05), and one commit gained a check after the fact (S-lin-7, P8-02, below). No recorded known
limit remains: **KL-3 is removed**, and both soaks' files are empty.

This report certifies the commit that contains it. `gates reports` checks that commit's CI run on
the next push.

## The result of the phase

### A retried `Append` applies once, and the checker accepts every history with reads

The done criterion (spec §11), with the oracle outside the state machine (decision 1; findings,
pattern 1): a retry is recorded as the operation it is, one per (session, sequence number), so an
`Append` applied twice shows as a value appended twice, which no ordering explains.

- **The oracle first (P8-00):** the history of a retried `Append` applied twice is rejected by the
  WGL checker and the brute-force oracle with no change to the search; its twin, the retry under a
  new sequence number (two operations), is accepted.
- **The state machine (P8-01, P8-02):** sessions registered through the log (decision 2: the
  session id is the `Register` entry's index), one cached reply per session for its latest number,
  `stale|` below it, `unknown-session|` for an id never registered.
- **Across compaction (P8-03):** the session table is in the snapshot; a retry reaching a node whose
  table came from a snapshot, by an install and by a restart, is answered from it.
- **Reads (P8-04 to P8-06):** ReadIndex. A `Get` is answered without a log entry, once an entry of
  the leader's term has committed, a heartbeat round sent after the read arrived is acknowledged by a
  quorum of every configuration in effect, and the read's index is applied. The three stale reads
  ReadIndex could produce if wrong were shown rejected by the unchanged checker before ReadIndex
  existed (P8-04).
- **The baseline soak** (10,000 executions, sessions and ReadIndex, compaction on): no invariant
  violated; 84,528 node states agreeing with the replay; 10,000 histories linearizable, **none
  undecided**. A retry committed twice and answered from the cache in 1,860 executions, from a table
  a snapshot carried in 174, a stale number refused in 5,748, a read waiting past a heartbeat
  interval in 956, a read released after another node's later election in 14.
- **The membership soak** (10,000 executions of five nodes): no invariant violated; 107,200 states
  agreeing; 10,000 linearizable, none undecided; the same dimensions 595, 32, 1,638, 430 and 7.

The positive controls (P8-08): a state machine applying every session command is caught by
linearizability in 37 of 300 generated executions (375 of 3,000), a leader answering reads from its
own state in 1 of 300 (7 of 3,000); each is red in its construction where the real node is green.

### The session table's size at its limit (the reviewer's addition to decision 3)

`MaxSessions` = 1,000,000; the 1,000,001st `Register` is refused (`sessions-full|`). At the limit,
each session holding a `Cas` reply, the snapshot is **28,000,017 bytes, 28.0 bytes per session**: an
8-byte id, an 8-byte sequence number, a 4-byte length and the reply (`ok|true` or `ok|false`). It
registers, snapshots and restores in 2.2 s. A reply's size is bounded only by what the command
returns; reads carry no session (decision 4), so in this workload a session's reply is at most 8
bytes. For phase 9: 28 MB of snapshot at the bound, about 1.3% of the log file's limit, and the
expiry row (below) is what keeps a real deployment away from it.

### What the phase found

**The undecided histories were made by how retries were recorded.** Merging retries took the
baseline soak's checking time from 399 s to 5 s and the hardest decided history from 21,232,723
states to 2,477; KL-3, undecided with three histories of seed 8741 since phase 6, is decided in at
most 103 states. A retry the client gave up on had been an indeterminate operation open to the end of
the history; one operation per (session, sequence) removes it. I predicted KL-3 would survive
(P8-09). It did not, and phase 11's structural-measure row has no undecided key from either soak to
measure against. This is the phase's finding.

**A sabotage went vacuous through a change upstream of its patch.** S-lin-4 answers a write at
append; its patch keyed on the command's first byte, and from P8-02 that answered registrations
too, so no client held a session, no write was sent, and its target accepted forty histories of
reads alone. Found before the push by running the 94 sabotages whose patches touch the files the
phase changed, an extra local check. The patch now answers session writes (caught at seed 28), the
target asserts that writes completed, and S-lin-7 is the patch as it had become; both were checked
at the commit that broke it, P8-02, which I amended before the push.

**A server path the workload could not reach.** The stale-sequence dimension was 0 in both samples:
sequential clients never send a lower number. The session clients now send a late duplicate every
16th number when the two before it were answered.

**ReadIndex needs a wire field.** `AppendEntries` and its response carry a round number, echoed;
without it the answer to a heartbeat sent just before a read confirms the read (S-read-4).

## Done criteria

| Criterion (spec §11 phase 8, and the breakdown's additions) | Where it is shown |
|---|---|
| A retried `Append` applies once | P8-00 (the history rejected), P8-01 (the table), P8-02 (the clients), P8-07 (a retry committed twice and applied once, from the log, in both soaks), P8-08 (the control without deduplication caught) |
| The checker accepts every history that includes reads | Both soaks: 20,000 histories with reads served by ReadIndex, all linearizable, none undecided |
| The session table is part of the snapshot (spec §5 item 6) | P8-01 (the round trip), P8-03 (a retry after an install and after a restart), P8-07 (174 and 32 soak executions) |
| Stale reads rejected when served the new way | P8-04 (three histories, checker and oracle), P8-05 and P8-06 (constructions), P8-08 (the read control) |
| The session count's limit measured | P8-01: 28,000,017 bytes at 1,000,000 sessions |
| Both soaks with sessions and reads, the known limits re-measured | P8-09: KL-3 removed |

## Predictions

**Evidence 10, forcing 1.** Right 5, partly 4, wrong 1 on evidence. Each outcome is in the breakdown.

| Task | Outcome | In one line |
|---|---|---|
| P8-00 | partly | the double application rejected with no search change; the first adapter never faced the identical-command history the vacuity line feared |
| P8-01 | partly | the round trip passed first as predicted, but nothing went red first: the first implementation kept every reply |
| P8-02 | right | indeterminate operations fell by 51%, the hardest sample history from 866,019 states to 410 |
| P8-03 | wrong | the replay was not blind to a lost session table: it never snapshots, so a snapshot defect is outside what the two share |
| P8-04 | right | all three stale reads rejected by the unchanged checker |
| P8-05 | wrong (forcing) | no construction went red: the wait the prediction named was in the first implementation |
| P8-06 | right | the joint read waited for both majorities on its first run |
| P8-07 | partly | the restored-table retry below the floor in the membership sample only, and not the rarest; the stale path unreachable |
| P8-08 | right | the deduplication control caught in 37 of 300, the read control in 1 |
| P8-09 | partly | checking time fell 99%; KL-3 did not survive |
| P8-10 | right | no harness shard past 75% of its ceiling on GitHub: the slowest at 652 s of 900 |

## What exists

| Task | Delivered | Sabotages |
|---|---|---|
| P8-00 | One operation per (session, sequence) in the client history | S-adapt-5 |
| P8-01 | The session table in `KvStateMachine`: registration, cached reply, `stale|`, `unknown-session|`, `MaxSessions`; in the snapshot | S-sess-1, S-sess-2 |
| P8-02 | `SessionWorkload`; `IClientWorkload.Replied`; every soak's clients in sessions | S-sess-3, S-lin-7 |
| P8-03 | Sessions across compaction, by install and by restart | S-sess-4 |
| P8-04 | Stale reads served the new way, rejected by the checker and the oracle | S-wgl-13 |
| P8-05 | ReadIndex in `RaftNode`; the round on the wire; `IStateMachine.IsQuery`/`Query` | S-read-1, S-read-2, S-read-4 |
| P8-06 | Reads during joint consensus | S-read-3 |
| P8-07 | Session and read dimensions, from the log, the applying node and the simulator's trace; late duplicates | S-cov-14 |
| P8-08 | The controls (`KvStateMachine(deduplicate: false)`, `RaftOptions.ReadsWithoutQuorum`) | S-ctl-1 |
| P8-09 | Both soaks with sessions and reads; KL-3 removed; budget seeds re-picked | S-soak-11 |

**Test projects** (at the start of phase 8 in brackets): Architecture 46 (46), Gates 119 (119),
Checker 170 (167), Simulation 110 (110), Core 842 (815), Scale 22 (18), Budget 3 (3), Membership 3
(3).

## Deviations and choices for the reviewer

- **Floor declarations.** A read released after another node's later election: 14 of the baseline
  soak's 10,000, 7 of the membership soak's, 1 and 0 of the samples; declared below the soak floor
  and rare in the samples. A correct ReadIndex produces it only between a quorum's last
  acknowledgement and the old leader learning of the new term. A retry answered from a restored
  table: 32 of the membership soak's 10,000 (0.32%; 174 in the baseline's, over the floor), declared
  below the soak floor, and 0 of the membership sample. Both are exercised directly by constructions.
- **The read control runs named executions, not a sample.** Its natural rate (0.23%) is below the
  floor in 300 executions; a sample to its third catch would stop at seed 1,418 and, under a
  sabotage that slows executions, run to its cap, which is phase 7's shard 10 again. It runs seeds
  33, 1,367 and 1,418, each red with the control and green with the real node.
- **The session clients send late duplicates** (P8-07): every 16th sequence number, when the two
  before it were answered. Without them the state machine's `stale|` path is unreachable by the
  generator.
- **`command-retried-and-duplicated` is retired** (P8-07): with every write in a session it hit
  exactly the executions of `retry-committed-twice-applied-once`, and the soak fails one measurement
  under two names.
- **The wire format changed** (P8-05): `AppendEntries` and its response carry an 8-byte round, so an
  encoded `AppendEntries`' fixed part is 49 bytes, not 41, and `LargestCommandFor` follows it.
- **A peer being sent a snapshot acknowledges no round** until its install ends: a read can wait on
  it when the other peers alone are not a quorum. A liveness cost, not a safety one.
- **`ReadsWithoutQuorum` and `deduplicate: false` are production options that exist for the
  positive controls**, as `CompactPastCommit` and `DisruptionRule` do.
- **S-lin-4's fix rewrote unpushed history**: the P8-02 commit was amended (with the later commits
  rebased onto it) before anything was pushed, so that no pushed commit carries a surviving sabotage.

## The new work's cost in CI (P8-10)

Measured on run 37393582257, the push of P8-00 to P8-09 (head `6f5001d`): 123 jobs, each completed
once, all green; compared with phase 7's certifying run 37299866345.

| | Phase 8 | Phase 7 |
|---|---|---|
| Harness shards | 11, steps 407 to 652 s (slowest 72% of 900) | 10, 329 to 729 s (81%) |
| Baseline soak step | 376 s | 603 s |
| Membership soak step | 607 s | 1,261 s (KL-3's search) |
| Longest per-commit job | 802 s | 1,053 s |
| Local run before the push | 650 s (12 to 16 minutes at GitHub's ratio) | 1,036 s |

The prediction (no shard past 75%) held, but its reason held only because of a change made during the
phase: the read control's first sample ran toward its end and was replaced by named executions
before the push. A re-run run (37146478876) was refused as a comparison: the same commit's soak took
1,327 s in one attempt and 892 s in the other.

## Register

Opened: session expiry, promised to phase 9 (decision 3). Sessions never expire; an abandoned real
client leaves its session in the table for good, at 28 bytes plus its last reply.

## Findings added this phase

In `docs/findings.md` under Phase 8: the undecided histories were made by how retries were recorded
(the phase's finding); a server path the workload could not reach; a sabotage made vacuous by a
change upstream of its patch; a positive control too rare for a floor in the sample; ReadIndex needs
a wire field.

## Still the person's

- `soak-membership` beside `soak` in branch protection; deleting `prerewrite-b96fc4b`, the probe
  branches and the sabotage branch; the cold walk of the README; P0.

## Commits never verified in CI

None. Every job of run 37393582257 passed, the per-commit matrix included: each of this phase's
pushed commits has a passing verdict for every (commit, shard).
