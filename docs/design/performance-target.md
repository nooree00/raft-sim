# Phase 10's performance target

Written before any end-to-end number exists (P10-02, decision 2), from a model of what this design
costs and the model's inputs measured alone. Its numbers are not changed after the first
end-to-end record (P10-03 onwards). Every input cites its records (P10-01); the medians below are the
median over five repetitions of each record's median, and the ranges are across the repetitions.

## The model

A client write commits when a majority holds it durably. In this design (spec §4's persist
barrier), the leader appends the entry and syncs it **before** its `AppendEntries` is released, and a
follower syncs it before it acknowledges. On the critical path of one write:

1. the leader's sync, **S**;
2. the round trip to the fastest follower and back, **R**;
3. that follower's sync, **S**.

So the model's commit latency is **L = 2S + R**. The client's own request and reply add another round
trip; they are counted in the end-to-end latency P10-04 measures, but not in L, so the comparison
against the target is generous to the host by one round trip (about 47 µs).

Two capacities, because the breakdown's "the model's capacity" left open what one sync carries:

- **The design's capacity, C_design = 1 / S.** The leader issues one sync per client write (each
  request is its own input to Core, and the disk executor syncs each persist before the next input),
  so the leader cannot commit more than one write per sync. Followers are not the limit: one append
  carries up to `MaxEntriesPerAppend` (64) entries and one sync.
- **The disk's capacity, C_disk = 64 / S.** What this disk allows if a leader sync carried as many
  entries as a follower's already may (64). The design does not do this today (no group commit on
  the leader); C_disk is what the protocol could reach on this disk.

## The inputs, measured alone

| Input | Where | Median | 99th percentile | Records |
|---|---|---|---|---|
| S: a 96-byte append and its sync | the Compose data volume | 160 µs (154 to 171) | 410 µs (361 to 539) | m:p10-02-sync-volume-1 to m:p10-02-sync-volume-5 |
| S: the same | the container's `/tmp`, where in-process tests write | 163 µs (153 to 180) | 421 µs (388 to 527) | m:p10-02-sync-tmp-1 to m:p10-02-sync-tmp-5 |
| R: a 96-byte frame and its echo over loopback TCP | in one container | 47 µs (46 to 49) | 95 µs (88 to 107) | m:p10-02-rtt-1 to m:p10-02-rtt-5 |

The volume and `/tmp` are the same disk under two mounts here, and measure the same.

## The target

With S = 160 µs (its 99th percentile 410 µs) and R = 47 µs (95 µs):

- **Model latency:** L = 2 × 160 + 47 = **367 µs**; its 99th-percentile counterpart, the inputs'
  99th percentiles composed the same way, 2 × 410 + 95 = **915 µs**.
- **C_design** = 1 / 160 µs = **6,250 writes a second**; **C_disk** = 64 / 160 µs = **400,000 writes a
  second**.

| Criterion | Target | Measured at |
|---|---|---|
| Median commit latency | at most 1.5 × L = **551 µs** | an offered write rate of C_design / 2 = 3,125 a second |
| 99th-percentile commit latency | at most 3 × 915 µs = **2.75 ms** | the same |
| Highest sustained write rate (completed within 5% of offered, the 99th percentile under 100 ms) | at least C_disk / 2 = **200,000 a second**; **ungrounded (phase 10 acceptance):** C_disk is the capability of a design whose leader sync carries a 64-entry batch (a group commit), which this design does not have. The design's own comparison is C_design (6,250) | offered rates from a tenth of C_design up past the point where completed falls below offered |

The same figures apply to the in-process cluster (S = 163 µs gives L = 373 µs and C_design = 6,135; the
difference is inside the repetitions' range, so one target serves both).

**What is expected, stated with the target (P10-04's prediction):** the throughput target is missed,
because the leader issues one sync per write, so the host is held near C_design, a sixty-fourth of
C_disk; the latency target is met at half of C_design. If that holds, a group commit on the leader is
the register row phase 10 opens and does not build (spec §2: no performance work before a measured
baseline).

**What the model leaves out:** the client's round trip (above); the state machine's apply and the
reply's path back; the host's event log (one JSON line per Core event, to standard output); garbage
collection; and any sharing of the four processors among three nodes and the load generator in one
container. Each is a reason the host may fall short of the model at low load. The latency target's
slack (1.5 times the median, 3 times the tail) is for them.

## Phase 11's target (P11-04)

Restated before any phase-11 end-to-end number (P11-04, decision 4), with the inputs measured again
on the day, because P11-05 compares within one machine and one session. The model is phase 10's. The
throughput criterion changes, and the reason is phase 10's acceptance: its 200,000 a second was half
of C_disk, the capability of a design whose leader sync carries a 64-entry batch, which this design
does not have. Every criterion below is a property of the design as built.

| Input | Where | Median | 99th percentile | Records |
|---|---|---|---|---|
| S: a 96-byte append and its sync | the Compose data volume | 159 µs (142 to 166) | 366 µs (287 to 408) | m:p11-04-sync-volume-1 to m:p11-04-sync-volume-5 |
| S: the same | the container's `/tmp` | 164 µs (156 to 168) | 381 µs (327 to 401) | m:p11-04-sync-tmp-1 to m:p11-04-sync-tmp-5 |
| R: a 96-byte frame and its echo over loopback TCP | in one container | 44 µs (37 to 49) | 93 µs (88 to 100) | m:p11-04-rtt-1 to m:p11-04-rtt-5 |

With S = 159 µs (99th percentile 366 µs) and R = 44 µs (93 µs): L = 2 × 159 + 44 = **362 µs**, its
99th-percentile counterpart 2 × 366 + 93 = 825 µs, and **C_design = 1 / S = 6,277 writes a second**.

| Criterion | Target | Measured at |
|---|---|---|
| Median commit latency | at most 1.5 × L = **543 µs** | an offered rate of C_design / 2, 3,125 a second (phase 10's point, 0.4% under 3,139) |
| 99th-percentile commit latency | at most 3 × 825 µs = **2.47 ms** | the same |
| Highest sustained write rate (completed within 5% of offered, the 99th percentile under 100 ms) | at least **C_design / 2, 3,139 a second** | the offered-load curve, 625 to 9,375 a second |

C_disk (64 / S, about 402,000 a second) is kept only as what a group commit on the leader would reach
on this disk; it is no criterion. Whether a group commit is built is decided by P11-06's rule, from
the curve P11-05 measures against these.

## Phase 12: what the target cannot see, stated before measuring (P12-00)

Written before any phase-12 number, at the reviewer's direction, so that it is not discovered later
as a residual. The target above is unchanged (P12's decision 5), and it may be **unreachable in
principle** on this design and machine, for a reason the model shows on its face.

L = 2S + R counts the leader's sync, the follower's sync and one round trip between them. It has no
term for two things every measured write also pays:
- **the client's own round trip** to the leader and back: one more loopback round trip, R again,
  44 µs on the inputs above;
- **every piece of software between the hand-offs:** the client's request read from its socket and
  given to the leader's loop, the append handed to a writer and written, the follower's read handed
  to its loop and its answer to its writer, the answer handed back to the leader's loop, and the
  response written. On this host each hand-off crosses a thread (P12-03 counts five on a commit's
  path), and a thread's wake-up is commonly tens of microseconds.

The median criterion is 1.5 L = 543 µs, which leaves 543 − 362 = **181 µs** above the model for both:
44 µs for the client's round trip and about **140 µs** for all the software of a commit, about 28 µs
a hand-off if there are five. Whether that is reachable is what P12-03's decomposition measures. If
the hand-offs' measured floors (each timed alone, P12's decision 1) sum to more than the margin, the
criterion cannot be met by changing the host alone, and the phase report says so, segment by segment,
rather than moving the criterion inside the phase.

## The target restated against the floors (phase 12's acceptance)

Restated at the reviewer's direction (phase 12's acceptance, decision 1). The median criterion above,
1.5 L = 543 µs, lies under the floor P12-06 measured along a commit's path, 524 to 939 µs: a target
unreachable in principle is not a target, and every phase measured against it would report a miss
that says nothing. Its slack factors (1.5 on the median, 3 on the tail) were set by hand at phase
10's decision 2, before any measurement. **They are the second of the target's figures to prove
ungrounded**, after phase 10's 200,000 a second (half of C_disk, a capacity the design does not
have). The reviewer's rule at this acceptance: every figure of the target not yet measured is
suspect until it is grounded; they are listed at the end of this section.

**The floor** is the commit's path with nothing else running, each part timed alone on the day
(P12-06, five repetitions each; medians of the repetitions' medians and 99th percentiles):

| Part | Median | 99th percentile | On the path | Records |
|---|---|---|---|---|
| A sync with two writers at once (the leader's and the follower's, on one disk) | 212 µs | 439 µs | 2 | m:p12-06-sync2-tmp-1 to -5 |
| A one-way loopback hop to a reader asleep on its own thread | 83 µs | 201 µs | 4: the client to the leader, the leader to the follower and back, the leader to the client | m:p12-06-hop-1 to -5 |
| A hand-off to a loop asleep on a channel | 61 µs | 168 µs | 3: the client's request, the follower's append, the follower's answer | m:p12-06-handoff-1 to -5 |
| (for reference) a sync alone; the warm round trip | 182 µs; 50 µs | 401 µs; 113 µs | — | m:p12-06-sync-tmp-1 to -5, m:p12-06-rtt-1 to -5 |

The floor's median is 4 × 83 + 2 × 212 + 3 × 61 = **939 µs**, and its 99th percentile, the parts'
99th percentiles composed the same way (as L's was), 4 × 201 + 2 × 439 + 3 × 168 = **2,186 µs**. The
hops and hand-offs are the cold ones because this design's threads sleep when idle; the warm sum,
524 µs, needs every hop to find its reader awake, which this design does not arrange.

**The allowance** is what the criterion's load adds to a path that is otherwise floor: the wait at
the one serial resource. The leader's loop runs the leader's syncs inline (spec §2), so at 3,125
writes a second it is a single server whose service time is at least the sync. Modelled as M/D/1
with the sync above (212 µs, utilisation 0.66), the wait's median is **127 µs** and its 99th
percentile **1,194 µs** (Lindley's recursion over 4,000,000 Poisson arrivals after 100,000
discarded, seed 12; the mean, 209 µs, agrees with ρD/2(1 − ρ), and a third of arrivals wait not at
all, as 1 − ρ says). Nothing is allowed for software beyond the floors: the host is held to the
floor plus the queue its own design forms.

| Criterion | Restated | Was | Why it changed |
|---|---|---|---|
| Median commit latency at 3,125 writes a second | at most 939 + 127 = **1,066 µs** | 1.5 × L = 543 µs | 1.5 was set by hand, and 543 µs is under the floor |
| 99th-percentile commit latency at the same rate | at most 2,186 + 1,194 = **3.38 ms** | 3 × 825 µs = 2.47 ms | 3 was set by hand; the floor's own tail is 2.19 ms |
| Highest sustained write rate | unchanged: at least 3,139 a second (C_design / 2) | — | not restated; its halving is set by hand (below) |

**Where it is grounded:** the in-process cluster, whose hops are loopback in one container, where
the floors were timed. Compose's hops cross containers, and no floor was measured for them, so no
Compose criterion is grounded; Compose is reported against this one with that said, until its hop
floor is measured (phase 13).

**Where the host stands against it** (phase 12's records, P12-06): in process at 3,125 a second,
the median 1.27 to 2.05 ms (1.40, the median of five) is about **1.3 times** the restated median,
and the 99th percentile, 21 to 40 ms, six to twelve times the restated tail; in Compose the median,
3.7 to 7.4 ms, is three to seven times it.

**Still set by hand, suspect until grounded:** the operating point, half of C_design; the
sustained-rate definition (completed within 5% of offered, the 99th percentile under 100 ms); the
sustained-rate criterion's half of C_design; composing percentiles by summing them (for parts
that do not stall together, the 99th percentile of the sum is well under the sum of the parts'
99th percentiles, so the restated tail is likely lenient); and the allowance's model, a single server whose service is the sync alone, when
the loop also reads requests and answers.

## Compose's criterion, from its own hop floor (P13-06)

Until phase 13 no Compose criterion was grounded: Compose's hops cross containers, and no floor had
been measured for them. P13-06 measured one: a cold one-way frame from one container of the node
image to a reader asleep on its own thread in another, on a bridge network of their own (Compose's
driver), timed from the writer's monotonic stamp (the two containers share one kernel, so one
clock), five runs of 10,000 frames a millisecond apart (m:p13-06-hop-compose-1 to -5): **110 µs at
the median and 219 µs at the 99th percentile** (medians of the runs'), against loopback's 83 and 201.

The criterion is the restated one with Compose's hop in place of loopback's, the sync with two
writers on a Docker volume in place of /tmp's (209 and 463 µs, m:p12-06-sync2-volume-1 to -5), the
same hand-off floor (61 and 168 µs) and the same allowance (127 and 1,194 µs, phase 12's
acceptance), as decision 8 of phase 13 set:

| Compose criterion at 3,125 writes a second | Floor | Allowance | Criterion |
|---|---|---|---|
| Median commit latency | 4 × 110 + 2 × 209 + 3 × 61 = 1,040 µs | 127 µs | **1,167 µs** |
| 99th-percentile commit latency | 4 × 219 + 2 × 463 + 3 × 168 = 2,305 µs | 1,194 µs | **3.50 ms** |

The same figures still set by hand apply to it, and are settled below (P13-08). **Where Compose
stands:** traced, at 3,125 writes a second its median was 3,294 µs on this phase's host (2,797 to
3,564, m:p13-05-traced-compose-3125-1 to -5), about 2.8 times the criterion; untraced, both this
phase's host and phase 12's were past their capacity on this machine at 3,125 in four runs of five
(m:p13-05-compose-ab-*), so Compose's highest sustained rate on this machine that day was 1,250 for
both (2,000 in four runs of five for this phase's host, two for phase 12's).

## The figures still set by hand: declared, tested, dropped (P13-08)

Phase 13 settled the figures phase 12's acceptance listed as suspect (decision 7, as the reviewer
set it): the operating point and the sustained-rate definition are conventions and are declared;
the percentile summing and the halving are arithmetic over measured things and were to be grounded
or dropped; the allowance's model stays with its sensitivity reported.

**Declared conventions.**
- *The operating point, half of C_design (3,125 writes a second).* A choice of what is tested: the
  load at which a design with one sync a write should still have headroom. Another choice tests
  something else (at 0.8 of capacity the queue, at a tenth the floor); nothing measured says which.
- *The sustained-rate definition: completed within 5% of offered, the 99th percentile under 100 ms.*
  A choice of what keeping up means here: 5% leaves room for a run's drain, 100 ms is an interactive
  bound. Near the knee the reported rate moves with the choice: on phase 12's host the 99th
  percentile went from 26 ms at 3,125 to 114 ms at 4,000 (m:p13-05-ab-base-*).

**The percentile summing: tested, and dropped.** The floor's median and 99th percentile are sums of
the parts' (939 µs and 2,186 µs). Measured end to end at 100 writes a second, where the model's queue
is negligible, interleaved, five runs a side (m:p13-08-ab-fix-100-*, m:p13-08-ab-base-100-*): the
median is 1,647 µs on this phase's host and 1,486 on phase 12's, and the 99th percentile 2,598 and
2,402 µs, **all above the sums**. The run holds what the parts do not: an idle system's wake-ups,
a millisecond apart in the parts' timing and ten here. And at 1,250 a second the median on phase
12's host is 846 µs (m:p13-05-ab-base-1250-*), **below** the summed 939. The floor depends on how
warm the system is, so the sum is neither a bound on it nor an estimate of it. Decision 7's rule
applies: the sum is dropped, and with it the floors both latency criteria rest on. The restated
1,066 µs and 3.38 ms (and Compose's 1,167 µs and 3.50 ms) are the figures reported against until
they are restated; they are not grounded.

**The halving: dropped.** The sustained-rate criterion, half of C_design (3,139 a second), was to be
replaced by the rate at which the allowance's model, with the measured service, keeps the
sustained-rate definition. With the leader's measured execution per write at each rate (304 µs at
3,125, 280 at 3,500, 248 at 4,000, m:p13-05-ab-fix-*) the model keeps it to about 4,000 (its 99th
percentile wait 33 ms at 3,500 and 93 ms at 4,000, m:p13-08-allowance-3500-measured and -4000-measured),
but the model is falsified by the same runs (below), so it grounds nothing. The highest sustained
rate is reported with no criterion: in process 3,500 a second on this phase's host (every run) and
3,125 on phase 12's (3,500 in four runs of five); in Compose 1,250 for both, on this machine that day.

**The allowance's model, checked, and its sensitivity.** The model is M/D/1: Poisson arrivals at
3,125 a second, each served for the sync with two writers, 212.4 µs. Its waits by Lindley's
recursion, now a committed command (`bench allowance`, seed 12, 4,000,000 arrivals after 100,000):

| Service time | Utilisation | Median wait | 99th-percentile wait | Median criterion | 99th-percentile criterion | Record |
|---|---|---|---|---|---|---|
| 50% low, 106 µs | 0.33 | 0 µs | 222 µs | 939 µs | 2.41 ms | m:p13-08-allowance-3125-d0.5 |
| 25% low, 159 µs | 0.50 | 0 | 530 | 939 | 2.72 | m:p13-08-allowance-3125-d0.75 |
| 10% low, 191 µs | 0.60 | 69 | 861 | 1,008 | 3.05 | m:p13-08-allowance-3125-d0.9 |
| **as restated, 212 µs** | **0.66** | **127** | **1,199** | **1,066** | **3.38** | m:p13-08-allowance-3125-d1.0 |
| 10% high, 234 µs | 0.73 | 197 | 1,713 | 1,136 | 3.90 | m:p13-08-allowance-3125-d1.1 |
| 25% high, 266 µs | 0.83 | 419 | 3,229 | 1,358 | 5.42 | m:p13-08-allowance-3125-d1.25 |
| 50% high, 319 µs | 1.00 | 25,722 | 202,495 | about 27 ms | about 205 ms | m:p13-08-allowance-3125-d1.5 |

**A wrong model moves the criterion a little one way only:** the operating point sits at a
utilisation of 0.66, on the steep side of the queue's curve. Both of the model's inputs proved
wrong. The service is not the sync alone: the leader's serial work per write was 1.29 times it on
the loop (P13-03) and is 1.43 times it on the effect thread (304 µs at 3,125), where the model's
median wait is 1,961 µs and its 99th percentile 13.5 ms (m:p13-08-allowance-3125-measured). And the
generator's arrivals are evenly spaced, not Poisson, where the same service gives no queue at all
(m:p13-08-allowance-3125-measured-even). The waits measured in the effect queue at 3,125 are 280 µs
before a persist and 213 µs before a response at their medians (m:p13-05-ab-fix-3125-*), between the
two. The restated allowance came close because its two errors cancel: an agreement with no
mechanism behind it.
