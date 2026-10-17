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
7. **No group holds half, the two largest together hold at least 0.5, each at least 0.15, and both have a
   reading above.** *Reading:* the tail is shared by those two mechanisms; each group's own reading is
   checked as in its branch, and this branch is reported only if both checks pass. *Held out:* each
   group's own direction. *Implies:* the larger group's subject first, the second named with it. R has no
   reading (it is not one mechanism), so a pair that includes R cannot pass here.
8. **Otherwise, or a check failing:** no branch fits. The rule is reported as wrong, with what the parts
   show and what it should have said (phase 15's lesson), not read through its least-bad branch.

**What P16-02's numbers select, said before the reading.** Over P16-02's four runs at 3,125 the mean
shares are Q 0.40, W 0.18, P 0.16, R 0.16, G 0.07, H 0.03, D 0.00 (the parts' ranges in P16-02's outcome).
No group holds half; Q and W together hold 0.58, each over 0.15; Q's check passes in every run (the loop
0.94 busy, leader-queue's 99th percentile 1.1 to 2.0 ms against mean syncs of 251 to 259 µs), and W's
too (the time before emission's 99th percentile 1.0 to 2.4 ms against a round trip's median of 361 to
379 µs). So the rule's answer on P16-02 is branch 7, **the loop's queues, then the pipeline's wait**. Two
things about that answer are fragile, and are stated here so the reading cannot hide them:
- **the second place is narrow:** W 0.18 against P and R at 0.16 each. Run by run the two largest
  groups were Q and W, Q and P, Q and W, and G and W (one run's generator lateness 0.25). A resample that
  puts P second selects the loop's queues, then the persists (P's check passes in all four runs); one that
  puts R second fits no branch;
- **the 0.15 floor in branch 7 is new, written knowing W at 0.18:** a floor of 0.2 would send P16-02's
  numbers to branch 8. The half in branches 1 to 6 is P14-04's and was fixed before any of this.

**What each reading in P16-04 can test.** As decision 4 says: on fresh runs of P16-02's condition, the
rule's answer is nearly determined by P16-02, so that reading tests whether the composition replicates
and whether the selected readings pass their checks, not whether the rule is right. The held-out
condition can test it: under branch 7 as selected, beside one busy process, **Q's share grows and the
loop's busy fraction rises, W grows in microseconds, P's share falls, H stays under 0.1 and D under 0.05.**
If H grows past half again or D past 0.05, the hand-offs are processor contention after all and the
reading is wrong. Whether that held-out reading is taken, and what each is worth, is the reviewer's.

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
- **Outcome:** pending

### P16-04 — The tail read by the corrected rule

- **Task:** On the reviewer's answer, fresh runs after the rule's commit, generator separate: five traced (one write in ten) and five untraced at 3,125 writes a second, interleaved; and, if the reviewer takes it, the same beside one busy process; the rule applied to the traced runs each within 1.5 times the untraced of their condition, each branch's reading checked against its part before it is reported, and in the held-out condition each part's direction against the one its branch predicted. No remedy built; the reading names the next subject, and says what each reading is worth as the reviewer decided.
- **Vacuity:** A reading taken from runs beyond the threshold reads the trace again; guarded by the per-run threshold, checked first, from the records. A reading of a resample presented as a test of the rule; guarded by decision 4's statement of what each reading tests, carried into the outcome.
- **Sabotage:** ; manual: the threshold against each traced run, the branch's reading against its part, the held-out directions against the rule's
- **Verifiable here:** partial — measured here; whether the rule is the right one is the reviewer's
- **Prediction:** At the operating point the rule's numbers select the hosts' thread hand-offs, reader and sender together holding more than half of the tail's excess, and its reading holds against the parts; the persists hold 0.15 to 0.25, the generator's lateness under 0.05. Beside one busy process the reader's and the sender's parts grow by more than half again in share while delivery stays under 0.05, as a processor-contention reading predicts. **Observable:** the records and the rule.
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
