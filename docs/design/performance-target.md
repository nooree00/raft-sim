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
