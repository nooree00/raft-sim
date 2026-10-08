# Phase 12 — task breakdown

Commit latency, then the group commit by the corrected gate (spec §11 phase 12, added at phase
11's acceptance; its done criterion is P12-00's to write, at this breakdown's approval). Format as
in `docs/phases/P11/breakdown.md`, parsed by `gates breakdown`; outcomes will say `(evidence)` or
`(forcing)`.

**Approved** (reviewer), every decision as proposed; stop and report after the phase. With the
approval:
- **P12-01 checks both directions:** a report that says its phase is complete while
  `docs/phases/status.md` does not, and `status.md` marking a phase complete with no certified report
  behind it, the second being how a phase gets closed without evidence and the one nobody would
  think to test. P12-01 also audits the other gates of the same shape, a gate reading a file kept by
  hand (the sabotage cost file, the test baseline, the patch set, and any other found): whether
  anything verifies each file is current, or each depends on someone remembering. Any that depends
  on remembering becomes a register row. The task below is amended accordingly, before it starts;
- **the status file's finding leads the phase report,** stated as a class: any gate reading a
  manually maintained file needs a check that the file is current, and the gate itself cannot be
  that check (the fourth instance in two projects: the test-count baseline, the sabotage cost file,
  the stale patches, the status file);
- **the target's gap is written down before the measurement** (`docs/design/performance-target.md`,
  phase 12's section, with P12-00): L has no term for the client's round trip or for software, which
  leaves about 140 µs for all of a commit's software at 1.5 L, a reason the target may be
  unreachable in principle;
- the headline prediction's shape is what the reviewer wants tested: if the generator's lateness is
  the largest segment and the corrected gate then selects a group commit, phase 13 is decided by
  measurement.

**Latency is next, and this phase is about it** (the reviewer asked that the breakdown say so, not
leave it to the register row). Phase 11 left the cluster at about ten times the design's latency
with nothing aimed at it: a median of 5.4 ms at 3,125 writes a second against the criterion's
543 µs, and about 3 ms below the knee against L = 362 µs. Nobody has yet measured where those
milliseconds go. The group commit waits behind latency, because it trades latency for throughput,
and is decided at the end of the phase by the corrected gate, on the curve this phase produces.

**Done when** (proposed for spec §11 by P12-00):
- **the latency of a commit decomposed** along its path, every segment measured from what happened
  (the generator's own lateness included), at a rate below the knee and at the criterion's rate, the
  segments summing to the end-to-end latency;
- **the instrument first:** the load generator's own lateness measured and bounded before any change
  to the host is judged;
- **the host's largest segments that are not the design's** (two syncs and a round trip) removed or
  explained, and the curve measured again against L, in process and in Compose: the latency
  criterion met, or the remaining gap attributed segment by segment, each remaining segment beside
  its measured floor;
- **the corrected gate applied to the new curve** (spec §2): the leader's sync utilisation at the
  lowest rate whose 99th percentile passes 100 ms in any run; at 80% or more, a group commit is
  proposed as phase 13 by an amendment in the report, not built here; under it, the row closes with
  the measurement naming the cap;
- **every control that compares a measured, non-deterministic quantity with a threshold has its
  margin measured on both sides** (the reviewer's ask at phase 11's acceptance), and any within the
  stated factor of its threshold is moved or redesigned before one survives.

Carried forward:
- **phase 11's acceptance:** the gate's premise was wrong, not its answer. It measured at the highest
  sustained rate; the behaviour that matters is at the rate where the tail breaks. Corrected in §2,
  and written precisely below (decision 6) before any phase-12 number;
- **"a sabotage whose effect sits at its check's threshold is caught only by chance"** (the
  reviewer's choice of phase 11's findings): check the other controls rather than wait for one to
  survive (P12-08). The 90% barrier guard was the CRDT project's §13.38 again, a remembered figure
  with no boundary attached;
- **a change to the node moves facts measured from the soak** (phase 11's findings, made mechanical
  by P12-02), and **a hand-kept status file let a gate go silent for four phases** (found at phase 11's
  acceptance, made mechanical by P12-01);
- **spec §10:** a rule tied to a mechanical act holds. Every new check here is a test or a gate, and
  every new measurement a record that `gates measurements` reads.

## Ordering

The spec amendment first (P12-00). Then two cheap process fixes, so this phase's own pushes use them:
completion read from the reports (P12-01), and the soak-derived entries selected when the node
changes (P12-02). Then the latency work in order: the decomposition (P12-03), the instrument's own
lateness bounded (P12-04), the host's segments cut by the decomposition's order (P12-05), the curve
measured again (P12-06), and the corrected gate on it (P12-07). Then the control margins (P12-08),
after the host has changed, because the host's changes move the bench controls' numbers. Then the
cost in CI (P12-09).

**Blocking set:** P12-00 to P12-08. P12-09 blocks nothing.

## Decisions for review

1. **Phase 12's done criterion** (P12-00), as above. The phase is done when the criterion is met, or
   when the gap is attributed: a segment that remains is shown at its floor by its own measurement
   (a thread hand-off timed alone, a loopback write timed alone), not argued.
2. **The decomposition: timestamps the host takes, not counters in the node** (P12-03). In bench
   mode only, each host records a timestamp (`Stopwatch.GetTimestamp`, the kernel's monotonic
   clock, comparable across processes on one machine, so in Compose too) at each hand-off a write
   passes: its line read from the client's socket, its input taken by the loop, its entry durable,
   the append carrying it written to a peer's socket; on the follower, the frame read, taken,
   durable, and the answer written; on the leader again, the answer read, taken, and the response
   flushed. The generator records the write's scheduled time, its send and its answer. Writes are
   joined to log indices by the host (the index its persist carried). Segments are differences of
   consecutive timestamps, so each write's segments sum to its latency exactly, and the means of the
   segments sum to the mean latency; the gap between the stamps the decomposition joins and the
   generator's end-to-end figure is reported, and must be under 5%. A planted delay in one segment
   must appear in that segment and no other (the control, S-lat-1). No timestamp or counter enters
   `RaftNode` (spec §9).
3. **The instrument first** (P12-04). The generator times each write from its schedule (avoiding
   coordinated omission, P10-03), so its own lateness in sending counts as latency. It waits with
   `Task.Delay`, whose granularity here is about a millisecond. Proposed: measure that lateness as
   its own segment first; if its median is over 50 µs, the generator waits on a dedicated thread,
   sleeping to within a millisecond and spinning the rest, with a control that its 99th percentile
   lateness stays under 100 µs on an idle machine (S-bench-4). The latency reported stays measured
   from the schedule.
4. **What may change: the host and the generator, not the protocol** (P12-05). Allowed: how the host
   moves a write between its threads and its sockets (a send written from the loop instead of
   queued to a writer task, a peer read on a dedicated thread, fewer hand-offs), and the generator.
   Not proposed: pipelining past one append in flight per follower (phase 11's decision 3), a leader
   persisting in parallel with its sends (Ongaro's thesis §10.2.1; it changes L itself, from 2S + R
   to about S + R, and the persist barrier the spec keeps, P10-05), and leader leases (§2). If the
   decomposition shows the protocol's own waiting as the largest segment, that comes back as an
   amendment to this breakdown, approved first. The order of change is the decomposition's: the
   largest segment that is not a sync or a network hop first, re-measured after each.
5. **The target stays as P11-04 wrote it**: a median within 1.5 L (543 µs) and a 99th percentile
   within 2.47 ms at 3,125 writes a second, and a sustained rate of at least C_design / 2. One gap is
   stated now, before any number: L has no term for the client's own round trip (one loopback round
   trip, 44 µs) or for any software between the hand-offs, so 1.5 L leaves about 140 µs for both.
   Whether that is reachable is what the decomposition says; the criterion is not moved inside the
   phase.
6. **The corrected gate, written before the curve** (P12-07). The breaking rate is the lowest rate
   on P12-06's curve whose 99th percentile passes 100 ms in any of its five runs. The gate is the
   leader's sync busy fraction there (syncs a second times the median sync time, as P11-06 measured
   it), the median of the five runs. At 0.8 or more, the report proposes the group commit as phase
   13 by an amendment, and nothing is built in phase 12; under 0.8, the register row closes by a
   spec change that names the measured cap.
7. **The control-margin audit** (P12-08, the reviewer's ask). In scope: every check whose verdict
   compares a measured quantity that varies from run to run with a threshold: the host's bench
   controls, the soak's rate floors (a dimension's share against its floor), the replication cost
   check's bound and backlog guard, and the harness's staleness factors. Out of scope: checks on
   seeded, deterministic runs (a pinned seed is caught or not, the same every run). Method: the
   measured value on both sides, unpatched and patched, over at least five runs here, with GitHub's
   ratio to this machine (1.1 to 1.5, phase 7) applied to timings. Rule: a check whose value on
   either side comes within a factor of 1.5 of its threshold in any run is redesigned, or its
   threshold moved to where both sides have that margin, in this phase. A script prints the table,
   and a sabotage proves it flags a threshold set on a control's own unpatched value (S-margin-1).
8. **Two process fixes from phase 11** (P12-01, P12-02).
   - **Completion read from the reports:** `gates register` fails when a phase's report says it is
     accepted and `docs/phases/status.md` does not mark it complete, with P0 the one written
     exception (its row is the person's walk).
   - **The soak-derived entries selected when the node changes:** a push that changes a file under
     `src/Raft.Core/` also selects, in the touched-file stage, every entry whose target class runs
     the soak's executions or names a seed measured from them (the soak, membership-soak and Budget
     classes). It would have selected S-soak-6 at `af62a4a`.

## Tasks

### P12-00 — The spec amended: phase 12's done criterion

- **Task:** Decision 1. Spec §11's phase-12 row gets the done criterion above in place of the placeholder phase 11's acceptance left; §9 gains the sentence that lets the host take timestamps at its hand-offs in bench mode (a measurement of what happened, as the sync timing of P11-06 is), with none in `RaftNode`. The register's latency row cites the amendment.
- **Vacuity:** An amendment the breakdown gate does not read changes nothing that is checked. Guarded by the done criterion being checked at the phase report against the amended row, by the reviewer.
- **Sabotage:** ; manual: the reviewer reads the amended row against this breakdown's done criterion
- **Verifiable here:** partial — the text here, its approval the reviewer's
- **Prediction:** The amendment changes §11 and §9 and nothing else: §4's description of the host loop holds until P12-05 chooses its changes, and P12-05, not this task, will be the one to change it. **Observable:** the sections P12-00's commit changes.
- **Outcome:** right (evidence) — the amendment changed §11 (the phase-12 row's done criterion, in place of the placeholder) and §9 (the host's hand-off timestamps as a reading with no state to derive from, none inside `RaftNode`), and no other section: §4's description of the host loop is untouched, and stays P12-05's to change if its changes need it. Outside the spec, as the task said, the register's latency row cites the row; and, at the reviewer's direction with the approval, the performance target gained its phase-12 section, the gap in L written before any measurement.

### P12-01 — Phase completion checked against the reports, both ways; the other hand-kept inputs audited

- **Task:** Decision 8, amended at approval. `gates register` compares `docs/phases/status.md` with the reports both ways. An accepted report whose phase is not marked complete fails. A phase marked complete whose report is missing, or does not say it is accepted, fails too. A missing report is one `gates reports` never sees either, since it certifies the reports that exist, so a phase closed with no report would pass every gate today. P0 is the one exception, written in the gate with its reason: accepted, its row the person's walk. Then the audit: for every gate that reads a file kept by hand (the sabotage cost file, the test baseline, the patch set, and any other found), whether anything verifies the file is current or it depends on someone remembering; each that depends on remembering becomes a register row. Phase 11's acceptance found `status.md` unmaintained since phase 7: four phases unmarked, so the check on open rows promised to completed phases could not fire, and the resends row stayed open after its phase said it closed.
- **Vacuity:** A check that reads a status line nobody writes passes everything. Guarded by a test on today's reports (every accepted one parsed as accepted), by S-status-1 (the first direction removed: a phase accepted and unmarked passes) and by S-status-2 (the second removed: a phase marked complete with no report passes).
- **Sabotage:** S-status-1, S-status-2
- **Verifiable here:** yes — the gate and its tests run locally
- **Prediction:** Run against phase 11's tree before its acceptance, the first direction fails on exactly the four unmarked phases and, once they are marked, on exactly one open row (the resends). P0 is the only exception it needs. The second direction finds nothing on today's tree: every phase marked complete has an accepted report. The audit: the test baseline and the patch set are verified current on every run, mechanically (the count gate compares the baseline with the executed counts; the patch gate applies every patch); the cost file only coarsely (an entry beyond a factor of 5, a shard beyond 2), so a line wrong by less, as S-sess-3's was at 2.9 times, depends on someone refreshing it, and that is a row. At least one more input depends on remembering: the touched-file stage's rate per entry (`ci/sabotage-touched.txt`, 13.7 s, against 5.7 s measured at phase 11's main push), which nothing compares with a run. **Observable:** the gate's output on `47a9d91` with the check added and on today's tree; the audit's table, file by file.
- **Outcome:** partly (evidence) — the check found what it was for, and I miscounted what it would find on phase 11's tree. Run with phase 11's files from before its acceptance (`47a9d91`: the status file, the reports, the register, the spec), the first direction fails on **three** phases, P8, P9 and P10, not the four predicted: at that commit phase 11's report still said "for review", so it was not yet a phase the file failed to mark. With those three marked, no open row fails, because the resends row was promised to P11, which was not complete; marking P11 complete then trips both directions at once: the second (its report not yet accepted) and the resends row (open, promised to a phase marked complete). So the row could not have been caught before acceptance, only at it, which is when it was. P0 is the only exception needed (right), and the second direction finds nothing on today's tree (right). **The audit, file by file:** the test baseline (`gates testcount` compares it with the executed counts on every run, both ways: a project missing from it, or listed with no results, fails) and the patch set (`gates patches` applies every patch and control on every run, and the harness then needs each caught) are current by construction, as predicted. The cost file is checked only beyond a factor of 5 and 30 s per entry and 2 per shard, so a line wrong by less depends on someone refreshing it, as predicted: a row, with S-sess-3's 2.9 times as the instance that mattered, because the costliest line prices the capacity check's placeholders. The touched stage's rate per entry is checked by nothing, as predicted: 13.7 s against 12.9, 5.7 and 24.1 s measured at phase 11's three pushes. **Two more than predicted:** the Budget tests' hardest seeds (named by hand from a soak run, and nothing checks they are still the hardest; S-soak-6 surviving was the only signal) and a measurement record's commit field (checked for presence, not for naming a commit on the branch; the reword left ten records pointing off it). Four rows. The rest of the inputs found are checked against what they describe: the known limits by the soak, the image and SDK pins by preflight, the shard size by the capacity check. S-status-1 and S-status-2 are caught.

### P12-02 — The soak-derived entries selected when the node changes

- **Task:** Decision 8. In the touched-file stage, a push that changes a file under `src/Raft.Core/` also selects every entry whose target class runs the soak's executions or names a seed measured from them. The plan line says how many it added and why. Phase 11's S-soak-6 survived on GitHub because neither its patch nor its target named the node.
- **Vacuity:** A rule that matches no entry selects nothing and passes. Guarded by a test that a change to `RaftNode.cs` selects S-soak-6 (S-touch-1, the rule removed) and one that a change elsewhere does not add them (S-touch-2, the rule matching every path).
- **Sabotage:** S-touch-1, S-touch-2
- **Verifiable here:** yes — the selection and its tests run locally
- **Prediction:** The rule adds 25 to 40 entries to a push that changes the node, about a tenth of the manifest, and under 8 minutes of the touched stage at the recorded costs. On `af62a4a`'s diff it selects S-soak-6 and S-sess-3, and P11's main push would have gone from 67 entries to under 100, still under a third of the manifest. **Observable:** the selection on the diff `58bed2b..af62a4a` and on `58bed2b..254cd0a`, with its estimate.
- **Outcome:** partly (evidence) — the rule selects what it was for, and adds far fewer entries than predicted. On phase 11's diffs, each against the manifest at its head: the resend fix (`58bed2b..af62a4a`) selects 65 entries by file and 10 more as soak-derived (S-kl-1 to S-kl-3, S-lin-2, S-pos-1, S-pos-2, S-pos-4, S-pos-5, S-sess-3, S-soak-6), 75 of 316; the main push (`58bed2b..254cd0a`) 67 by file and the same 10, 77 of 318 (24.2%), estimated 17.6 minutes. **Right:** S-soak-6 and S-sess-3 are selected at `af62a4a`; the main push stays under 100 entries and under a third of the manifest; the addition is under 8 minutes (10 at 13.7 s, about 2.3). **Wrong:** 10 added, not 25 to 40. The five soak-derived classes hold 21 entries, and on a push that changes the node 11 of them are already selected by file, because their patches name the node or the soak's own files. **A limit, for the reviewer:** the rule fires on the node only, as decided; the simulator (`src/Raft.Simulation/`) and the workloads (`RaftWorkload`, `SessionWorkload`, in `tests/Raft.Core.Tests/`) shape the soak's histories as much, and a change to them moves the same facts. Not added here, since the approved decision names the node. S-gate-1's patch, whose context the new `Select` changed, is regenerated in this commit with the same mechanism; S-touch-1 and S-touch-2 are caught.

### P12-03 — Where a commit's latency goes

- **Task:** Decision 2. The hosts and the generator record timestamps at each hand-off of a write, in bench mode only; the bench joins them by request and log index and writes, per rate, each segment's mean, median and 99th percentile into a measurement record. Measured in process and in Compose, five repetitions, at 625 writes a second (below the knee) and at 3,125 (the criterion's rate), before any change.
- **Vacuity:** A decomposition whose segments do not add up measures something other than the latency, and one that puts a delay in the wrong segment points the work at the wrong place. Guarded by the segments' means summing to the generator's mean latency within 5% (S-lat-2, a stamp taken at the wrong point; *amended at P12-03:* the sum holds by construction, since the segments telescope, so it is reported but guards only that the span is the generator's latency; S-lat-2 is a wrong join, guarded by content, see the sabotage list), and by a control: a delay planted in the follower's hand-off from socket to loop must appear in that segment, by its planted amount within 20%, and in no other (S-lat-1, the delay attributed to the wrong segment). *Amended after P12-03, before the push:* the control's band is half to twice the planted amount, not within 20%, and the planted amount is the time the delays actually took. The ±20% band failed unpatched in the sabotage harness's baseline at 1.22 (alone 1.05, beside three busy processes 1.04): on a crowded machine the spinning readers take processors from the loop they hand to, so the segment rises by more than the spin. A ±20% band around an unpatched value of 1.0 also sits within P12-08's factor of 1.5 on both sides by construction. With S-lat-1 the ratio is 0.05 to 0.07, a factor of 7 or more under the new floor; every other host segment is still held under a quarter of the planted amount.
- **Sabotage:** S-lat-1, S-lat-2
- **Verifiable here:** yes — the hosts, the generator and Compose run here
- **Prediction:** At 625 writes a second in process, the design's segments (the two syncs and the two network one-ways between leader and follower) are under a quarter of the median, under 0.75 ms of about 3 ms. The generator's own lateness is the largest single segment, 0.4 to 1.2 ms, because it waits with `Task.Delay` at about a millisecond's granularity. The host's hand-offs between a socket's reader, the loop and a socket's writer, five on a commit's path (the client's line to the leader's loop, the leader's loop to its writer, the follower's reader to its loop and its loop to its writer, the answer's reader to the leader's loop), are together over 0.5 ms. At 3,125 a second a new segment appears that the low rate barely has: the leader's wait for an outstanding append (phase 11's one in flight per follower), at least 0.3 ms at its median. **Observable:** each segment's mean and median, per rate, in the records.
- **Outcome:** partly (evidence) — the decomposition joins every write, and the generator's lateness is the largest segment at 625 writes a second in process and in Compose; two magnitudes fall outside their ranges, and at 3,125 the largest median segments are queues the prediction did not name. The records are `p12-03-local-*` and `p12-03-compose-*`, five runs at each rate, all at `a65be9a`: every write joined in all twenty, no causality violation, no mismatched join, the stamps' span within 0.06% of the generator's latency. Medians of the five runs' medians, in µs, in process at 625 (end to end 3,136): generator-late 1,459, leader-queue 393, leader-persist 356, follower-persist 321, leader-to-send 145, response-to-client 59, client-to-leader 57, follower-to-send 57, network-back 43, network-out 43, leader-queue-answer 29, follower-queue 25, commit-respond 16. In Compose at 625 (3,796): generator-late 1,548, leader-queue 386, leader-persist 363, follower-persist 346, leader-to-send 325, response-to-client 138, network-back 124, network-out 112, client-to-leader 106, follower-to-send 72, commit-respond 51, leader-queue-answer 45, follower-queue 37. In process at 3,125 (6,367): leader-queue 1,699, leader-queue-answer 1,356, generator-late 1,302, leader-to-send 1,264, follower-persist 359, leader-persist 194, commit-respond 106, response-to-client 85, follower-to-send 67, client-to-leader 64, network-out 56, network-back 49, follower-queue 30. **Right:** at 625 the generator's lateness is the largest segment, 47% of the median in process and 41% in Compose; the design's segments (the two syncs and the two one-way hops) are under a quarter of the median, 763 µs of 3,136 in process (24%) and 945 of 3,796 in Compose (25%); the five host hand-offs together are over 0.5 ms, 649 µs in process (leader-queue 393, leader-to-send 145, follower-to-send 57, leader-queue-answer 29, follower-queue 25) and 865 in Compose; at 3,125 the leader's wait to send grows from 145 µs to 1,264 at its median, past the 0.3 ms predicted. **Wrong:** the generator's lateness is 1.46 ms at its median in process (1.55 in Compose), not 0.4 to 1.2; the design's segments are 0.76 ms in process, not under 0.75; and at 3,125 the two largest median segments are the leader's loop queue for client requests (1.70 ms) and for followers' answers (1.36 ms), which the prediction did not name. The loop runs its persists and their syncs inline, and at 3,125 it spends 0.64 of its time in syncs (the leader's sync busy fraction, median of five; 0.22 at 625), so every input waits behind them. The segment that was predicted cannot be split here into the wait for an outstanding append and the hand-off to the writer: both lie between the same two stamps. **Found, for the instrument (P12-04):** (1) The generator sends early as well as late: it skips its wait when under a millisecond remains, so 18% of writes at 625 and 21% at 3,125 go out before their time in process (13% and 20% in Compose; medians of five). Its segment is signed for that reason, and causality is checked from the send on. (2) Its segment also holds the wait for a free connection, and the generator keeps at most 64 writes outstanding. In a first set of these runs (discarded: its commit was rewritten before the push to repair a stale patch, see the findings), Compose's fourth run at 3,125 completed 2,465 writes a second, the backlog waited there, and the segment's median was 1.03 s: the system's saturation recorded as the generator's lateness. P12-04 splits the two, the generator's own lateness (its dispatch against the write's time) and the wait for a connection. (3) The trace costs little at the median and much in the tail. In process, interleaved, five runs each (`p12-03-trace-ab-*`), traced against untraced: the median of the medians is 3,291 against 3,235 µs at 625 (+1.7%) and 6,499 against 6,076 at 3,125 (+7.0%); the 99th percentile is 23.5 against 6.5 ms and 65.0 against 32.0 ms, and the mean 3,526 against 3,098 µs and 9,971 against 6,918. The decomposition is therefore read by its medians, as P12-05's rule is written; its means and tails are a traced system's, not the system's, and the curve (P12-06) is measured untraced. (4) The approved guard on the segments' sum could not fail (S-lat-2 amended, in the sabotage list). The control, run locally twice: follower-queue rose 528 and 524 µs for 500 planted, every other host segment under 60 µs either way; with S-lat-1, 24 µs (0.048 of the planted amount).

### P12-04 — The generator's own lateness bounded

- **Task:** Decision 3. If P12-03 measures the generator's median lateness over 50 µs, the generator schedules its writes on a dedicated thread that sleeps to within a millisecond of each write's time and spins the rest; its lateness is recorded with every load record. A control asserts its 99th percentile lateness under 100 µs at 3,125 writes a second on an idle cluster. If under 50 µs already, the task records that and changes nothing. *Amended at P12-04:* the control asserts that no write is dispatched before its time and that the median lateness is under 100 µs, against a server that answers every write at once; the 99th percentile is held by the records, each of which carries it. Two measurements moved it. Against the in-process cluster the tail is the collector's: the generator shares a heap with the three hosts, and its 99th percentile was 1.6 to 3.9 ms in the test process, about one write late per 0.3 ms of pause. And the tail needs two free processors: beside 0, 1 and 2 busy processes on four, the 99th percentile was 6 to 20 µs; beside 3 it was 2.3 to 2.8 ms, while the median stayed under 1 µs throughout. The sabotage harness runs four workers on GitHub's four processors, so a bound on the tail would be decided there by the neighbours. *Amended again at P12-04, the mechanism:* the schedule sleeps on the kernel's clock until 80 µs before each write's time and spins the rest, instead of sleeping only while two milliseconds remain. The first version spun through every interval above 500 writes a second, and in Compose at 3,125 its container took 100 to 120% of a processor; interleaved there against the old generator and the sleeping one (`p12-04-paced-compose-ab-*`), it had the worst median and tail of the three. A sleep to within 20 µs, tried before either, woke too late too often (a 99th percentile of 49 and 177 µs alone). *Amended a third time, after the first push:* the control's median bound is 5 ms, not 100 µs. On GitHub the unpatched control's median lateness was 569 µs in the sabotage harness (run 37785921243), the machine saturated by four workers, as locally beside four busy processes (0.9 ms). What the control holds on any machine is that no write is dispatched early, which the Task.Delay generator breaks for a quarter of its writes (S-bench-4); the lateness itself is the machine's load and is held by the records.
- **Vacuity:** A generator that sends on time and then measures from its send has coordinated omission back, and its latency looks better for the wrong reason. Guarded by the existing stall control (S-bench-1, still caught: latency measured from the schedule) and by S-bench-4 (the spin removed, the lateness control red).
- **Sabotage:** S-bench-4; shared: S-bench-1
- **Verifiable here:** yes — the generator and its controls run here
- **Prediction:** The change is needed: the median lateness is 0.3 to 1.0 ms with `Task.Delay`. After it, the 99th percentile lateness is under 50 µs at both rates, and the end-to-end median at 625 writes a second falls by the lateness removed, within 10%: the generator was inflating the latency phase 10 and 11 reported, by a third or more. **Observable:** the lateness distribution before and after, and the medians at 625 and 3,125 writes a second.
- **Outcome:** partly (evidence) — the change was needed and took two thirds of the median away, more than the lateness it removed; the tail bound was not reached. The generator now sleeps on the kernel's clock to 80 µs before each write and spins the rest (`85edc0a`); its first version, which spun through every interval (`c4506af`), was replaced after Compose showed it taking more than a processor (both amendments in the task above). **Right:** the change was needed (P12-03 measured the old generator's median lateness at 1.46 ms in process); it was inflating the latency phases 10 and 11 reported by a third or more: interleaved in process, five runs each (`p12-04-paced-ab-*`, against P12-03's head), the median of the medians fell from 3,279 to 1,239 µs at 625 writes a second (−62%) and from 7,510 to 1,943 at 3,125 (−74%), the 99th percentile from 6.5 to 5.4 ms and from 54 to 31 ms; in Compose, interleaved with both other generators (`p12-04-paced-compose-ab-*`), from 3,873 to 1,581 µs at 625 (−59%) and from 9,484 to 6,956 at 3,125 (−27%). The new generator's median lateness is 0.5 to 1.0 µs everywhere it was measured. **Wrong:** the old generator's median lateness was 1.46 ms, not 0.3 to 1.0; the 99th percentile lateness is not under 50 µs at both rates: in process 43 to 67 µs at 625 and 0.7 to 1.1 ms at 3,125 (the collector's pauses, which the generator shares with the three hosts), in Compose 71 to 136 µs and 0.2 to 2.1 ms (the generator's container beside three busy nodes on four processors); and the median at 625 fell by 2,040 µs, 1.40 times the 1,459 µs of lateness removed, not within 10% of it (in Compose 2,292 µs, 1.48 times 1,548). The difference is the host: the old generator released writes in bursts, and a burst queued in the leader. In process at 625 (`p12-04-paced-local-*` against `p12-03-local-*`, medians), the leader's queue for client requests fell from 393 to 40 µs and its wait to send from 145 to 69. **Found:** (1) The client's hop to the leader rose, from 57 to 112 µs in process (in Compose it fell, from 106 to 87): with writes arriving one at a time, each read of a client's line wakes a pool thread that a burst used to find awake. It is now the largest host segment at 625, and P12-05 starts there. (2) A spinning schedule perturbs what it measures when the cluster shares the machine: in Compose at 3,125 the spinning generator had a median of 8.7 ms and a 99th percentile of 474 ms, against 7.0 and 86 for the sleeping one and 9.5 and 184 for the old one (five runs each, interleaved), its container at 100 to 120% of a processor. (3) At 3,125 the leader's loop queues remain the largest segments in process (requests 473 µs, answers 547 µs, the wait to send 347 µs at their medians), behind syncs that keep it 0.77 busy.

### P12-05 — The host's hand-offs cut, by the decomposition's order

- **Task:** Decision 4. The largest host segment that is neither a sync nor a network hop is changed first, re-measured, and then the next, while any such segment's median exceeds 50 µs at 625 writes a second or a change is left that the decomposition says is worth more than 50 µs. Each change keeps the persist barrier (a send or a response leaves only after every persist before it is durable) and passes the host tests, the bench controls, the fsync tests and the Compose run unchanged. A change to the protocol is not in this task: it comes back as an amendment.
- **Vacuity:** A faster host that sends before its persist is durable would look like a latency fix. Guarded by S-hostdisk-1 (the barrier sabotaged, still caught), and by the decomposition's control (S-lat-1) still attributing a planted delay to its segment after each change.
- **Sabotage:** ; shared: S-hostdisk-1, S-lat-1; manual: each change's segment re-measured before and after, in the records
- **Verifiable here:** yes — the host and its tests run here
- **Prediction:** Two changes cover most of it: writing a node's sends from its loop instead of through a channel to a writer task, and reading each peer on a thread of its own instead of the pool. After them the host's hand-offs fall from over 0.5 ms to under 0.2 ms at 625 writes a second, and the in-process median there falls under 1 ms. What remains above the design is mostly thread wake-ups the loop's single-threaded design keeps, each tens of microseconds. **Observable:** each segment's median before and after each change, and the end-to-end median at 625 writes a second.
- **Outcome:** pending

### P12-06 — The curve measured again against L

- **Task:** Decision 5. Phase 11's curve measured again (in process and in Compose, five repetitions at each rate, the same rates), with the decomposition at 625 and 3,125 writes a second, against the target as written: the median, the 99th percentile and the sustained rate, each beside L. The inputs (S, R) measured again on the day.
- **Vacuity:** A curve measured on a quieter day than phase 11's would credit the machine. Guarded by an A/B in one session, phase 11's head against this phase's, interleaved (`bench.sh load-ab`, as P11-05), and by the inputs measured again.
- **Sabotage:** ; shared: S-bench-1, S-bench-2; manual: the A/B in one session, interleaved
- **Verifiable here:** partial — in process and Compose here; GitHub's runners are not the measuring machine
- **Prediction:** In process, the median at 3,125 writes a second falls from 5.4 ms to between 0.6 and 1.5 ms: the median criterion (543 µs) is still missed, by under three times. The 99th percentile criterion (2.47 ms) is met in process and missed in Compose. The highest sustained rate rises above 4,000 a second, because each write costs the leader's loop less. Compose stays at least 0.5 ms above in-process at every rate below the knee (its network crosses a bridge). **Observable:** the curve's records, in process and in Compose, and the A/B.
- **Outcome:** pending

### P12-07 — The corrected gate on the new curve

- **Task:** Decision 6. From P12-06's records: the breaking rate (the lowest rate whose 99th percentile passes 100 ms in any of its five runs), and the leader's sync busy fraction there, the median of the five. At 0.8 or more, the report proposes the group commit as phase 13 by an amendment; under 0.8, the register row closes by a spec change naming the cap the measurement finds.
- **Vacuity:** A gate read at a rate the curve does not resolve (the tail breaking between two measured rates) can be argued either way. Guarded by the rule's being written now, and by measuring a rate between the last sustained one and the breaking one if they are more than 25% apart.
- **Sabotage:** ; manual: the rule's text in decision 6 applied as written, to records committed before the reading
- **Verifiable here:** partial — the reading is mechanical, here; that the rule was applied as written is the reviewer's to check
- **Prediction:** The gate selects the group commit: at the new breaking rate the leader's sync is over 0.8 busy, because with less host cost per write the cluster reaches rates where the leader's one sync per write is most of its time. **Observable:** the breaking rate and the sync busy fraction there, from P12-06's records.
- **Outcome:** pending

### P12-08 — Every threshold control's margin, measured on both sides

- **Task:** Decision 7. A script lists every check in scope, with its threshold, its unpatched value and its patched value over at least five runs (GitHub's ratio applied to timings), and flags each within a factor of 1.5 of its threshold on either side. Every flagged check is redesigned or its threshold moved, measured on both sides again, in this phase. Run after P12-05, whose changes move the bench controls.
- **Vacuity:** An audit that reads only the unpatched side repeats phase 11's error (the stall control was calibrated on that side alone). Guarded by both sides in every row, and by S-margin-1 (a control's threshold set on its own unpatched median, which the script must flag).
- **Sabotage:** S-margin-1
- **Verifiable here:** partial — every run here; GitHub's spread only through its ratio and the CI runs' annotations
- **Prediction:** Two checks are flagged besides the two phase 11 fixed. The first is S-bench-2's slowdown control: its unpatched rise fell to 1.497 against a floor of 1.5 once on GitHub. The second is one soak dimension other than the two declared ones, within a factor of 1.5 of its floor: the fix made rare paths rarer, and the floors have not been read since. The replication cost check is not flagged: its patched side is hundreds of times over its bound and its unpatched side about half of it. **Observable:** the script's table.
- **Outcome:** pending

### P12-09 — The phase's cost in CI

- **Task:** The harness shards, the soaks and the per-commit matrix on the phase's first-attempt runs against phase 11's, the per-commit job count per push and whether every job was given a runner, and the never-verified commits tabled, failed apart from never ran.
- **Vacuity:** A run with a job that never ran compares less work. Guarded by counting only jobs with steps and stating any that never ran.
- **Sabotage:** ; manual: the comparison checked against each run's job list
- **Verifiable here:** no — CI's numbers only in CI
- **Prediction:** The phase adds under ten harness entries and moves no shard past 400 s, against phase 11's slowest of 361 s. The touched stage grows for pushes that change the node (P12-02) and stays under its threshold for the others. Every per-commit job is given a runner. **Observable:** the shards' slowest step, the entries added, and the job counts of the phase's runs.
- **Outcome:** pending

## Sabotage ids

New series: S-lat (P12-03), S-touch (P12-02), S-status (P12-01, two), S-margin (P12-08); S-bench-4 follows
S-bench-3. Each id's `sabotage/<id>/` entry lands in the same commit as the check it proves and is
run on that commit before it is pushed.

- **S-status-1:** the gate no longer compares accepted reports with the status file (a phase accepted and unmarked passes).
- **S-status-2:** the gate no longer checks that a phase marked complete has an accepted report (a phase closed with no report passes).
- **S-touch-1:** the rule removed: a change to `RaftNode.cs` no longer selects S-soak-6.
- **S-touch-2:** the rule matching every path: a change to a document selects the soak's entries.
- **S-lat-1:** the planted delay attributed to the segment after its own (a stamp moved one hand-off on).
- **S-lat-2:** each write joined to the next log index, so every segment reported is of another write's entry. *Amended at P12-03:* the approved text was "one stamp taken before the hand-off it closes, so the segments no longer sum to the latency", and it cannot be caught that way. The segments are differences of consecutive stamps, so they sum to the span from the first stamp to the last whatever the stamps in between are; a stamp at the wrong point moves time between two neighbouring segments (S-lat-1's shape), and a wrong join moves it to another write. The guard against a wrong join is content instead: every stamp the join reaches by index must be of an entry carrying the write's own command (the leader's and the follower's persisted records, the append written and the append read), and a write that fails it is counted and left out.
- **S-bench-4:** the generator waits with `Task.Delay` again, so its lateness control is red.
- **S-margin-1:** the audit script reads only the unpatched side, so a threshold set on a control's own unpatched median passes.

## Register rows

Promised here: commit latency, then the group commit by the corrected gate (the row opened at phase
11's acceptance). It closes with P12-06 and P12-07: latency by the criterion met or its gap
attributed, the group commit by the gate's reading. The rows promised to phase 0 (the person's
walk) stay the person's.
