# Phase 16 — task breakdown

The leader's hand-offs, measured apart; the tail rule restated after them; the harness keeps each
entry's output (spec §11 phase 16, added at phase 15's acceptance; its done criterion is P16-00's to
write, at this breakdown's approval). Format as in `docs/phases/P15/breakdown.md`, parsed by `gates
breakdown`; outcomes will say `(evidence)` or `(forcing)`.

**Approved** (reviewer), decisions 1 to 3 and 5 to 7; **decision 4 replaced:** the rule is sent to the
reviewer between P16-03 and P16-04, committed, pushed and sent as a message, and P16-04 starts only on
the reviewer's answer. Machine contention was the wrong objection: reviewing a rule costs no machine
time, only wall-clock. And showing that the rule's commit precedes the reading's records proves
ordering, not independence: the rule would be written from P16-02's numbers and then used to read
P16-04, with nothing between them, the loop a rule fixed in advance exists to break. P15-04's rule was
wrong in its branches and that was learned only afterwards; a rule derived from the data it will judge
is weaker than that one was. Whether a rule written from P16-02 can be independent of P16-04 at all is
answered under decision 4, so that what the reading is worth is decided before it is taken.

**Out of scope, stated first: the replication pipeline.** Past the capacity (9,375 writes a second) the
pipeline's one append in flight per follower is the candidate limit, and widening it would reverse
P11-02, the fix for the 200-times resend defect, for throughput past an operating point pinned at a
third of the capacity (phase 15's decision 3). Its register row stays open and unhomed. No task here
changes the pipeline, and a reading that points at it is reported, not acted on.

**Where phase 15 left the tail.** *Superseded by P16-01's trace fix:* the composition below was
largely the sampled trace's own cost, its collections landing on client-to-leader (P16-01's outcome; at
P16-02, client-to-leader holds 0.03). With the remedy (outputs no persist precedes released at a batch's
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
- **the corrected tail rule is written after that measurement and sent to the reviewer before the
  reading it governs,** each branch's numeric test written apart from its reading, a branch for no
  branch fitting, each branch's reading checked against the segments it names before the branch is
  reported, and each branch's direction stated for a held-out condition P16-02 does not measure;
- **the tail at 3,125 writes a second is read by it,** after the reviewer's answer, at the operating
  point and, if the reviewer takes it, in the held-out condition, from traced runs each within 1.5 times
  the untraced 99th percentile at the same rate and condition (per run, phase 15's decision 4); no
  remedy for the tail is built in this phase, the reading naming the next subject;
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
and sent to the reviewer (P16-03); then, on the reviewer's answer, the reading on fresh runs (P16-04).
Then the margin audit at the last host change (P16-07) and the cost in CI (P16-08).

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
4. **The rule is written after P16-02 and sent to the reviewer before P16-04** (replaced at approval).
   It is committed as an amendment to this breakdown, pushed, and sent to the reviewer as a message;
   P16-04 starts on the reviewer's answer, with whatever the reviewer changes. Its form is fixed now:
   each branch a numeric test over the measured parts and, apart from it, a reading; a branch for no
   branch fitting; each reading checked against the parts it names before its branch is reported; and,
   for the held-out condition below, the direction each branch's reading predicts.
   **Can a rule written from P16-02 be independent of P16-04? Not if P16-04 only measures P16-02's
   condition again.** The rule will be written knowing which parts dominate at 3,125 writes a second.
   Fresh runs of the same condition on the same machine will reproduce that composition within its
   spread, so the rule's numbers will select the branch P16-02 already points to, almost surely. Such a
   reading tests two things: that P16-02's composition replicates, and that each branch's reading
   survives its check against the parts. It does not test whether the rule is right, and the reviewer's
   reading of the rule before it is used is then the only independent check on it. What would let the
   reading test something is a condition P16-02 does not measure, **fixed now, before P16-02's numbers
   exist**, for which each branch's reading must predict a direction: **3,125 writes a second beside one
   busy process** (a spinning loop, as the margin audit's busy mode runs three). If the tail's hand-offs
   are the hosts' threads waiting for processors, the reader's and the sender's parts grow there and
   delivery does not; if they are the kernel's delivery, delivery grows; if neither moves, the hand-offs
   are not contention for processors. The reviewer decides at the rule's review what each of P16-04's
   two readings is worth, and whether the held-out one is taken, before either is. **Not chosen:**
   writing the rule now, before the parts are known, which is P14-04's mistake again; reading a
   resample alone and calling it a test.
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

## The corrected tail rule (P16-03), sent to the reviewer before P16-04

**Retired at phase 16's acceptance** (reviewer, decision 2). The tail stands as reported (decision 1), so no
reading follows, and a rule nothing will use is retired rather than fixed. Its two defects are findings:
its group Q, the loop's queues, held the wait for the loop's thread to be run, a hand-off its name does
not cover (P16-04); and it did not say whether a check is read on the runs' means or run by run, which
differ beside the busy process. A later reading through the trace starts from a rule written fresh.

Written after P16-02's second set and from its numbers (m:p16-02-set2-*), committed here, pushed, and
sent to the reviewer; P16-04 starts on the reviewer's answer, with whatever the reviewer changes.

**What it reads.** At 3,125 writes a second, each traced run within 1.5 times the median of its
condition's untraced 99th percentiles (per run, phase 15's decision 4): the writes over the run's own
99th percentile, and their excess over the median write part by part (P16-01's seventeen parts, the
records' `tail_share_part_*`), as shares of the whole excess; the shares averaged over the runs kept,
each run's range beside them. The groups, fixed now:

| Group | Parts |
|---|---|
| G, the generator's lateness | generator-late |
| D, the kernel's delivery | client-to-leader.delivery |
| H, the hosts' thread hand-offs | client-to-leader.reader, leader-to-send.sender |
| Q, the loop's queues (P14-04's) | leader-queue, leader-queue-answer, commit-respond |
| P, the persists (P14-04's) | leader-persist, follower-persist |
| W, the pipeline's wait | leader-to-send.before |
| R, the rest | connection-wait, leader-to-send.loop-hold, network-out, follower-queue, follower-to-send, network-back, response-to-client |

**The branches.** The tests are taken in order and the first that holds is the rule's answer; "holds the
tail" is P14-04's half, fixed at phase 14. Each branch's reading is written apart from its test, is
checked against the parts it names before the branch is reported (a failed check sends the reading to
vii), states the direction it predicts in the held-out condition (3,125 beside one busy process, fixed at
approval), and what it implies.

1. **G ≥ 0.5.** *Reading:* the generator's own lateness, the instrument's and not the system's. *Check:*
   the generator's lateness at its 99th percentile is at least half the tail writes' mean excess.
   *Held out:* G grows (the generator shares the processors). *Implies:* no host subject; the generator
   is fixed first.
2. **H + D ≥ 0.5 and H ≥ D.** *Reading:* the hosts' threads wait for a processor at their hand-offs, a
   reader or a sender woken late. *Check:* the reader's or the sender's 99th percentile is at least 1 ms,
   delivery's under 100 µs. *Held out:* H grows by at least half again in share, D stays under 0.05.
   *Implies:* the hosts' threads (a reader per connection, the senders' wakes) are the next subject.
3. **H + D ≥ 0.5 and D > H.** *Reading:* the kernel's delivery on loopback. *Check:* delivery's 99th
   percentile is at least 1 ms. *Held out:* D grows, H does not. *Implies:* the kernel's networking,
   outside the hosts' code; reported.
4. **Q ≥ 0.5.** *Reading:* inputs wait in the leader's queue behind the batch in progress, whose sync is
   one at a time (§8's limit), at a loop near saturation. *Check:* the leader's loop is at least 0.85
   busy, and leader-queue's 99th percentile is at least twice the leader's mean sync. *Held out:* Q's share
   grows and the loop's busy fraction rises; P's share falls (the disk is not the processor). *Implies:*
   the tail follows the median's limit, the loop's service per write; a change to it is the one §8 states
   as out of reach with one sync at a time, so its home is the reviewer's.
5. **P ≥ 0.5.** *Reading:* the disk's own time. *Check:* the leader's persist at its 99th percentile is
   at least three times its mean sync. *Held out:* P's excess unchanged in microseconds, its share
   falling as others grow. *Implies:* the file system and its syncs.
6. **W ≥ 0.5.** *Reading:* an append waits for the previous append's answer, one in flight per follower
   (P11-02). *Check:* the time before emission at its 99th percentile is at least one replication round
   trip's median (network-out, follower-queue, follower-persist, follower-to-send, network-back). *Held
   out:* W grows in microseconds (the follower's side slows). *Implies:* the replication pipeline, out of
   scope by the breakdown's first statement: reported, not acted on; its row stays open.
7. **No group holds half, the two largest together hold at least 0.5, each at least 0.2, and both have a
   reading above.** *Reading:* the tail is shared by those two mechanisms; each group's own reading is
   checked as in its branch, and this branch is reported only if both checks pass. *Held out:* each
   group's own direction. *Implies:* the larger group's subject first, the second named with it. R has no
   reading (it is not one mechanism), so a pair that includes R cannot pass here.
8. **Otherwise, or a check failing:** no branch fits. The rule is reported as wrong, with what the parts
   show and what it should have said (phase 15's lesson), not read through its least-bad branch.

**Branch 7's floor was moved after its choice was disclosed** (the reviewer, on the rule as sent). The
rule went to the reviewer with a floor of 0.15 in branch 7, and with the statement that it was written
knowing W at 0.18 and that 0.2 would send P16-02's numbers to branch 8: a threshold fitted to the data it
judges, the circularity of phase 13's allowance model. It is set at 0.2, the round number chosen blind;
branch 7 was the only threshold chosen with the answer in view, the half in branches 1 to 6 being
P14-04's, fixed at phase 14. The disclosure is what made the rule usable, and the record of it stays.

**What P16-02's numbers select, said before the reading.** Over P16-02's four runs at 3,125 the mean
shares are Q 0.40, W 0.18, P 0.16, R 0.16, G 0.07, H 0.03, D 0.00 (the parts' ranges in P16-02's outcome).
No group holds half, and the second largest, W, holds 0.18, under branch 7's floor, so **P16-02's
composition fits no branch: branch 8**. The parts show the loop's queues largest (Q's check passes in
every run: the loop 0.94 busy, leader-queue's 99th percentile 1.1 to 2.0 ms against mean syncs of 251 to
259 µs), then three groups between 0.16 and 0.18, the pipeline's wait, the persists and the rest (W's
check passes too: the time before emission's 99th percentile 1.0 to 2.4 ms against a round trip's median
of 361 to 379 µs). Run by run the two largest groups were Q and W, Q and P, Q and W, and G and W (one
run's generator lateness 0.25).

**What each reading in P16-04 can test.** As decision 4 says: on fresh runs of P16-02's condition, the
rule's answer is nearly determined by P16-02, so that reading tests whether the composition replicates,
not whether the rule is right; the reviewer judged it worth little. The held-out condition can test the
readings: beside one busy process, **Q's share grows and the loop's busy fraction rises, W grows in
microseconds, P's share falls, H stays under 0.1 and D under 0.05** (branches 4, 6, 5, 2 and 3's
directions, the ones branch 7 joins). If H grows past half again or D past 0.05, the hand-offs are
processor contention after all and those readings are wrong. **The reviewer took the held-out reading**:
P16-04 runs it, with the operating point interleaved beside it as its baseline, so that each direction is
read against runs of the same hours.

## Tasks

### P16-00 — The spec's phase-16 row

- **Task:** Spec §11's phase-16 row gets its done criterion (decision 1). The register's rows promised to phase 16 cite the row.
- **Vacuity:** A done criterion that names a remedy rather than a measurement and a rule would admit building before reading; guarded by the criterion naming the parts, the rule's order and its form, and by `gates register`, which reads the phase row.
- **Sabotage:** ; manual: the diff checked against the decisions (§11 only)
- **Verifiable here:** partial — the diff and the register gate here; its approval the reviewer's
- **Prediction:** The amendment changes §11's phase-16 row and no other section; `docs/design/node-interface.md` is unchanged through the phase, the new stamps being bench-mode observation only. **Observable:** the diff, and the phase's diff of node-interface.md.
- **Outcome:** right (evidence) — `011c044` changes §11's phase-16 row (its done criterion) and no other section of the spec, and the two register rows promised here cite it. `docs/design/node-interface.md` is unchanged through P16-01: the arrival and release stamps are bench-mode observation, the kernel's timestamp asked for only on a traced host's client sockets. Whether it stays unchanged is read again at the report.

### P16-01 — The hand-offs' instrument: the kernel's arrival and the send's release

- **Task:** Decisions 2 and 3. In a traced bench run, the leader's client sockets carry the kernel's receive timestamp and its reader stamps each line's arrival (`ClientArrived`, the kernel time, on the trace's clock); the executor's hand-off of a frame to the peer's queue is stamped (`Released`). The decomposition splits client-to-leader into delivery and reader, and leader-to-send into before emission, hold and sender; the records carry each part's median, mean and 99th percentile and its tail share. The sampled trace keeps both stamps as it keeps the stamps they join.
- **Vacuity:** An arrival stamped when the reader returns, not when the kernel received the bytes, makes the reader's part zero whatever it holds; guarded by a test on a socket pair whose reader waits a planted 20 ms before reading: the reader's part holds the 20 ms and delivery under 1 ms (S-hand-1). A release stamped in the sender task shows no sender hand-off; guarded by the bench's hand-off control with a planted delay in the sender's dequeue, which must appear in the sender's part and not in the hold (S-hand-2). A clock offset gone wrong puts arrivals outside their writes; guarded by the decomposition's causality count, which a test plants (S-hand-3).
- **Sabotage:** S-hand-1, S-hand-2, S-hand-3
- **Verifiable here:** yes — the host suites, the sabotages and the traced runs on this machine
- **Prediction:** The containers' kernel delivers the receive timestamp for TCP; at 3,125 every joined write has its arrival and every append its release, with no causality violation; the traced runs stay within 1.5 times the untraced 99th percentile in at least three runs of five (P15-03's set was 1.40 on aggregate), the two stamps adding under 5% to the traced median. **Observable:** the records' joined counts, violations and percentiles.
- **Outcome:** partly (evidence) — the kernel delivers the timestamp and every joined write has its arrival and its release within its segment, but the instrument as first built did not stay within 1.5 times the untraced tail, and beneath the new stamps the trace's cost was its own. **Right:** the containers' kernel timestamps what TCP receives, with one condition: it turns the timestamps on for the machine in deferred work, so a connection's first segment can come unstamped (the socket tests wait for a stamped line); in all 45 traced runs of the phase's hosts every joined write had its arrival and its release, none outside its segment (m:p16-02-s10-*, m:p16-02-cost*-ab-fix-*, m:p16-02-gc*-s10-*, m:p16-02-set2-s10-*); the release's control moved the sender's part by 1.01 times the delay waited and no other part by more than 80 µs; with the instrument as it stands (`03cd5f7`) four traced runs of five are within 1.5 times at each rate (1.07 to 1.24 at 3,125), and the traced median is 3.2% over the untraced at 3,125 and 0.2% at 1,250, inside 5% (m:p16-02-set2-*). **Wrong:** as first built (`bcba8fc`) no traced run at 3,125 came within 1.5 times (1.57 to 25.6, three over 20 ms; m:p16-02-s10-r1-local-3125-1 to -r5). Interleaved against the trace before the arrival (phase 15's, at `7b4d8ad`), five pairs, its 99th percentile was 13.2 ms against 9.4, and 5.7 untraced (m:p16-02-cost-*); the leader's loop was as busy under both (0.93), and the difference was client-to-leader, 6.2 to 7.7 ms at its 99th percentile against 1.9 to 2.3. The reader's buffers moved off the young generation changed nothing (16.2 against 7.8, m:p16-02-cost2-*; `0223f91` named that cause before testing it, a finding); one blocking `recvmsg` a line, as the stream reader waits, brought it level with phase 15's trace (10.3 against 9.5, m:p16-02-cost3-*; `f81c74b`). Both traces were then about 1.8 times the untraced, the threshold out of reach, so the cost beneath was the trace's own: with the collector's pauses in the records (`71a5acd`), a traced run paused the hosts' process 125 to 133 ms with two collections of generation 2, against 38 to 46 ms and none untraced (m:p16-02-gc-*), the sampled trace keeping each stamp as an object until the run's end. Kept in arrays allocated at its start (`03cd5f7`), a traced run paused 40 to 47 ms and its 99th percentile was the untraced one (5.2 against 5.7 ms, m:p16-02-gc2-*). Three deviations from the task: the clock offset is read at each line, the tightest of three brackets, not once at the trace's start and checked at its end (the per-write causality count is the check); the release's control is a class of its own beside the bench controls, so their entries do not run it; and every load record now carries the hosts' process's collector pauses.

### P16-02 — The hand-offs measured apart

- **Task:** At the head after P16-01 (the remedy's host), generator separate, traced keeping one write in ten and untraced, five runs each at 1,250 and 3,125 writes a second, interleaved; for the traced runs each within 1.5 times the untraced at its rate, each part's share of the tail writes' excess over the median write, and its median and 99th percentile; the growth from 1,250 to 3,125 by part. A description, read by no rule.
- **Vacuity:** Parts that sum to more or less than their segment describe another quantity; guarded by each write's parts summing to its segment (the decomposition's sum check), its gap reported.
- **Sabotage:** ; manual: each part's sum against its segment, each run's 99th percentile against the threshold
- **Verifiable here:** partial — measured here; its checks are by hand against the records
- **Prediction:** Of client-to-leader's share of the tail's excess, the reader's part holds at least three quarters and delivery under 0.05 of the excess, delivery's median about 20 µs and the reader's 99th percentile over a millisecond: the reader's wake, not the network, is client-to-leader's tail. Of leader-to-send's share, the sender's part holds 0.05 to 0.10 of the excess, the time before emission 0.04 to 0.10 and the hold under 0.03. **Observable:** the records' part shares and percentiles.
- **Outcome:** partly (evidence) — the parts are measured apart, and the hand-offs this phase split are not where the tail is once the trace stops paying for itself. The first set (`bcba8fc`, m:p16-02-s10-*, m:p16-02-untraced-*) measured an instrument that cost the tail it read, and is superseded by the second, at `03cd5f7`: five traced and five untraced at each rate, interleaved, generator separate (m:p16-02-set2-*). Four traced runs of five within 1.5 times at each rate (at 1,250 r1 is 1.66; at 3,125 r3 is 1.65). The parts sum to their segments by construction, as the segments sum to the latency (P12-03); the stamps' span against the generator's latency is within 0.13% in every run. **At 3,125,** the tail writes' excess over the median write, mean over the four runs (each run's range): the loop's queues 0.40 (0.19 to 0.57; leader-queue 0.37, its 99th percentile 1.1 to 2.0 ms), the time before the append's emission, the pipeline's wait, 0.18 (0.10 to 0.26; its 99th percentile 1.0 to 2.4 ms), the persists 0.16, the rest 0.16 (the follower's queue 0.05, the response 0.04), the generator's lateness 0.07 (one run 0.25), the reader 0.03 (0.002 to 0.09; its 99th percentile 175 to 381 µs), the loop's hold to the release 0.01, the sender 0.000 (its 99th percentile 9 to 17 µs: the sender runs on the loop's thread, the channel completing its wait inline), delivery 0.000 (its median 3 µs, its 99th percentile 15 to 25 µs). **At 1,250** the persists hold 0.30, the loop's queues 0.29, the reader 0.12, the generator's lateness 0.09 (one run 0.30), the response 0.08 and the pipeline's wait 0.07. **From 1,250 to 3,125** the mean write grows 457 µs: leader-queue +106, the pipeline's wait +94, leader-queue-answer +94, the leader's persist +73, the follower's +55, the loop's hold +20, the reader +5, the sender and delivery nothing. **Against the prediction:** the reader holds all of client-to-leader's share and delivery none (right), but client-to-leader's share is 0.03, delivery's median 3 µs, not about 20, and the reader's 99th percentile under 400 µs, not over a millisecond; the sender holds nothing, not 0.05 to 0.10, the time before emission 0.18, not 0.04 to 0.10, and the hold under 0.03 (right). The prediction was written from phase 15's composition, which was the trace's own cost landing on client-to-leader (a finding).

### P16-03 — The corrected tail rule, sent to the reviewer

- **Task:** Decision 4. Written from P16-02's description, committed as an amendment to this breakdown, pushed, and sent to the reviewer as a message; the phase waits for the answer before P16-04. Branches whose numeric tests range over the measured parts (the persists; the loop's queues; the hosts' thread hand-offs, reader and sender; the kernel's delivery; the generator's lateness; the rest), each with its reading written apart, a branch for no branch fitting, for each reading the part it must be checked against before its branch is reported, the direction each reading predicts in the held-out condition (3,125 writes a second beside one busy process), and what each branch implies for the next subject.
- **Vacuity:** A rule written from the data it will judge fits that data by construction; guarded by the reviewer's reading of it before it is used, and by the held-out condition, fixed at approval before P16-02's numbers existed, where each branch must predict a direction.
- **Sabotage:** ; manual: the reviewer's review; the rule's commit against P16-04's records' commits
- **Verifiable here:** partial — git orders the commits; the rule's independence is the reviewer's to judge
- **Prediction:** The rule is written in one commit, sent before P16-04 starts, and changed only as the reviewer's answer directs. **Observable:** the breakdown's history, the message, the records' commits.
- **Outcome:** right (forcing) — the rule was written in one commit (`8a55d6c`), after P16-02's second set and from it, pushed (`83750c0`) and sent before any P16-04 run; the reviewer's answer moved branch 7's floor from 0.15 to 0.2 (`5872758`), on my disclosure that 0.15 was written knowing W at 0.18, and took the held-out reading; nothing else in the rule changed. The prediction described the procedure I then followed, so it could not come out otherwise. Two defects of the rule surfaced only when it was used (P16-04): it did not say whether a group's check is read on the runs' means or run by run, and its groups put the reader's channel to the loop inside the loop's queues.

### P16-04 — The tail read by the corrected rule

- **Task:** On the reviewer's answer, fresh runs after the rule's commit, generator separate: five traced (one write in ten) and five untraced at 3,125 writes a second, interleaved; and, if the reviewer takes it, the same beside one busy process; the rule applied to the traced runs each within 1.5 times the untraced of their condition, each branch's reading checked against its part before it is reported, and in the held-out condition each part's direction against the one its branch predicted. No remedy built; the reading names the next subject, and says what each reading is worth as the reviewer decided.
- **Vacuity:** A reading taken from runs beyond the threshold reads the trace again; guarded by the per-run threshold, checked first, from the records. A reading of a resample presented as a test of the rule; guarded by decision 4's statement of what each reading tests, carried into the outcome.
- **Sabotage:** ; manual: the threshold against each traced run, the branch's reading against its part, the held-out directions against the rule's
- **Verifiable here:** partial — measured here; whether the rule is the right one is the reviewer's
- **Prediction:** At the operating point the rule's numbers select the hosts' thread hand-offs, reader and sender together holding more than half of the tail's excess, and its reading holds against the parts; the persists hold 0.15 to 0.25, the generator's lateness under 0.05. Beside one busy process the reader's and the sender's parts grow by more than half again in share while delivery stays under 0.05, as a processor-contention reading predicts. **Observable:** the records and the rule.
- **Outcome:** wrong (evidence) — the rule does not select the hosts' thread hand-offs, and they did not grow beside a busy process; the held-out reading falsified the loop's-queue reading's mechanism instead. On the reviewer's answer (the floor at 0.2, the held-out reading taken, the resample worth little), five rounds at `5872758`, each a traced and an untraced run beside one busy process and the same at the operating point as their baseline, interleaved, generator separate (m:p16-04-*). Every traced run had every joined write's arrival and release within its segment. **At the operating point** (the resample, all five traced runs within 1.5 times): the mean shares are W 0.27, Q 0.25, P 0.18, R 0.17, H 0.12, G 0.01, D 0.00, so the rule reads branch 7, the pipeline's wait, then the loop's queues, both checks passing; run by run branches 4, 6, 8, 8, 8. P16-02's composition (Q 0.40, W 0.18, branch 8) did not replicate, the machine slower in these hours (untraced medians 1,056 to 1,459 µs against 1,055 to 1,129). **Beside one busy process** (all five traced runs within 1.5 times, and below 1, 0.33 to 0.75: the untraced 99th percentiles were 16 to 19 ms in three runs of five, the traced 5.4 to 12.2 ms, a difference the per-run gate, which bounds the traced side only from above, does not see): the mean shares are Q 0.42, P 0.20, W 0.15, R 0.14, H 0.07, G 0.02, D 0.00. With each group's check read on the runs' means, branch 7, the loop's queues then the persists; read run by run, Q's check fails in one run (the loop 0.847 busy, under 0.85), so branch 8. The rule as written did not say which, a defect of its own. **The directions against the interleaved baseline:** Q's share grew (0.25 to 0.42, right); the pipeline's wait grew in microseconds (mean 122 to 180 µs, 99th percentile 2.5 to 2.8 ms, right); H stayed under 0.1 on the mean (0.12 to 0.07; two runs 0.13 and 0.19) and D at 0.00 (right); **the loop's busy fraction fell, 0.95 to 0.91, where branch 4's reading predicted it would rise, and P's share rose, 0.18 to 0.20, where it predicted a fall (wrong).** The loop did less work a write beside the busy process (278 µs of execution against 288, in larger batches) and was less busy, yet its queue grew (mean 185 to 245 µs, 99th percentile 1.6 to 2.4 ms): the inputs waited for the loop's thread to be run, time the loop's own clock counts as waiting, not behind a saturated loop's sync. So branch 4's reading, and branch 7's as it joins it, is wrong in its mechanism under contention, and the rule's groups put a thread hand-off, the reader's channel to the loop, inside Q, where no part separates it. **Against the prediction:** H holds 0.12 at the operating point and 0.07 beside the busy process, not more than half, and did not grow (wrong); the persists 0.18 (inside 0.15 to 0.25) and the generator's lateness 0.01 (under 0.05) right. The reading names the next subject: leader-queue split at the loop thread's wake, the channel's hand-off apart from the wait behind a batch.

### P16-05 — S-ran-2 anchored away from the counts

- **Task:** Decision 7. S-ran-2's patch keeps only its project-file change; its command lowers the project's baseline line by the excluded file's test count, computed from the file, and runs the count check; a `baseline:` line keeps the unpatched run plain. The sabotage loader refuses an entry whose patch or control names `ci/test-baseline.txt`.
- **Vacuity:** A rule against the count file that refuses nothing guards nothing; guarded by a test that an entry whose patch names it is refused (S-patch-2). An entry moved to a command that no longer breaks the count check passes as caught for another reason; guarded by its message, which names the file's tests as written and not executed.
- **Sabotage:** S-patch-2
- **Verifiable here:** yes — the harness runs S-ran-2 here and in CI
- **Prediction:** S-ran-2 is caught with its message; after this task no commit of the phase touches `sabotage/S-ran-2/`, although the Gates, Host and Host.Slow counts each change at least once in the phase; no other entry names the count file. **Observable:** the harness's runs and the phase's history of `sabotage/S-ran-2/`.
- **Outcome:** right (evidence) — S-ran-2 is caught with its message (by hand at `5125336`, and in GitHub's head shards, run:38997459747), and no commit after `5125336` touches `sabotage/S-ran-2/`, though the Gates count changed once (229 to 230, `7b4d8ad`), the Host count twice (52 to 56, `bcba8fc`; 56 to 57, `03cd5f7`) and the Host.Slow count once (12 to 13, `bcba8fc`), each of which would have broken the old patch. No entry names the count file: the loader would refuse it (S-patch-2, caught).

### P16-06 — The harness keeps each entry's output

- **Task:** Decision 6. For any entry whose outcome is not the one it expects, the harness prints the entry's test run's last 80 lines and its results summary into the log under the entry's id, in CI and in the local stages alike.
- **Vacuity:** A path taken only on rare outcomes is untested until one happens; guarded by a test that drives the outcome classification with a run whose results omit the target and requires the output's tail in the report (S-out-1). Output printed for every entry buries the one that matters; guarded by the same test requiring none for an expected outcome.
- **Sabotage:** S-out-1
- **Verifiable here:** yes — the harness's tests here
- **Prediction:** The change touches the harness's outcome reporting and its tests only; no head shard's harness step grows by more than 5 s; if an unexpected outcome happens in the phase, its output is in the log, and none is expected. **Observable:** the diff, the shards' times, the logs.
- **Outcome:** partly (evidence) — the output is kept and printed for an unexpected outcome, but the change reached past the harness's reporting, and the first unexpected result of the phase was one it did not cover. **Right:** S-out-1 is caught; printed by hand on a scratch branch, an entry whose outcome was not the expected one was followed in the log by its run's last lines (18, its whole run); GitHub's slowest head shard's harness step was 328 s against phase 15's 374 (run:38997459747, plans differing, so no shard compares one to one). **Wrong:** drawing the test run's classification out of the entry's run moved the line after S-harness-6's hunk, and its patch stopped applying (`7b4d8ad`; the known-red commits in the report); and the phase's unexpected results were apply failures, whose detail is git's own message, and one baseline command, S-ci-1's, that failed once unpatched at `27744b7` with its assertion cut off by the twelve lines a baseline's failure kept. A baseline command's failure now keeps its run's last 80 lines too (`9c3fe8d`), with no test of its own; the Gates test's failure stays unexplained (a register row).

### P16-07 — The margin audit after the host's changes

- **Task:** `scripts/margins.sh` at the phase's last host change, three modes, five runs a side, with a row for the hand-off control's sender part (S-hand-2's); the table becomes this phase's, read by P14-06's observer at the report.
- **Vacuity:** An audit at a commit before the host's last change describes another host; guarded by P14-06's observer.
- **Sabotage:** ; manual: `gates margins` over the table, and `gates margins --currency` at the report
- **Verifiable here:** partial — the audit runs here; GitHub's runners give other margins, seen only when a control fails there
- **Prediction:** Every row keeps the audit's 1.5 on both sides, the new sender row included; no other row moves by more than a tenth of its bound. **Observable:** the table and the gates.
- **Outcome:** right (evidence) — `scripts/margins.sh` at the last change to the controls, `0f75a29` (the group commit control given the bench controls' election timeout after the report's first run), three modes, five runs a side (`docs/phases/P16/margins-bench.tsv`); a first audit at `1f6c10d` read the same and was superseded by that change. Every row keeps 1.5 times its bound on both sides, and `gates margins` passes. The release control's rows: the sender part's rise 1.83 times its lower bound and 2.97 times inside its upper, the largest other part 8.95 times under its bound, and with S-hand-2 the sender part never reaches its bound and the loop's hold crosses the others' by 2.28 times. The group commit control keeps 7.45 unpatched and 2.00 with S-group-1. No other row's unpatched median moved by more than 0.040 of its bound against phase 15's table (stall-open-p95's), inside the tenth predicted.

### P16-08 — The phase's cost in CI

- **Task:** The harness shards, the soaks and the per-commit matrix on the phase's first-attempt runs against phase 15's, the entries added, whether every job was given a runner, and the never-verified commits tabled.
- **Vacuity:** A run with a job that never ran compares less work; guarded by counting only jobs with steps and stating any that never ran.
- **Sabotage:** ; manual: the comparison checked against each run's job list
- **Verifiable here:** no — CI's numbers only in CI
- **Prediction:** The phase adds five entries (S-hand-1 to S-hand-3, S-patch-2, S-out-1) and retires none; no head shard passes 400 s against phase 15's slowest of 374; the soaks' effect counts are phase 15's line for line; every job is given a runner. **Observable:** the shards' slowest step, the manifest, the soaks' reports, the job list.
- **Outcome:** partly (evidence) — the phase's push ran on its first attempt with every job given a runner and its head green but for one observer, and its per-commit matrix red where the known-red commits are. Run:38997459747, at `83750c0`: 204 jobs, all with steps; five entries added (S-hand-1 to S-hand-3, S-patch-2, S-out-1), none retired (364 live to 369); the slowest head shard's harness step 328 s (its job 377 s) against phase 15's 374 (right); the baseline soak's effect lines are phase 15's line for line (run:38843960353), Core and the simulator unchanged since phase 15's acceptance (right); soak 784 s, soak-membership 857 s (phase 15: 780 and 771), Compose 91 s. **Wrong:** 40 of the 182 per-commit jobs failed, at the eleven commits from `7b4d8ad` to `8a55d6c` (S-harness-6's patch, S-proc-4's patch, and S-patch-1's baseline, which runs the patch gate, one cause; the breakdown gate refusing P16-00's outcome, "right so far", at the eight commits from `c125c6d` to `ef16618`, with S-bd-1, S-bd-2 and S-bd-7's baselines, which run that gate, a second, left out of this outcome and the report's table until the phase's acceptance; and S-ci-1's baseline once at `27744b7`), accepted by the reviewer as known-red; and the head's build failed on the class-reach observer, which named the sampled trace's room test (`ASampledTracePastItsRoomDropsAndCounts`, red beside three of its class's four other entries), listed in `9c3fe8d`. The report's first push (`9719e96`, run:39062045985) failed one shard on S-group-1's baseline, the group commit control answering 21 writes in a crowded shard (an election on the default 150 to 300 ms timeout is inferred, not read: the baseline check of a target keeps no output); a re-run of the failed jobs passed it but could not make the collector green, and the control was given the bench controls' 1 to 2 s (`0f75a29`), the audit run again after it. The report's own run at its final head is the certifying one.

### P16-09 — The README walk removes its copy

- **Task:** Maintenance, added after the phase's acceptance (the reviewer). `scripts/readme-walk.sh` copies the working tree into a fresh `mktemp -d` and never removes it: 195 copies of about 109 MB each had filled the disk by the acceptance push's local run, and its touched-file stage failed its baseline builds with "No space left on device". The walk removes its temporary directory on exit, whether the walk passes or fails, after printing what it prints now; a test runs the script on a small repository of its own with a passing walk and a failing one, each under an empty `TMPDIR`, and requires that directory empty afterwards.
- **Vacuity:** A cleanup on the success path only leaves a failing walk's copy, the case a person debugging it creates most often; guarded by the failing walk's test. A test that reads the real `/tmp` passes or fails with whatever else ran; guarded by giving each run its own `TMPDIR` (S-walk-2: the removal dropped, both tests red).
- **Sabotage:** S-walk-2
- **Verifiable here:** yes — the test here and in CI; the local walk's leftovers counted in `/tmp` before and after
- **Prediction:** The change touches `scripts/readme-walk.sh` (the trap that already removes its NuGet volume removes its directory too), one new test class with two tests, the Gates count (230 to 232), S-walk-2's entry and its cost line, and nothing else; S-walk-2 is caught by both tests; S-walk-1 is still caught for its reason; the walk's CI job passes, and a local walk leaves no `tmp.*` directory behind. **Observable:** the diff, the touched-file stage, the walk job, `/tmp` counted before and after a local walk.
- **Outcome:** partly (evidence) — the walk removes its copy and both tests hold it, but the first version failed on GitHub's unprivileged runner, the prediction's last CI clause, which the local run as root could not see. **Right:** `d8bfc17` touches `scripts/readme-walk.sh`, `ReadmeWalkTests` (two tests), the Gates count (230 to 232), S-walk-2's entry and its cost line, and nothing else; S-walk-2 is caught with both tests red (2.9 s); S-walk-1, S-readme-1 and S-readme-2 are caught in the local host stage; and around that local run, which walks four times, no walk left a directory in `/tmp`. **Wrong:** on GitHub (run:39105627374) the readme-walk job failed, and so did S-readme-1's baseline in the secrets job, which then passed its 15-minute ceiling. The runner is unprivileged and the SDK container builds as root, so the exit trap's `rm -rf` could not remove the copy's `bin/` and `obj/`, and with `set -e` the failed removal also replaced the walk's exit status. The host sabotages met the same root-owned output in their worktrees in phase 9 (P10-08). `e81223f` hands the copy back through the pinned image on a non-root runner, as `host-sabotages.sh` does, keeps the walk's exit status, and fails a passing walk whose copy it cannot remove. Reproduced as uid 1001 with the Docker socket, the pushed script's walk exited 1 and left its copy; with the fix the full walk passed and a failing one kept exit 3, both leaving `TMPDIR` empty. The non-root path has no test of its own (the tests run as root in the SDK container); its check is the walk job on GitHub's runner, which the report's run reads. The same count found two more leaks of the walk's kind, outside this task: the secret-scan host entries' clones and `host-sabotages.sh`'s empty worktree parents (1,431 `tmp.*` directories before the local run, 1,442 after; a register row).

### P16-10 — The host sabotages and the bench remove their temporary directories

- **Task:** Maintenance, added after the project's close (the person: the README walk's leak, checked for in `scripts/host-sabotages.sh` and `scripts/bench.sh` and fixed where it recurs). The register row P16-09 opened: `host-sabotages.sh` makes a `mktemp -d` parent for each entry's worktree and removes only the worktree, seven empty directories a run; the host entries S-sec-1, S-sec-2 and S-sec-5, and their shared baseline, each `git clone` the repository into a `mktemp -d` that nothing removes, four clones of about 24 MB a run. `scripts/bench.sh` exports other commits' trees (`compose-ab`, `compose-node-ab`, `load-ab`, `soak-ab`) into `mktemp -d` directories that nothing removes, the last two holding root's build output. Each host entry gets one directory, holding its worktree and given to its baseline and command as their `TMPDIR`, removed after the entry and on any exit, root's files handed back first on a non-root runner as now; the bench exports into one directory, removed with its data volume by every mode's exit trap. A test runs the script on a small repository of its own, its entries' baseline and command each making a directory as the secret-scan entries do, with every entry caught and with a baseline that fails, each under an empty `TMPDIR`, and requires that directory empty and no worktree registered afterwards. `scripts/ci-commit.sh run` leaves its worktree too, but runs only in CI's per-commit jobs, on runners discarded after them: out of scope.
- **Vacuity:** A removal on the caught path only leaves a failing baseline's directory, which leaves through a branch of its own; guarded by the failing baseline's test. Giving `TMPDIR` to the command and not the baseline keeps the secret-scan baseline's clone, the larger part; guarded by both tests' baselines making a directory. A test that reads the real `/tmp` passes or fails with whatever else ran; guarded by an empty `TMPDIR` per run (S-hostsab-1: the entry's directory no longer removed, both tests red).
- **Sabotage:** S-hostsab-1; manual: `scripts/bench.sh load-ab 1` at one rate, `/tmp` counted before and after
- **Verifiable here:** yes — the tests here and in CI; `/tmp` counted around the local run; the non-root path as uid 1001 with the Docker socket here, and on GitHub's unprivileged runner in the secrets job
- **Prediction:** The change touches `scripts/host-sabotages.sh`, `scripts/bench.sh`, one new test class with two tests (the Gates count 232 to 234), S-hostsab-1's entry and its cost line, and nothing else; S-hostsab-1 is caught with both tests red; the seven host entries are still caught for their reasons; around the local run, which runs the host stage and walks, the count of `tmp.*` directories in `/tmp` is the same before and after (P16-09's count rose by 11); as uid 1001 with the Docker socket, `host-sabotages.sh` on a clone of its own catches all seven and leaves its `TMPDIR` empty; one `load-ab` run at one rate leaves no directory behind; on GitHub, the secrets job passes. **Observable:** the diff, the tests and S-hostsab-1's run, the host stage's output, `/tmp` counted before and after the local run and the bench run, the uid-1001 run, the secrets job.
- **Outcome:** right (evidence) — every clause held; the script changes were drafted before the prediction was committed, and the prediction was written about what they had not yet been run against. `a375735` touches `scripts/host-sabotages.sh`, `scripts/bench.sh`, `HostSabotagesTests` (two tests), the Gates count (232 to 234), S-hostsab-1's entry and its cost line, and nothing else. S-hostsab-1 is caught with both tests red (4.8 s in the touched-file stage, its only entry); the two vacuity guards were checked by hand as well: the baseline run without `TMPDIR` turns both tests red, a failing baseline's branch without the removal turns its test red. The seven host entries were caught locally (932 s), as uid 1001 with the Docker socket on a clone of its own (its `TMPDIR` empty after, one worktree registered, every file in the clone still its own), and on GitHub's runner (run:39126188489, the secrets job: 713 s, `7 host sabotages, 0 failure(s)`). Around the local run, in a fresh container, `/tmp` held no `tmp.*` directory before and none after; its one new entry was `host-sab.out`, the baseline's output file, of fixed name and overwritten each run. One `load-ab` run at 625 (44 s) left none either; its exported tree was seen in its directory during the run. The bench's hand-back on a non-root host was not run: only as root. That run was red on another account: it was the branch's first push, whose range the each-commit list takes from the merge-base with `origin/main`, the repository's first commit, and it refused 52 commits of earlier phases that changed a report as not a push's head; the harness shards and the per-commit jobs were skipped, so the build and each-commit collectors failed, and every other job passed. `e8e3dcf`, in no per-commit job, had its preflight, build and gates run here, green.

### P16-11 — The release control's delay planted per frame, not per wake

- **Task:** Maintenance, added after the project's close (the person, 2026-10-26: redesign the control rather than move it). P16-01's release control, `ReleaseControlTests.APlantedSenderDelayAppearsInTheSendersPartOnly`, failed three times in a week on GitHub's runners and in none of about ten local runs (the register's unexplained-harness-results row, (4) and (5)). Its delay is waited once each time one of the leader's peer senders wakes to frames (`NodeHost.DelaySender`), and every frame queued by the end of the wait is then sent; its assertion reads each write's sender part, at the 5th percentile, against the average wait per wake. The delay is planted per wake and read per write, and the relation between the two is a scheduling property: the control measures something other than what it asserts, and a quiet machine hides that by making the two coincide (the person). The plant moves to once per frame, before the frame is taken (peek, wait, take, stamp, write), so every write's frame waits the full delay wherever it falls among others, and the average wait becomes per frame. With no delay planted the sender's loop is unchanged. The report line gains the delayed windows' smallest sender part and the count of delayed writes whose sender part is below 95% of the planted delay. The assertions do not change.
- **Vacuity:** A per-frame wait placed after the frame is sent leaves the delay outside the release-to-send segment while the average wait still reads 2 ms; guarded by the sender part's rise, which S-hand-4 drops to zero. A wait between the take and the send, with the release stamped at the take, would put the whole delay in the sender's part however the release was stamped; the release is stamped at the executor's hand-off, and S-hand-2 still moves it beside the send. The per-frame spin keeps each of the leader's senders busy about 0.6 of the time at 300 writes a second: the bound on every other part's rise (0.4 of the wait) is the guard that the load did not move the delay elsewhere.
- **Sabotage:** S-hand-4; shared: S-hand-2; manual: the per-wake plant restored in the working tree with this task's report line, and the control run three times
- **Verifiable here:** partial — locally: both plants' report lines, S-hand-4 and S-hand-2 by the touched stage; whether the redesign holds where the control failed, only GitHub's runners can show, in this push's run and main's
- **Prediction:** Mechanism: with the delay waited once per wake, a frame released while the sender is already waiting is sent at the wait's end, having waited only its remainder, so the sender part's 5th percentile reads how often frames share a wake, which the machine decides; waited once per frame, before the frame is taken, every frame waits it in full. **Observable:** locally, the report line's count of delayed writes whose sender part is below 95% of the planted 2,000 µs is above zero in at least one of three runs with the per-wake plant restored, and zero in each of five runs with the per-frame plant, whose ratio (the sender part's rise over the average wait) is between 0.8 and 1.25 in each; S-hand-4 and S-hand-2 are caught on this commit. If the per-frame plant leaves any delayed write below 95% locally, the redesign has not done what it claims and the work stops before any push. On GitHub, the control passes in every execution in this push's run and in main's (the head's tests, the README walk's, the harness's baseline checks). **A miss on GitHub is a finding about the runner, not the plant:** if the redesigned control fails there, the work stops, the failure is recorded, and the control moves to a local-only split (the person, 2026-10-26), with no further change made to bring it to green.
- **Outcome:** partly (evidence) — every local clause held, and the clause that matters is not decided by this commit. The prediction was committed (`512cb5b`) before any code was written or run. With the per-wake plant restored and the new report line, three runs counted 94, 71 and 41 of 3,000 delayed writes below 95% of the delay, the smallest sender parts 348, 289 and 320 µs: frames do share wakes on this machine too, at 1.4 to 3.1% of writes, just under the 5% a 5th percentile tolerates. That is why the control passed here and failed where the share was larger. With the per-frame plant (`ad3ac51`), five runs counted 0 of 3,000 in each, the smallest sender parts 2,006 to 2,007 µs, and the ratio the assertion reads 0.993 to 1.010; every other part rose by less than 0.05 of the delay at its 5th percentile. S-hand-4 and S-hand-2 were caught on `ad3ac51` with their reasons, by the touched stage (9 of 365 entries, all caught). The GitHub clause, and the stop condition with it, belongs to this push's run and to main's; it can be decided only by them, not written here

### P16-12 — A reader thread's exception ends its connection, never the host's process

- **Task:** Maintenance, added after the project's close (the person, 2026-10-27). The branch run on `ed6964a` (40421775799) failed one job of 120: S-fsync-1 `target-not-run` at `3c9e458`. The output P16-06 keeps for an entry showed the patched test failing its assertion as it should, and then the test host aborting (exit 134) on an unhandled `ArgumentNullException` from `TcpClient.GetStream()` in `NodeHost.ServeClient`. A stop disposes every accepted connection from another thread, and a reader thread that had just started was inside `GetStream()` as its connection was disposed. Each accepted connection is read on a plain thread of its own (P12-05, `cbb2767`), and an exception that leaves a plain thread ends the process, so any test that stops a host while connections arrive can end its own run this way. The readers' bodies catch a list of types (`IOException`, `SocketException`, `ObjectDisposedException`, and for a peer `InvalidDataException`): a guard over the instances someone enumerated rather than the property (§13.19), and the next type will not be on the list either (the person). One already is not: a traced host's client reader (`KernelLineReader`, P16-01, `bcba8fc`) throws `InvalidDataException` for a line past 64 KiB, and `ServeClient`'s list does not name it. The fix is the property. Each reader thread's root catches whatever its body throws, logs it as a host event (`reader-failed`, with the type and message), and ends the thread with its connection closed. The lists stay, and now decide only which endings are quiet; `ArgumentNullException` is added to none of them. The regression test is written and shown failing on `ed6964a` before the fix exists (P11-01's pattern), and lands with the fix. It runs the host (`Raft.Host node`, one node, traced) in a process of its own, so an exception that ends the host is the test's failure, with the host's exit code and standard error in its message, and not a crash of the test run that the harness could only report as `target-not-run`. Its trigger is the one that fires on demand: a line past 64 KiB on a traced host's client connection. The stop race that found the defect cannot be made to happen on demand; the root's catch covers it because it covers every type.
- **Vacuity:** A line the host never reads to its limit (a host not traced, or a kernel that refuses the receive timestamps, so the plain reader takes the connection) leaves the reader waiting and the test passing on any code; guarded by the test asserting that the host closed that connection within 10 s and logged its ending. A host that never started would answer nothing either way; guarded by a `Status|` answered before the line is sent.
- **Sabotage:** S-thread-1; manual: the test run on `ed6964a`, before the fix
- **Verifiable here:** yes — the host's process, its exit code and its output are all local
- **Prediction:** Mechanism: an exception that leaves a reader thread's body is unhandled on a plain thread, which ends the process whatever the exception's type; caught at the thread's root, it ends only the thread. **Observable:** on `ed6964a` the test fails with the host's process gone, exit code 134, and its standard error naming `InvalidDataException` ("a client line longer than 64 KiB") through `ServeClient`; the test run itself completes and reports that failure. With the fix the test passes: the over-long line's connection closed by the host, `Status|` answered on a new connection, and one `reader-failed` event naming `InvalidDataException` on the host's standard output. S-thread-1, the root's catch removed, is caught with the test's assertion as its reason, and S-fsync-1 is caught on the fix's commit by the touched stage. No run here can show that the race no longer ends a process, beyond the property the test holds; GitHub's runs of S-fsync-1 will show whether its `target-not-run` comes back.
- **Outcome:** partly (evidence) — every clause a run here could decide held, and one on GitHub did not (below). The prediction was committed (`46b8dfb`) before the test or the fix was written. With the test added to `ed6964a`'s code, it failed three runs of three in the same way: "the host stopped answering after a reader thread's exception: its process exited with code 134; standard error: Unhandled exception. System.IO.InvalidDataException: a client line longer than 64 KiB / at Raft.Host.KernelLineReader.ReadLine() / at Raft.Host.NodeHost.ServeClient(TcpClient client)". Each time the test run itself completed and reported the failure (exit 2, not 134). With the fix (`a9fd0fc`) the test passed three runs of three, in about 0.6 s each. Run by hand on the same build, the host wrote `{"Event":"reader-failed","Fields":{"error":"System.IO.InvalidDataException","message":"a client line longer than 64 KiB"}}`, answered `ok|Leader|1` on a new connection, and was still running. On `a9fd0fc` the touched stage caught all 10 of its 366 entries: S-thread-1 by the host's exit code ("its process exited with code 134", 4.4 s), and S-fsync-1 with its own reason, the stopped node still accepting connections. The stop race itself cannot be run on demand: whether its `target-not-run` comes back is for GitHub's runs of S-fsync-1 to show. **P16-06 paid for itself here** (the person). The unexplained-harness-results row exists because two harness results could not be diagnosed: no output was kept for them. The third could be diagnosed, because P16-06 now keeps an entry's output, and that output found a real defect. Reading the code then found a second instance of it, and it gave the likely explanation of the row's (1). That (1) cannot be confirmed for exactly the reason P16-06 was built: its output was not kept. **The deciding reason was never S-fsync-1** (the person). Any test that stopped a host while connections arrived could end its own run this way, a latent source of unexplainable reds across the suite that had already cost two (40421775799's, and probably phase 15's at `429c5b2`). And the list the fix does not extend was already wrong before the race showed it: `KernelLineReader` had been throwing a type the list did not name since `bcba8fc`, ten days earlier. **On GitHub the sabotage clause held five times in six.** In the branch run on `380f7bd` (40464503656), S-thread-1 was caught by the host's exit code in five executions and came back `wrong-reason` in one, at `30be2ed`. With the root catch removed, the test failed at its log assertion, after the over-long line's connection had ended and `Status|` had been answered `ok|`. A host this exception ends cannot answer, and here the connection's end coincides with the process's exit (ten probes: the process already a zombie, or exiting with its threads gone). Something answered while the test went on as though its host had. The cause is unknown, because the test's messages kept none of the child's state: the gap P16-06 closed for the harness, reopened in the test written for P16-06's own finding. `c664465` reads the host's life from its process and never from an answer. The test reads the child's own `host-start`, written after both listeners bind, before it uses the port, and after the line it waits for the event or the process's exit. Every assertion's message now carries the child's process state, its latest standard output and its first standard error lines. With S-thread-1's patch the test failed three runs of three on "its process exited with code 134", the message naming the exception and its frames. S-fsync-1 was caught in every execution of that run; whether its `target-not-run` comes back is still for later runs to show. The run was not re-run (the person: re-running past an undiagnosed mechanism is what was declined twice)

### P16-13 — The report-at-head rule holds each report's last change in a push, not every change

- **Task:** Maintenance, added after the project's close (the person, 2026-10-27). **Disclosure: the change was prompted by wanting `main` to reach the branch's head in one push.** The branch carries three commits that change P0's report (`ed6964a`, `380f7bd`, and the re-commit at this push's head), and `gates each-commit`'s report-at-head rule (P4-10) fails any push in which a non-head commit changes a report, so `main` would have had to step through each of them. The correctness argument was checked separately and stands on its own; the person's test was whether the rule would be relaxed with nothing waiting to merge, and it would. The rule exists because `gates reports` asks for the run of the commit that last changed each report (`git log -1 -- <report>` from the head), and CI runs only a push's head: a report whose last change in a push is not the head is one no run can certify, phase 3's red acceptance run. A report changed mid-push and changed again later in the same push is different. Its intermediate state never reaches the head's tree, and nothing asks for its run: `gates reports` takes the last change, and `gates register`, `gates measurements` and `margins --currency` read the head's tree or the head commit (each read for this task). Holding that state to the head guards nothing, and the guarantee was already absent across pushes: `ed6964a`'s version of P0's report was superseded by a later push, and nothing certifies it, legitimately, under the rule as it stands. The case also arises without any merge in view, because `VerifyRun`'s own remedy is to re-commit a report: each use of it on a branch puts the earlier version mid-range for that branch's next merge. The rule becomes: for each report changed in a push's range, its last change in the range is the head. A report changed mid-range and not again still fails, as does one whose last change is a later non-head commit, and the failure names that last change, the one whose state reaches the head's tree. **What the change no longer buys:** a one-step merge is not available anyway. GitHub caps a matrix at 256 jobs, which at 14 shards a commit is 18 non-head commits, and `main`'s range from `c03906f` holds more than 18 after this work. So `main` moves in two pushes (`ed6964a`, then the head) instead of three (`ed6964a`, `380f7bd`, the head); the relaxation saves one step and stands on its correctness alone.
- **Vacuity:** A relaxation that exempts too much passes the shape the rule exists for. Three ways, each a sabotage: an exemption whenever the head changes any report rather than the same one (S-each-7: a P3 report mid-range with a P4 report at the head passes); an exemption for any report change followed by any later commit (S-each-8: the P3 acceptance shape itself passes, the original rule swallowed); and "last" taken as the first change (S-each-9: the failure lands on a superseded commit, and the twice-changed report's last change goes free). The original rule's test, a report followed by another commit, is unchanged.
- **Sabotage:** S-each-7, S-each-8, S-each-9; shared: S-each-5, S-each-6
- **Verifiable here:** partial — the rule's tests, the sabotages, and `gates each-commit` over `main`'s range, locally; `main`'s runs only on GitHub
- **Prediction:** Mechanism: only each report's last change in a push reaches the head's tree and is ever asked for its run, so holding only that change to the head keeps every guarantee the rule gave and drops a requirement that guarded nothing. **Observable:** the original test still fails a report changed mid-range with no later change, with its message unchanged. A report changed mid-range and again at the head passes. A P3 report changed mid-range with a P4 report changed at the head fails, naming the P3 commit. A report changed at two non-head commits fails, naming the later one only. S-each-7, S-each-8 and S-each-9 are caught, and S-each-5 and S-each-6 still are. At this push's head, `gates each-commit --since c03906f --check true` (`main`'s range, which failed at `380f7bd` naming `ed6964a`) passes the rule, and so does the second of `main`'s two pushes on GitHub.
- **Outcome:** partly (evidence) — every clause this commit can decide held, and the range clause is decided after it. The prediction was committed (`0be1a97`) before the tests or the change were written. The three new tests were run first on the unchanged gate. A report changed again at the head failed (one failure, at its first change), a report changed at two non-head commits failed at both, and a P3 report mid-range beside a P4 report at the head failed at the P3 commit, as both rules require. With the change (`355c2a2`) all 237 gate tests pass, the original test with its message unchanged. On `355c2a2` the touched stage caught all 9 of its entries: S-each-7 ("Assert.Single() Failure: The collection was empty"), S-each-8 ("Assert.Contains() Failure", the P3 acceptance shape passing again), S-each-9 ("Assert.StartsWith() Failure", the failure naming the superseded commit), and S-each-5 and S-each-6 as before. `gates each-commit --since c03906f` at `355c2a2`, `main`'s range at that commit, fails naming only `380f7bd`: P0's report's last change there is not the head, which the new rule still fails, and `ed6964a`, superseded, is no longer named. That range already holds 18 non-head commits, 252 jobs at 14 shards, the most GitHub's 256-job matrix allows. The clause that the range passes at this push's head belongs to the re-commit at that head and the local run before the push, and its second half to `main`'s second push

## Sabotage ids

New series: S-hand (P16-01, and S-hand-4 at P16-11), S-out (P16-06), S-hostsab (P16-10), S-thread (P16-12); and S-each-7 to S-each-9 at P16-13, in phase 4's series. Each id's `sabotage/<id>/` entry lands in the same commit
as the check it proves and is run on that commit before it is pushed.

- **S-hand-1:** a client line's arrival stamped when the reader returns it, not with the kernel's
  receive time.
- **S-hand-2:** a send's release stamped by the peer's sender task, not by the executor's hand-off.
- **S-hand-3:** the kernel's clock taken as the trace's, without the offset.
- **S-patch-2:** the loader accepts a patch naming the count file.
- **S-out-1:** the harness reports an unexpected outcome without the entry's output.
- **S-walk-2:** the README walk leaves its copy of the working tree behind (P16-09, after acceptance).
- **S-hostsab-1:** the host sabotages leave each entry's directory, its worktree's parent and its
  commands' temporary directories, behind (P16-10, after the close).
- **S-hand-4:** the release control's per-frame wait placed after the frame's send, outside the
  release-to-send segment the control reads (P16-11, after the close).
- **S-thread-1:** the reader threads' root catch removed, so an exception a reader's list of quiet
  endings misses ends the host's process (P16-12, after the close).
- **S-each-7:** the report-at-head rule exempts a mid-range report change whenever the head changes any
  report, not the same one (P16-13).
- **S-each-8:** the rule exempts any report change followed by any later commit, which passes the P3
  acceptance shape itself (P16-13).
- **S-each-9:** the rule holds each report's first change in the range, not its last (P16-13).

## Register rows

Promised here, at phase 15's acceptance: the tail at 3,125 in the leader's hand-offs (P16-01 to
P16-04), and the harness keeping each entry's output, reworded from S-fsync-1's unexplained
`target-not-run` (P16-06). Each closes with its task. The rate's limit past the capacity stays open and
unhomed, out of this phase's scope. The rows promised to phase 0 (the person's walk) stay the person's.
