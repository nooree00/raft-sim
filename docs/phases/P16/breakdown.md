# Phase 16 — task breakdown

The leader's hand-offs, measured apart; the tail rule restated after them; the harness keeps each
entry's output (spec §11 phase 16, added at phase 15's acceptance; its done criterion is P16-00's to
write, at this breakdown's approval). Format as in `docs/phases/P15/breakdown.md`, parsed by `gates
breakdown`; outcomes will say `(evidence)` or `(forcing)`.

**Draft, for review.** Nothing in it is built before approval.

**Out of scope, stated first: the replication pipeline.** Past the capacity (9,375 writes a second) the
pipeline's one append in flight per follower is the candidate limit, and widening it would reverse
P11-02, the fix for the 200-times resend defect, for throughput past an operating point pinned at a
third of the capacity (phase 15's decision 3). Its register row stays open and unhomed. No task here
changes the pipeline, and a reading that points at it is reported, not acted on.

**Where phase 15 left the tail.** With the remedy (outputs no persist precedes released at a batch's
start, client requests handed over last), the 99th percentile at 3,125 writes a second is 8.8 ms
untraced against the criterion's 5,051 µs (m:p15-05-ab-fix-3125-1 to -5). In the four traced runs within
1.5 times the untraced (m:p15-05-s10-r1-local-3125-1 to -r4), the tail writes' excess over the median
write sits mostly in two hand-offs: **client-to-leader 0.32** (0.22 to 0.41; its median 36 µs, its 99th
percentile 2.0 ms) and **leader-to-send 0.17** (0.09 to 0.19; median 64 µs, 99th 3.3 ms); then the
leader's persist 0.13, leader-queue 0.11, the connection wait 0.08, the follower's persist 0.07; the
generator's lateness 0.03. P14-04's rule had no branch for an excess in the hosts' hand-offs and was
reported as wrong (P15-04). Each of the two segments hides a hand-off between threads:
- *client-to-leader* runs from the generator's stamp before its write to the leader's reader returning
  the line. Inside it are the kernel's delivery on loopback and the wake of the leader's reader thread
  (one blocking thread per client connection, 64 of them per host, three hosts on four processors);
  no stamp separates them;
- *leader-to-send* runs from the entry's durability to the frame handed to the peer's socket. P15-03
  split it at the node's emission (before it, the pipeline's wait; after it, the hold). After the hold
  the frame is queued for the peer's sender, an asynchronous task on the thread pool, and the `Sent`
  stamp is taken when that task writes it; no stamp marks the release, so the hold and the sender's
  wake are one number.

A rule that governs a reading before its dominant terms are understood is how P14-04's went wrong. So
the hand-offs are measured apart first, and the corrected rule is written after that measurement and
before the reading it governs.

**Done when** (proposed for spec §11 by P16-00):
- **the hand-offs in the tail are measured apart:** client-to-leader into the kernel's delivery and the
  leader's reader after it, leader-to-send into the time before emission, the hold, and the sender's
  hand-off after release; each part guarded by a planted delay that must appear in it alone;
- **the corrected tail rule is written after that measurement and before the reading it governs,**
  each branch's numeric test written apart from its reading, a branch for no branch fitting, and each
  branch's reading checked against the segments it names before the branch is reported;
- **the tail at 3,125 writes a second is read by it** from traced runs each within 1.5 times the
  untraced 99th percentile at the same rate (per run, phase 15's decision 4); no remedy for the tail is
  built in this phase, the reading naming the next subject;
- **the sabotage harness keeps each entry's output** for any outcome it did not expect, so an
  unclassified result can be read when it happens;
- **S-ran-2's patch carries no count,** and no sabotage patch or control names the count file;
- every bench control's margins measured again after the host's changes; every row promised to phase
  16 closed; the replication pipeline untouched.

Carried forward from phase 15's acceptance:
- **a rule fixed in advance can be wrong in its branches as well as its threshold;** the tell is an
  answer that satisfies a branch while contradicting its description, and then the rule is reported
  as wrong, with what it should have said;
- **an instrument's cost model is itself a measurement:** the instrument is measured at its extremes
  before its parameters are tuned;
- **the measurement and the verification compete for the machine:** no local CI run while a
  measurement runs;
- **the median criterion is out of reach with one sync at a time** (spec §8); it is reported against,
  not chased;
- **an audit describes the commit it measured** (P14-06's observer); **a check that cannot fail is
  rejected; every input a gate reads is registered.**

## Ordering

The spec's row first (P16-00). Then the two mechanical tasks, which need no measurement and touch no
host code: S-ran-2 anchored away from the counts (P16-05) and the harness's per-entry output (P16-06),
so the rest of the phase runs with both. Then the instrument (P16-01), because the parts are read
through it; then the hand-offs measured apart (P16-02); then the rule, written from what P16-02 found
(P16-03); then the reading on fresh runs (P16-04). Then the margin audit at the last host change
(P16-07) and the cost in CI (P16-08).

## Decisions for review

1. **Phase 16's done criterion** (P16-00), as above.
2. **The kernel's receive time for each client line** (P16-01). The leader's client sockets, in a traced
   bench run only, ask for the kernel's receive timestamp (`SO_TIMESTAMPNS`, which Linux reports for TCP
   since 4.13), and the reader takes its lines through `recvmsg` so the timestamp arrives with the
   bytes; each line is stamped with the kernel time of the read that completed it, converted from the
   kernel's real-time clock to the trace's monotonic one by an offset taken at the trace's start and
   checked at its end. client-to-leader then splits into *delivery* (the generator's stamp to the
   kernel's arrival) and *reader* (arrival to the line read). **Not chosen:** a wake probe (a thread
   timing its own wakes beside the readers), which correlates with the tail rather than measuring its
   writes; scheduler tracing (perf, eBPF), which needs privileges the containers do not have.
   **Contingency, stated now:** if the containers' kernel does not deliver the timestamp for TCP, the
   task reports it and the reader's part is read through the wake probe, named as correlation.
3. **A release stamp for sends** (P16-01). The executor's hand-off of a frame to the peer's queue is
   stamped (`Released`, the frame's array, joined to its `Sent` stamp by reference as reads are to their
   takes). leader-to-send then splits into *before emission* (P15-03's), *hold* (emission to release)
   and *sender* (release to the socket write). Responses need no stamp: the loop writes them itself,
   and `Responded` is their release.
4. **The rule is written after P16-02 and before P16-04's runs,** committed as an amendment to this
   breakdown, its form fixed now: each branch a numeric test over the measured parts and, apart from
   it, a reading; a branch for no branch fitting; each reading checked against the parts it names
   before its branch is reported. It is not sent for review before the reading (the phase would stop
   between two measurements of one machine); its commit is an ancestor of every P16-04 record's, and
   the report shows that. **Not chosen:** writing the rule now, before the parts are known, which is
   P14-04's mistake again.
5. **No remedy for the tail in this phase.** The reading names the next subject, as P14-04's did. The
   tail criterion (5,051 µs) is reported against; the median's is a stated limit (§8).
6. **The harness's output, kept where it is read** (P16-06): for any entry whose outcome is not the one
   it expects (survived, wrong-reason, target-not-run, build error, a control red), the harness prints
   the entry's test run's last 80 lines and its results summary into the job's log, under the entry's
   id; the local stage log keeps the same. **Not chosen:** every entry's output as a workflow artifact
   (a new action to pin, and the log already keeps it), or every entry's output always (357 entries of
   noise around the one that matters).
7. **S-ran-2's count edit moves into its command** (P16-05): the patch drops the test file from its
   project only; the command lowers that project's baseline line by the file's test count, computed
   from the file, and runs the count check; a `baseline:` line keeps the unpatched run plain. Tried by
   hand at `8d1ae90`: the count check fails with the entry's message, the baseline line read 849. And
   the loader refuses any patch or control naming `ci/test-baseline.txt`, so no entry can anchor there
   again.

## Tasks

### P16-00 — The spec's phase-16 row

- **Task:** Spec §11's phase-16 row gets its done criterion (decision 1). The register's rows promised to phase 16 cite the row.
- **Vacuity:** A done criterion that names a remedy rather than a measurement and a rule would admit building before reading; guarded by the criterion naming the parts, the rule's order and its form, and by `gates register`, which reads the phase row.
- **Sabotage:** ; manual: the diff checked against the decisions (§11 only)
- **Verifiable here:** partial — the diff and the register gate here; its approval the reviewer's
- **Prediction:** The amendment changes §11's phase-16 row and no other section; `docs/design/node-interface.md` is unchanged through the phase, the new stamps being bench-mode observation only. **Observable:** the diff, and the phase's diff of node-interface.md.
- **Outcome:** pending

### P16-01 — The hand-offs' instrument: the kernel's arrival and the send's release

- **Task:** Decisions 2 and 3. In a traced bench run, the leader's client sockets carry the kernel's receive timestamp and its reader stamps each line's arrival (`ClientArrived`, the kernel time, on the trace's clock); the executor's hand-off of a frame to the peer's queue is stamped (`Released`). The decomposition splits client-to-leader into delivery and reader, and leader-to-send into before emission, hold and sender; the records carry each part's median, mean and 99th percentile and its tail share. The sampled trace keeps both stamps as it keeps the stamps they join.
- **Vacuity:** An arrival stamped when the reader returns, not when the kernel received the bytes, makes the reader's part zero whatever it holds; guarded by a test on a socket pair whose reader waits a planted 20 ms before reading: the reader's part holds the 20 ms and delivery under 1 ms (S-hand-1). A release stamped in the sender task shows no sender hand-off; guarded by the bench's hand-off control with a planted delay in the sender's dequeue, which must appear in the sender's part and not in the hold (S-hand-2). A clock offset gone wrong puts arrivals outside their writes; guarded by the decomposition's causality count, which a test plants (S-hand-3).
- **Sabotage:** S-hand-1, S-hand-2, S-hand-3
- **Verifiable here:** yes — the host suites, the sabotages and the traced runs on this machine
- **Prediction:** The containers' kernel delivers the receive timestamp for TCP; at 3,125 every joined write has its arrival and every append its release, with no causality violation; the traced runs stay within 1.5 times the untraced 99th percentile in at least three runs of five (P15-03's set was 1.40 on aggregate), the two stamps adding under 5% to the traced median. **Observable:** the records' joined counts, violations and percentiles.
- **Outcome:** pending

### P16-02 — The hand-offs measured apart

- **Task:** At the head after P16-01 (the remedy's host), generator separate, traced keeping one write in ten and untraced, five runs each at 1,250 and 3,125 writes a second, interleaved; for the traced runs each within 1.5 times the untraced at its rate, each part's share of the tail writes' excess over the median write, and its median and 99th percentile; the growth from 1,250 to 3,125 by part. A description, read by no rule.
- **Vacuity:** Parts that sum to more or less than their segment describe another quantity; guarded by each write's parts summing to its segment (the decomposition's sum check), its gap reported.
- **Sabotage:** ; manual: each part's sum against its segment, each run's 99th percentile against the threshold
- **Verifiable here:** partial — measured here; its checks are by hand against the records
- **Prediction:** Of client-to-leader's share of the tail's excess, the reader's part holds at least three quarters and delivery under 0.05 of the excess, delivery's median about 20 µs and the reader's 99th percentile over a millisecond: the reader's wake, not the network, is client-to-leader's tail. Of leader-to-send's share, the sender's part holds 0.05 to 0.10 of the excess, the time before emission 0.04 to 0.10 and the hold under 0.03. **Observable:** the records' part shares and percentiles.
- **Outcome:** pending

### P16-03 — The corrected tail rule

- **Task:** Decision 4. Written from P16-02's description and committed as an amendment to this breakdown before P16-04's first run: branches whose numeric tests range over the measured parts (the persists; the loop's queues; the hosts' thread hand-offs, reader and sender; the generator's lateness; the rest), each with its reading written apart, a branch for no branch fitting, and, for each reading, the part it must be checked against before its branch is reported. What each branch implies for the next subject is written with it.
- **Vacuity:** A rule written after the reading it governs fits that reading by construction; guarded by its commit being an ancestor of every P16-04 record's commit, checked and reported.
- **Sabotage:** ; manual: the rule's commit against P16-04's records' commits
- **Verifiable here:** partial — git says which commit came first, checked by hand
- **Prediction:** The rule is written in one commit before P16-04 starts and is not amended during it. **Observable:** the breakdown's history and the records' commits.
- **Outcome:** pending

### P16-04 — The tail read by the corrected rule

- **Task:** Fresh runs after P16-03's commit: generator separate, five traced (one write in ten) and five untraced at 3,125 writes a second, interleaved; the rule applied to the traced runs each within 1.5 times the untraced, each branch's reading checked against its part before it is reported. No remedy built; the reading names the next subject.
- **Vacuity:** A reading taken from runs beyond the threshold reads the trace again; guarded by the per-run threshold, checked first, from the records.
- **Sabotage:** ; manual: the threshold against each traced run, the branch's reading against its part
- **Verifiable here:** partial — measured here; whether the rule is the right one is the reviewer's
- **Prediction:** The rule's numbers select the hosts' thread hand-offs, reader and sender together holding more than half of the tail's excess, and its reading holds against the parts: the reader's wake on four processors shared by three hosts, a loop 0.96 busy and about 200 threads. The persists hold 0.15 to 0.25, the generator's lateness under 0.05. **Observable:** the records and the rule.
- **Outcome:** pending

### P16-05 — S-ran-2 anchored away from the counts

- **Task:** Decision 7. S-ran-2's patch keeps only its project-file change; its command lowers the project's baseline line by the excluded file's test count, computed from the file, and runs the count check; a `baseline:` line keeps the unpatched run plain. The sabotage loader refuses an entry whose patch or control names `ci/test-baseline.txt`.
- **Vacuity:** A rule against the count file that refuses nothing guards nothing; guarded by a test that an entry whose patch names it is refused (S-patch-2). An entry moved to a command that no longer breaks the count check passes as caught for another reason; guarded by its message, which names the file's tests as written and not executed.
- **Sabotage:** S-patch-2
- **Verifiable here:** yes — the harness runs S-ran-2 here and in CI
- **Prediction:** S-ran-2 is caught with its message; after this task no commit of the phase touches `sabotage/S-ran-2/`, although the Gates, Host and Host.Slow counts each change at least once in the phase; no other entry names the count file. **Observable:** the harness's runs and the phase's history of `sabotage/S-ran-2/`.
- **Outcome:** pending

### P16-06 — The harness keeps each entry's output

- **Task:** Decision 6. For any entry whose outcome is not the one it expects, the harness prints the entry's test run's last 80 lines and its results summary into the log under the entry's id, in CI and in the local stages alike.
- **Vacuity:** A path taken only on rare outcomes is untested until one happens; guarded by a test that drives the outcome classification with a run whose results omit the target and requires the output's tail in the report (S-out-1). Output printed for every entry buries the one that matters; guarded by the same test requiring none for an expected outcome.
- **Sabotage:** S-out-1
- **Verifiable here:** yes — the harness's tests here
- **Prediction:** The change touches the harness's outcome reporting and its tests only; no head shard's harness step grows by more than 5 s; if an unexpected outcome happens in the phase, its output is in the log, and none is expected. **Observable:** the diff, the shards' times, the logs.
- **Outcome:** pending

### P16-07 — The margin audit after the host's changes

- **Task:** `scripts/margins.sh` at the phase's last host change, three modes, five runs a side, with a row for the hand-off control's sender part (S-hand-2's); the table becomes this phase's, read by P14-06's observer at the report.
- **Vacuity:** An audit at a commit before the host's last change describes another host; guarded by P14-06's observer.
- **Sabotage:** ; manual: `gates margins` over the table, and `gates margins --currency` at the report
- **Verifiable here:** partial — the audit runs here; GitHub's runners give other margins, seen only when a control fails there
- **Prediction:** Every row keeps the audit's 1.5 on both sides, the new sender row included; no other row moves by more than a tenth of its bound. **Observable:** the table and the gates.
- **Outcome:** pending

### P16-08 — The phase's cost in CI

- **Task:** The harness shards, the soaks and the per-commit matrix on the phase's first-attempt runs against phase 15's, the entries added, whether every job was given a runner, and the never-verified commits tabled.
- **Vacuity:** A run with a job that never ran compares less work; guarded by counting only jobs with steps and stating any that never ran.
- **Sabotage:** ; manual: the comparison checked against each run's job list
- **Verifiable here:** no — CI's numbers only in CI
- **Prediction:** The phase adds five entries (S-hand-1 to S-hand-3, S-patch-2, S-out-1) and retires none; no head shard passes 400 s against phase 15's slowest of 374; the soaks' effect counts are phase 15's line for line; every job is given a runner. **Observable:** the shards' slowest step, the manifest, the soaks' reports, the job list.
- **Outcome:** pending

## Sabotage ids

New series: S-hand (P16-01), S-out (P16-06). Each id's `sabotage/<id>/` entry lands in the same commit
as the check it proves and is run on that commit before it is pushed.

- **S-hand-1:** a client line's arrival stamped when the reader returns it, not with the kernel's
  receive time.
- **S-hand-2:** a send's release stamped by the peer's sender task, not by the executor's hand-off.
- **S-hand-3:** the kernel's clock taken as the trace's, without the offset.
- **S-patch-2:** the loader accepts a patch naming the count file.
- **S-out-1:** the harness reports an unexpected outcome without the entry's output.

## Register rows

Promised here, at phase 15's acceptance: the tail at 3,125 in the leader's hand-offs (P16-01 to
P16-04), and the harness keeping each entry's output, reworded from S-fsync-1's unexplained
`target-not-run` (P16-06). Each closes with its task. The rate's limit past the capacity stays open and
unhomed, out of this phase's scope. The rows promised to phase 0 (the person's walk) stay the person's.
