# Phase 16 — report

**Status: for review.**

The leader's hand-offs, measured apart; the tail rule restated after them; the harness keeps each
entry's output (spec §11 phase 16, P16-00's row): P16-00 to P16-08 as approved, with decision 4
replaced at approval (the rule sent to the reviewer between P16-03 and P16-04) and branch 7's floor
moved by the reviewer's answer. Every number in this report's tables cites its record (`m:`) or its CI
run (`run:`). This report certifies the commit that contains it; `gates reports` checks that commit's
CI run on the next push. **It is pushed after two pushes of the phase's work.** `83750c0`
(run:38997459747): its head's jobs green but for one observer, fixed since, and its per-commit matrix
red at eleven commits the reviewer accepted as known-red (the third section). Then `5872758` alone,
the rule's floor and phase 15's superseded lines: a commit that changes a report must be a push's head
(P4-10), and the P16-04 records name it, so it could not be folded into this one. It is red however it
is pushed (a twelfth known-red, in the table): a commit that carries a report has its margin tables
judged, and phase 15's was stale with phase 16's audit not yet measured (run:39057523276: `build-core`'s
margins gate, twelve rows; its 13 shards, both soaks and Compose green). Phase 15's report is changed
again here, so this report's run certifies it.

## First: the trace was the larger term, a third time (P16-01, P16-02)

Phase 15 read the tail at 3,125 writes a second as the leader's hand-offs, client-to-leader 0.26 to 0.32
of the tail writes' excess, and this phase was built to split that segment at the kernel's receipt. The
split was built; what it found first was the trace. Phase 15's sampled trace kept every sampled stamp as
an object until the run ended. With the collector's pauses now in every load record, a traced run at
3,125 paused the hosts' process 125 to 133 ms against 38 to 46 untraced, with two collections of
generation 2 against none, and the leader's loop, 0.93 busy, turned each pause into a backlog many
times its length. The pauses landed on client-to-leader. With the stamps kept in arrays allocated at the
trace's start (`03cd5f7`), the traced tail is the untraced one and client-to-leader holds 0.03:

| At 3,125, generator separate | 99th percentile, untraced | traced | client-to-leader's 99th, traced | collector's pauses, traced (untraced) | records |
|---|---|---|---|---|---|
| phase 15's sampled trace, at `71a5acd` | 5.0 to 6.5 ms | 11.3 to 18.1 ms | 1.7 to 6.2 ms | 125 to 133 ms (38 to 46) | m:p16-02-gc-s10-r1-local-3125-1 to -r5, m:p16-02-gc-untraced-r1-local-3125-1 to -r5 |
| stamps in arrays allocated at the start, `03cd5f7` | 4.7 to 7.0 ms | 4.5 to 6.4 ms | 0.2 to 0.3 ms | 40 to 47 ms (38 to 43) | m:p16-02-gc2-s10-r1-local-3125-1 to -r5, m:p16-02-gc2-untraced-r1-local-3125-1 to -r5 |

Getting there took three changes, and one cause named before it was tested. The kernel reader as first
built cost its own share (13.2 ms against phase 15's trace's 9.4, five interleaved pairs,
m:p16-02-cost-*): the leader's loop was as busy under both, the difference was client-to-leader, and I
named the reader's pinned buffers in `0223f91`'s message; the next A/B moved nothing (16.2 against 7.8,
m:p16-02-cost2-*). Reading with one blocking `recvmsg` a line, as the stream reader waits, brought it
level with phase 15's trace (10.3 against 9.5, m:p16-02-cost3-*, `f81c74b`); both were then 1.8 times
the untraced, and the collector's counters (`71a5acd`) showed the cost beneath them. Phase 15's tail
reading is marked superseded where it sits, in its report and in the performance target.

This is the third time the project measured an instrument and found it the larger term: the
generator's `Task.Delay` schedule (P12-04, phases 10 and 11 superseded), the traced tail that was the
generator's lateness and then the trace's per-message cost (P14-04, P15-01), and now the retained
stamps (a finding, at the reviewer's direction). P15-01 had ruled the retained stamps out, rightly then:
the per-message cost was larger. With it removed they were the next term.

Measured apart, on P16-02's second set (four traced runs of five within 1.5 times at each rate,
m:p16-02-set2-*), **the hand-offs this phase split are not where the tail is:**

| At 3,125, the tail writes' excess | share, mean (range) | median | 99th percentile | records |
|---|---|---|---|---|
| the loop's queues (leader-queue alone) | 0.40 (0.19 to 0.57; 0.37) | 65 µs | 1.1 to 2.0 ms | m:p16-02-set2-s10-r1-local-3125-1 to -r5 (r3 beyond 1.5 times) |
| the pipeline's wait, before the append's emission | 0.18 (0.10 to 0.26) | 11 µs | 1.0 to 2.4 ms | m:p16-02-set2-s10-r1-local-3125-1 to -r5 (r3 beyond 1.5 times) |
| the persists | 0.16 | 294 µs (the leader's) | 0.9 to 1.2 ms | m:p16-02-set2-s10-r1-local-3125-1 to -r5 (r3 beyond 1.5 times) |
| the generator's lateness | 0.07 (one run 0.25) | 0.7 µs | 0.1 to 0.3 ms | m:p16-02-set2-s10-r1-local-3125-1 to -r5 (r3 beyond 1.5 times) |
| the reader | 0.03 (0.002 to 0.09) | 23 µs | 175 to 381 µs | m:p16-02-set2-s10-r1-local-3125-1 to -r5 (r3 beyond 1.5 times) |
| the loop's hold, emission to release | 0.01 | 28 µs | 125 µs | m:p16-02-set2-s10-r1-local-3125-1 to -r5 (r3 beyond 1.5 times) |
| the sender, release to socket | 0.000 | 1.5 µs | 9 to 17 µs | m:p16-02-set2-s10-r1-local-3125-1 to -r5 (r3 beyond 1.5 times) |
| the kernel's delivery | 0.000 | 3 µs | 15 to 25 µs | m:p16-02-set2-s10-r1-local-3125-1 to -r5 (r3 beyond 1.5 times) |

The sender runs on the loop's own thread: its channel completes the sender's wait inline, so the frame is
written within the executor's hand-off unless the sender is still busy with the last one.

## Second: the rule, its floor moved after disclosure, and the held-out reading falsified a mechanism (P16-03, P16-04)

The rule was written after P16-02's second set, committed (`8a55d6c`), pushed and sent before any run it
governs. Seven groups; branches 1 to 6 each a group holding half (P14-04's threshold); branch 7 two groups
together holding half, each above a floor; branch 8 none fitting. It went to the reviewer with branch 7's
floor at 0.15 and the statement that 0.15 was written knowing the pipeline's wait at 0.18, and that 0.2
would send P16-02's numbers to branch 8. **The reviewer set it at 0.2** (`5872758`): a threshold fitted to
the data it judges is the circularity of phase 13's allowance model, and the disclosure is what made the
rule usable. P16-02's composition then fits no branch.

The resample at the operating point was judged worth little, and the held-out condition, fixed at
approval before P16-02 existed, was taken: five rounds of a traced and an untraced run beside one busy
process, and the same without it as their baseline, interleaved (m:p16-04-*).

| At 3,125, traced, mean shares | Q | W | P | R | H | G | D | the loop busy | rule | records |
|---|---|---|---|---|---|---|---|---|---|---|
| P16-02, the rule's source | 0.40 | 0.18 | 0.16 | 0.16 | 0.03 | 0.07 | 0.00 | 0.94 | 8 | m:p16-02-set2-s10-r1-local-3125-1 to -r5 |
| P16-04, the operating point (resample) | 0.25 | 0.27 | 0.18 | 0.17 | 0.12 | 0.01 | 0.00 | 0.95 | 7: W, then Q | m:p16-04-plain-s10-r1-local-3125-1 to -r5 |
| P16-04, beside one busy process | 0.42 | 0.15 | 0.20 | 0.14 | 0.07 | 0.02 | 0.00 | 0.91 | 7 (Q, then P) on means; 8 run by run | m:p16-04-busy-s10-r1-local-3125-1 to -r5 |

**The predicted directions, beside the busy process against its interleaved baseline:** Q's share grew
(right); the pipeline's wait grew in microseconds, its mean 122 to 180 µs (right); H stayed under 0.1 and
did not grow, and D stayed at 0.00 (right: the hand-offs as split are not processor contention); **the
loop's busy fraction fell, 0.95 to 0.91, and the persists' share rose, where the loop's-queue reading
predicted a rise and a fall (wrong).** The loop did less work a write (278 µs of execution against 288,
in larger batches) and was less busy, while its queue grew (mean 185 to 245 µs, 99th percentile 1.6 to
2.4 ms): inputs waited for the loop's thread to be run, which the loop's own clock counts as waiting, not
behind a saturated loop's sync. **Branch 4's reading is wrong in its mechanism under contention**, and
branch 7's with it; the rule's groups put a thread hand-off, the reader's channel to the loop, inside the
loop's queues, where no part separates it.

Three more things the reading shows, each stated rather than smoothed:
- **P16-02's composition did not replicate** in the resample, the machine slower in those hours
  (untraced medians 1,056 to 1,459 µs against 1,055 to 1,129); the operating point's answer moves between
  branches 8 and 7 with the window, as the rule's disclosure warned;
- **the rule did not say whether a check is read on the runs' means or run by run**; beside the busy
  process the two readings differ (Q's check fails in one run, the loop 0.847 busy);
- **beside the busy process the traced runs' 99th percentile was below the untraced in all five** (0.33 to
  0.75 of the untraced median; the untraced 16 to 19 ms in three runs of five, the traced 5.4 to 12.2).
  The per-run gate bounds the traced side only from above, so it passes them; whether the traced host's
  reader (the kernel reader, one blocking `recvmsg` a line) behaves differently from the untraced host's
  stream reader under contention is not measured, and the held-out reading describes the traced host.

No remedy was built. The reading names the next subject: **leader-queue split at the loop thread's
wake**, the channel's hand-off to the loop apart from the wait behind a batch in its sync.

## Third: eleven known-red commits, and a check that ran too late (P16-06)

Drawing the harness's test-run classification out of the entry's run (`7b4d8ad`, P16-06) moved the line
after S-harness-6's hunk, and its patch stopped applying. I ran that commit with its own two sabotages
only, S-out-1 and S-patch-2; the per-commit patch check exists for exactly this, and it ran after the
push. The local run before the push runs the patch gate at the head only, where the fix (`83750c0`) had
already landed. The P16-01 commit then broke S-proc-4's context the same way (fixed in `03cd5f7`). The
reviewer accepted the commits as known-red: rewriting them would orphan the measurement records that
name them, and `7b4d8ad` is the cost A/B's base.

| Commit | What it is | Red in its per-commit jobs because | jobs red | run |
|---|---|---|---|---|
| `7b4d8ad` | P16-06, the harness's output | S-harness-6's patch; S-patch-1's baseline (the patch gate) | 3 | run:38997459747 |
| `bcba8fc` | P16-01, the instrument | the same, and S-proc-4's patch | 4 | run:38997459747 |
| `c125c6d` | P16-00's outcome | the same three | 4 | run:38997459747 |
| `3846e50` | P16-02's first set | the same three | 4 | run:38997459747 |
| `0223f91` | the reader's buffers | the same three | 4 | run:38997459747 |
| `f81c74b` | the blocking reader | the same three | 4 | run:38997459747 |
| `71a5acd` | the collector's pauses | the same three | 4 | run:38997459747 |
| `03cd5f7` | the arena (S-proc-4 restored) | S-harness-6's patch; S-patch-1's baseline | 3 | run:38997459747 |
| `27744b7` | the cost records | the same two, and S-ci-1's baseline once (a Gates test; unexplained, a register row) | 4 | run:38997459747 |
| `ef16618` | P16-02's second set | S-harness-6's patch; S-patch-1's baseline | 3 | run:38997459747 |
| `8a55d6c` | the rule | S-harness-6's patch; S-patch-1's baseline | 3 | run:38997459747 |
| `5872758` | the floor at 0.2; phase 15's superseded lines | pushed alone as its own head; it carries a report, so the margins gate judges phase 15's table, twelve rows stale since the host changed (phase 16's audit came after) | its head's `build-core` | run:39057523276 |

All in run:38997459747, 40 of its 182 per-commit jobs. *Rule, from this:* a commit that changes a file a
sabotage patch names runs the patch gate at that commit before the next commit; checking a commit with
its own sabotages only does not see the patches it breaks.

## The result of the phase

### The spec (P16-00)

`011c044` writes §11's phase-16 row; no other section changes, and `docs/design/node-interface.md` is
unchanged through the phase: the arrival and release stamps are bench-mode observation.

### The instrument (P16-01)

A traced host reads its clients' lines with `recvmsg` and the kernel's receive time (`SO_TIMESTAMPNS`),
the socket made blocking, each line given its read's timestamp when the read ended at its newline (the
kernel restamps a queued segment when later data joins it; the bench's generator keeps one write in
flight a connection, so a line is alone), on the trace's clock by the offset read at each line. A frame
handed to its peer's queue is stamped `Released`, joined to its `Sent` by the frame. The decomposition
splits client-to-leader into delivery and the reader and leader-to-send into before the emission, the
loop's hold and the sender, counts arrivals and releases outside their segments, and gives each part's
tail share. The sampled trace keeps its stamps in arrays allocated at its start, and every load record
carries the hosts' process's collector pauses. Guards: S-hand-1 (an arrival stamped at the reader's
return), S-hand-3 (the clock offset dropped), S-hand-2 (the release stamped in the sender; the release
control, a planted sender delay, moved the sender's part 1.01 times the delay waited and no other part
by more than 80 µs). In all 45 traced runs of the phase's hosts every joined write had its arrival and
release within its segment.

### Measured apart (P16-02) and read (P16-03, P16-04)

The first and second sections.

### S-ran-2 and the count file (P16-05)

S-ran-2's patch drops the test file from its project only; its command lowers the baseline line by the
file's test count and runs the count check. No commit after `5125336` touches it, though four count
changes followed, each of which would have broken the old patch. The loader refuses a patch or control
naming `ci/test-baseline.txt` (S-patch-2).

### The harness's output (P16-06)

An entry or control whose outcome is not the expected one is followed in the log by its run's last 80
lines (S-out-1); since `9c3fe8d` a failing baseline command keeps the same, after S-ci-1's baseline at
`27744b7` failed once on a Gates test whose assertion twelve lines had cut off.

### The margin audit (P16-07)

`scripts/margins.sh` at the last host change, `1f6c10d` (the header set by hand to the commit the audit
began at: the script writes the head at its end, and a documentation commit made while it ran was
folded into this report's commit), three modes, five runs a side
(`docs/phases/P16/margins-bench.tsv`). Every row keeps 1.5 times its bound on both sides. The release
control's new rows: the sender part's rise 1.81 times its lower bound and 2.97 inside its upper, the
largest other part 9.37 times under its bound; with S-hand-2 the sender part never reaches its bound and
the loop's hold crosses the others' by 2.28 times. No other row's unpatched median moved by more than
0.027 of its bound against phase 15's table.

### The phase's cost in CI (P16-08)

| | Phase 15's second push | This phase's push | runs |
|---|---|---|---|
| jobs (all with steps) | 100 | 204 (40 per-commit failures, known-red; the head's build on one observer) | run:38811415917, run:38997459747 |
| slowest head shard's harness step | 374 s | 328 s | run:38811415917, run:38997459747 |
| soak, soak-membership steps | 780 s, 771 s | 784 s, 857 s | run:38811415917, run:38997459747 |
| Compose job | 105 s | 97 s | run:38811415917, run:38997459747 |

The baseline soak's effect lines are phase 15's line for line (run:38843960353): Core and the simulator
are unchanged. The manifest goes from 364 live entries to 369: five added (S-hand-1 to S-hand-3,
S-patch-2, S-out-1), none retired.

## Decisions for you

1. **The tail at 3,125** (a row, open, unhomed): 5.4 ms untraced at its 99th percentile against the
   criterion's 5,051 µs (m:p16-02-set2-untraced-*), closer than any phase before it now that the trace
   no longer inflates it. The held-out reading points at the leader's queue under contention: the
   loop's thread woken late, not the loop saturated. Is splitting leader-queue at the loop's wake the
   next subject, or does a tail 7% over its criterion, with its largest part in §8's limit at the
   operating point, stand as reported?
2. **The rule's two defects** (P16-04): whether a group's check is read on the runs' means or run by
   run, and the reader's channel to the loop inside the loop's queues. Fix them in the rule before the
   next reading, or let the next subject's rule be written fresh?
3. **The traced host under contention** (P16-04): beside a busy process its tail was below the untraced
   host's in all five runs, a difference the per-run gate cannot see. Measure the kernel reader against
   the stream reader under contention before the next reading through the trace, or accept the reading
   as the traced host's?
4. **The unexplained baseline failure** (a row, open, unhomed): S-ci-1's baseline at `27744b7`, one
   Gates test, once. Its output is now kept; nothing more can be learned from this occurrence.

## Deviations and choices

- **The clock offset read at each line** (the tightest of three brackets), not once at the trace's start
  and checked at its end; the per-write causality count is the check (P16-01).
- **The release's control a class of its own** beside the bench controls, so their entries do not run
  it (P16-01).
- **The kernel reader rebuilt twice and the trace's storage once** (P16-01), each after an A/B; the
  collector's pauses added to every load record to measure the trace's cost where it is paid.
- **P16-02's first set superseded** by the second, at the instrument that no longer costs the tail.
- **The held-out reading taken with the operating point interleaved beside it** (P16-04), so each
  direction is read against runs of the same hours; the reviewer judged the resample worth little as a
  test of the rule, and it is reported as the baseline and as replication.
- **The busy process** is one shell spin loop on the host, the margin audit's busy mode with one, not
  three; the records' ids name the condition, their configuration does not.
- **A baseline command's failure keeps 80 lines** (`9c3fe8d`), extending P16-06 past entries, with no
  test of its own.
- **No push while a measurement ran**, as at phase 15; two gate runs of a few seconds each (`register`, `breakdown`) overlapped the margin audit's unpatched rounds.

## Done criteria

- **The hand-offs in the tail measured apart, each part guarded by a planted delay in it alone:**
  P16-01, P16-02; met (the reader's 20-ms delay in the reader and not delivery; the sender's in the
  sender and not the hold; the hold against the emission since P15-03).
- **The corrected rule written after the measurement, tests apart from readings, a branch for none
  fitting, readings checked against their parts, directions stated for the held-out condition, sent to
  the reviewer before the reading:** P16-03; met, with branch 7's floor moved by the reviewer and two
  defects found in use.
- **The tail read by it on the reviewer's answer, from traced runs each within 1.5 times the untraced at
  the same rate and condition, what each reading is worth stated, no remedy built:** P16-04; met; the
  held-out reading falsified the loop's-queue reading's mechanism.
- **The harness prints an entry's output for any outcome it did not expect:** P16-06; met, extended to
  baseline commands after the first unexpected result it did not cover.
- **S-ran-2's patch carries no count; the loader refuses a patch or control naming the count file:**
  P16-05; met.
- **Every bench control's margins measured again after the host's changes:** P16-07; met, the release
  control's rows added.
- **Every row promised to phase 16 closed:** the tail's row done for what was promised (the hand-offs
  measured apart), the tail itself carried by a new open row; the harness's output done.
- **The replication pipeline untouched:** met; no commit of the phase changes it.

## Predictions

**Evidence 8, forcing 1.** Right 4, partly 4, wrong 1.

- **P16-00, right.**
- **P16-01, partly:** the timestamp, the joins and the release control as predicted; the instrument as
  first built cost the tail, and the trace beneath it more.
- **P16-02, partly:** the reader holds all of client-to-leader's share and delivery none, but that share
  is 0.03; the sender nothing, the time before emission 0.18. Written from phase 15's composition, which
  was the trace's cost.
- **P16-03, right (forcing):** the procedure I then followed.
- **P16-04, wrong:** the hand-offs neither dominate nor grow beside a busy process; the held-out reading
  falsified the loop's-queue reading's mechanism instead.
- **P16-05, right.**
- **P16-06, partly:** the output kept and printed; the change reached past the reporting (S-harness-6's
  patch), and the first unexpected result was a baseline's, not an entry's.
- **P16-07, right.**
- **P16-08, partly:** no shard past 400 s, the soaks identical, every job given a runner, five entries
  added; 40 per-commit jobs red at the known-red commits and the head's build on the class-reach
  observer.

## What exists

- **P16-01:** `KernelArrival`, `KernelLineReader`; `HandOffTrace.Kind.ClientArrived` and `Released`,
  the sampled trace's arena and `Dropped`; `NodeHost.SenderDelay` and `SenderSpun`;
  `Decomposition.Parts`, `PartMicros`, `Within`, `Result.Arrivals` and `Releases`, `TailShares` over any
  names; `HostCounters.Collector` and the records' `process_gc_*`, `decomposition_arrivals*`,
  `decomposition_releases*`, `part_*`, `tail_share_part_*`; `KernelArrivalTests`, the decomposition's
  new tests, `ReleaseControlTests`; S-hand-1 to S-hand-3; `LocalCluster.TraceCapacity`.
- **P16-05:** `SabotageSpec.CountFile` and the loader's rule; S-ran-2's command; S-patch-2.
- **P16-06:** `Sabotage.Outcome.Output`, `EntryRun`, `ClassifyTestRun`, `Report`, `OutputFor`,
  `OutputLines`; `HarnessOutputTests`; S-out-1.
- **P16-07:** the release control's rows in `scripts/margins.sh`; `docs/phases/P16/margins-bench.tsv`.

## Register

Two rows were promised to phase 16, and both are done: the tail at 3,125 in the leader's hand-offs, for
what was promised (the hand-offs measured apart and the rule read, proven by the release control), and
the harness keeping each entry's output (`HarnessOutputTests`). Two rows were opened, both unhomed for the
reviewer: the tail at 3,125 as it now stands, and S-ci-1's unexplained baseline failure. The rate's limit
past the capacity stays open and unhomed, out of this phase's scope.

## Findings added this phase

In `docs/findings.md`, under phase 16: an instrument's cost can land on the part it measures, and a gate
on the total does not see where; a cause named in a commit before the test that could refute it (the
reviewer: a commit message is a claim); this project has measured its own instruments three times, and
each time the instrument was the larger term (the reviewer).

## Still the person's

- The walk, cold, once per phase (phase 0's row): not scripted; not done by me.
- Branch protection's required checks, as before.

## Commits never verified in CI

None. Every commit of the phase's first push ran in its own per-commit jobs (run:38997459747), and
`5872758` as the head of its own push; the red ones are the known-red commits above. The commits after
`5872758` run in this report's run.
