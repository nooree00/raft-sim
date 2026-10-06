# Phase 9 — task breakdown

Real sockets, multi-process (spec §11 phase 9). Format as in `docs/phases/P8/breakdown.md`, parsed
by `gates breakdown`; outcomes will say `(evidence)` or `(forcing)`.

**Done when** (spec §11): three processes in Compose, a real client, a killed leader, and the checker
green on the resulting history. In addition, carried forward:

- phase 8's acceptance: running the sabotages whose patches touch the files a push changes is made
  mechanical (P9-00). The `reason:` check verifies that a target fails with the right message;
  nothing verifies that it still exercises anything, and a patch that degrades its own target passes
  every guard the harness has (S-lin-4, phase 8);
- phase 8's finding: before accepting an expensive measurement as a property of the system, ask
  which part of it is a representation choice. This phase records histories from a real client for
  the first time, and every choice between the client and the checker (what is stamped, by which
  clock, what is left open) is one of those choices. Each is a decision below, not a default;
- the two register rows promised to phase 9: message corruption on a real transport (decision 2) and
  session expiry (decision 5);
- spec §3: a logging library is a phase-9 question, argued on its own merits (decision 6);
- spec §8's known limit: a process kill keeps the operating system's page cache, so killing a
  process does not test durability. Power loss stays the simulator's domain (decision 7);
- spec §10: one test where nobody calls anything, for every scheduled behaviour (the host's clock
  drives elections, P9-04); one test at the largest legitimate size for every configured limit (the
  frame, P9-02);
- phase 7's rule: a local timing within 5% of a deterministic ceiling is a failure on a slower
  runner, and a test that only needs a floor stops at the floor.

## Ordering

The tooling the reviewer asked for first, because every later commit in the phase is pushed through
it: P9-00 (touched-file sabotage runs). Then the state machine's last deferred item, which needs no
sockets: session expiry (P9-01). Then the host from the bottom up, each piece tested in one process
before any container exists: the framed transport with its checksum (P9-02), the disk executor on
real files (P9-03), the host's event loop with a real clock, peers and a client endpoint (P9-04),
a leader killed and restarted in one test process with the checker on the history (P9-05), and the
real client (P9-06). Then the phase's positive control, in real processes (P9-07), Compose and its CI
job (P9-08), the README and the cold walk's new path (P9-09), and the cost in CI (P9-10).

**Blocking set:** P9-00 to P9-09. P9-10 blocks nothing; it is the measurement the reviewer asks for
whenever a phase grows CI.

## Decisions for review

1. **Touched-file sabotage runs, before the push, at the head** (P9-00, the reviewer's ask). The
   entries whose `patch.diff` or `control.diff` touches any file the push changes (every commit since
   the previous push, together) run at the head, as a stage of the local run before every push. Why
   the push's range and not each commit: S-lin-4 broke at P8-02, which never touched `RaftNode.cs`,
   the only file its patch changes; P8-05 did. A per-commit selection would not have picked it at
   P8-02, and the range picks it once anything in the push changes the file. What it does not do: it
   adds nothing in CI, where the per-commit matrix already runs the whole harness on every commit of
   the push, so S-lin-4 would have gone red there, after the push. Its value is moving that catch
   before the push. **Its cost is not always small:** 51 of the 292 patches touch `RaftNode.cs` and 30
   `Simulator.cs`. Over phase 8's first push the selection was 94 entries and took 21.4 minutes
   locally in one invocation, against the 11-minute local run. A push that changes only the host or
   documentation selects a handful. Proposed: run it, report its time with the local run's, and let
   the 90-minute rule (AGENTS.md) bring it back to the reviewer if it grows. Alternative: run it only
   when the selection is under a size, and accept S-lin-4's class past it. Not recommended: the pushes
   that select many entries are the ones that change the node, which are the ones it is for.
2. **The transport: length-prefixed frames with a CRC-32C per frame** (closes the register's row).
   Canonical form does not detect a corrupted message that decodes to another valid one (95% of
   single-bit flips of a `RequestVote`, P3-02), and TCP's own checksum is 16 bits. A frame is a 4-byte
   length, the message, and a 4-byte CRC-32C of both. A frame whose checksum fails is dropped and its
   connection closed: to Raft it is a lost message, which the protocol already tolerates. The CRC is
   written in `Raft.Host` (about thirty lines, table-driven). Alternative: `System.IO.Hashing`, a
   Microsoft package, which is a dependency to ask for (spec §12). Recommended: hand-written, with a
   test against the published check value.
3. **One process per node, and the client is a mode of the same executable.** `Raft.Host` gains a
   `client` mode beside `node`, so the project graph stays as spec §4 draws it (`Host → {Core, Kv}`),
   and no new project ships. The checker never runs in the product. The client writes its history as
   JSON lines, and `Raft.Host.Tests` (spec §4's integration project, created here with its first real
   test) reads it into `ClientHistory` and runs the WGL checker. Tests compose whatever they need.
4. **Real-time order from one clock.** The history's invoke and response times come from one
   monotonic clock, the client process's `Stopwatch`, stamped before the request is written to the
   socket and after the reply is read. All logical clients run in that one process. No node's clock
   ever enters the history: nodes run on different machines in principle, and their clocks order
   nothing. A response the client never read is indeterminate, as in the simulator: a client that
   gives up stamps nothing (spec §6's naive treatments, P5-02).
5. **Session expiry: the least recently used session is evicted when the table is full** (closes the
   register's row). Each session records the log index at which it was last used; a `Register` at
   `MaxSessions` evicts the session with the smallest, and every node evicts the same one, because the
   log decides the order (the dissertation's §6.3: expiry must be deterministic). An evicted client's
   next command is refused `unknown-session|`; a command it had outstanding stays indeterminate, and
   it registers again. Alternatives: expiry by time, which needs a clock every node agrees on (the
   leader's timestamp in the log), a mechanism this project does not have; and refusing `Register` at
   the bound, which is today's behaviour and leaves an abandoned session in the table for good. The
   bound stays 1,000,000 (28 MB of snapshot, P8-01).
6. **No logging library.** The host writes Core's events as JSON lines to standard output, one per
   event, with the node id and the host's monotonic time. Compose collects them. A library would add
   levels, sinks and configuration that nothing here needs, and a dependency (spec §3).
7. **"Killed" means SIGKILL of the leader's process** (`docker kill`), then a restart from its data
   directory. That tests a process crash and recovery from real files written with `fsync`. It does
   not test durability: a process kill keeps the page cache, so a write the host never synced
   survives it. Durability under power loss stays the simulator's (spec §8's known limit, the
   register's phase-10 row). Said plainly in the report, so a green Compose run is not read as more.
8. **The positive control, in real processes** (P9-07): a host whose leader answers a write when it
   appends it (`RaftOptions.AnswerAtAppend`, off in every real configuration, as `CompactPastCommit`
   and `ReadsWithoutQuorum` are). With the leader's traffic to its peers held, then the leader killed,
   an answered write is lost, a later read misses it, and the checker must go red; the real host in
   the same construction must not. It runs in one test process, where a transport hook (test-only)
   holds the leader's frames. In Compose it is measured once, not required: whether a killed leader
   holds an answered, unreplicated write depends on load and timing.
9. **The Compose job is required in CI, and runs in the image CI already pins.** The node and client
   containers run on the SDK image pinned by digest (`ci/image.digest`), so no new image is pinned or
   pulled. A smaller runtime image is a second pin, a dependency to ask for. Two networks: one for
   peers and one for the client, so a later phase can cut one without the other. Adding `compose` to
   branch protection is the person's.

## Tasks

### P9-00 — Touched-file sabotage runs before the push

- **Task:** Decision 1. `gates sabotage --touched <since>` selects every entry whose `patch.diff` or `control.diff` names a file that a commit in `<since>..HEAD` changed, and runs those at the head. The local run gains it as a stage (AGENTS.md, the staged sequence), with its time reported; the push's previous head is the range's start, as for the report-at-head check. Gate tests: an entry touching a changed file is selected, one touching none is not, and an entry whose control alone touches the file is selected.
- **Vacuity:** A selection that always comes back empty passes every push. Guarded by a gate test on a range known to touch `RaftNode.cs`, and by printing the selection's size. Sabotage: S-gate-1, the selection reads only `patch.diff`: the control-only gate test goes red.
- **Sabotage:** S-gate-1
- **Verifiable here:** yes — the gate and its tests run locally
- **Prediction:** My first selection reads the paths from `patch.diff` alone and misses an entry whose `control.diff` touches the file, because the harness treats the control as a separate run, not as part of the entry's footprint. Over phase 8's range it selects 94 entries, as the manual run did. **Observable:** the control-only gate test's verdict before any fix, and the selection's size over `a35b945..6f5001d`.
- **Outcome:** pending

### P9-01 — Session expiry: the least recently used session is evicted

- **Task:** Decision 5. Each session records the index of the entry that last used it, a cached reply included; a `Register` at `MaxSessions` evicts the session with the smallest. The snapshot carries the last-used index. `SessionWorkload` treats `unknown-session|` as its session's end: it registers again, and the command it had outstanding stays indeterminate. `MaxSessions` becomes a constructor argument (default 1,000,000), so a construction can fill a small table. The limit test is re-run at the bound with eviction.
- **Vacuity:** A table that never fills never evicts. Guarded by constructions at a bound of 3, asserting the eviction happened (an `unknown-session|` reply) before the verdict, and by a soak-sample variant at a small bound with a dimension for "a session evicted and its client registered again". Sabotage: S-sess-5, eviction by the session's id, not its last use: the construction where the oldest id is the busiest session goes red.
- **Sabotage:** S-sess-5
- **Verifiable here:** yes — constructions and the simulator
- **Prediction:** My first implementation updates a session's last use only when a command is applied fresh, not when a retry is answered from the cache, so a session whose latest command was retried is evicted while an idle one survives. The construction that retries a command, then fills the table, goes red first. **Observable:** the first red construction by name.
- **Outcome:** pending

### P9-02 — The framed transport, with a checksum

- **Task:** Decision 2. A frame is a 4-byte length, the encoded message, and a CRC-32C of both; the reader assembles frames across partial reads, drops a frame whose checksum fails and closes the connection. Tests on a loopback socket: the CRC against its published check value (`"123456789"` is `0xE3069283`); every message type round-trips; a frame delivered one byte per read; a corrupted bit is never delivered; and the largest legitimate frame under the configured options, a full batch of 64 commands at `MaxCommandBytes` (1 MB), 64 MB, round-trips (spec §10). The codec's absolute bound (`RaftOptions.LargestCommandFor`, about 2 GB per batch) is refused by the options, not sent.
- **Vacuity:** A loopback test whose frames always arrive in one read never exercises reassembly; guarded by the one-byte-per-read test and the largest frame. Sabotage: S-frame-1, the checksum computed and not compared: the corrupted-bit test goes red.
- **Sabotage:** S-frame-1
- **Verifiable here:** yes — loopback sockets in a test
- **Prediction:** My first reader assumes a read returns a whole frame once the length has arrived, so the largest frame (64 MB) is cut at the socket's buffer. The limit test goes red before any other. **Observable:** the first red test by name.
- **Outcome:** pending

### P9-03 — The disk executor on real files

- **Task:** The host executes Core's `Persist` effects on files in its data directory in order, each made durable before the next effect (`fsync`; for a rename, the target and then the directory), and releases a `Send` or a client response only after every persist before it is durable (node-interface §4). A restarted host recovers from those files through `TermVoteLog` and `EntryLog`, as the simulator's nodes do. Tests with a recording file system: the order of writes, syncs and releases; and on real files, a restart reads back what was written.
- **Vacuity:** A test on real files cannot see a missing `fsync`, since the page cache survives the test; guarded by the recording file system, which sees each sync and each release in order. Sabotage: S-hostdisk-1, a `Send` released before the persist before it is synced: the recording test goes red.
- **Sabotage:** S-hostdisk-1
- **Verifiable here:** yes — on this machine's file system
- **Prediction:** .NET has no managed call that syncs a directory: opening a directory as a file handle fails on Linux. So the rename test goes red first, on the directory sync, and the fix opens the directory with `open(2)` and calls `fsync(2)` through P/Invoke, inside `Raft.Host` only. **Observable:** the first red test and its exception.
- **Outcome:** pending

### P9-04 — The host: a real clock, peers and a client endpoint

- **Task:** A host process runs one `RaftNode` on one thread: ticks from a monotonic clock (one tick unit is one millisecond), frames from its peers, client requests from a TCP endpoint (one request per line, its reply on the same connection), Core's events to standard output as JSON lines (decision 6). It dials its peers and redials on failure; a message to an unreachable peer is dropped. A `Status|` request answers with the host's role, for the orchestration. Tests: three hosts in one test process, on loopback ports and temporary directories, elect a leader with nobody calling anything but the clock (spec §10), commit client writes, and answer reads by ReadIndex.
- **Vacuity:** A test that drives the node by calling it proves the node, not the host. Guarded by the test that only starts hosts and waits, and asserts a leader from the hosts' events. Sabotage: S-host-1, the clock's ticks never delivered: no leader is elected, and the test goes red.
- **Sabotage:** S-host-1
- **Verifiable here:** yes — loopback, in a test process
- **Prediction:** With a 10 ms timer and real scheduling, a heartbeat is sometimes late past the minimum election timeout on a loaded machine, and a follower stands while the leader is healthy. In a 30-second steady run of three hosts with no faults, the term goes above 1 at least once on this machine. **Observable:** the highest term of the steady run.
- **Outcome:** pending

### P9-05 — A leader killed and restarted, in one test process, with the checker on the history

- **Task:** Three hosts and the real client (P9-06) in one test process: the client's load runs, the leader is killed (its host disposed without closing anything cleanly), a new leader is elected, the old one restarts from its data directory and catches up, and the client's history is checked by WGL, with retries merged (P8-00). Guards, each asserted before the verdict: the killed host was the leader; another host led a later term after it; writes completed before and after the kill; at least one operation was invoked before the kill and answered, or left indeterminate, after it; the restarted host applied entries committed while it was down.
- **Vacuity:** A kill that lands on a follower, or after the load has ended, tests nothing about a leader's death; guarded by the guards above. Sabotage: S-kill-1, the orchestration kills a follower: the "killed host was the leader" guard goes red.
- **Sabotage:** S-kill-1
- **Verifiable here:** yes — in a test process
- **Prediction:** The verdict is green, and the first red is a guard, not the checker: the client finishes its operations in the gap between the kill and the new leader's election, so no operation spans the kill until the load is made continuous through it. **Observable:** which assertion fails first.
- **Outcome:** pending

### P9-06 — The real client

- **Task:** Decision 3 and 4. `Raft.Host client`: a number of logical clients in one process, each with a session (`Register`, then `Session|id|seq|command`), sending the workload's mix, following redirect hints, timing out and retrying with the same bytes as `SessionWorkload` does, and writing every attempt (request, node, invoke, response, reply) as JSON lines, stamped from one `Stopwatch`. A test reads a written history into `ClientHistory` and gets the operations a hand-built `ClientOp` list gives.
- **Vacuity:** A client whose history is translated wrongly can make any execution look linearizable, for example by dropping indeterminate operations (spec §6). Guarded by the round-trip test against a hand-built history with an indeterminate operation, and by P9-05's guards. Sabotage: S-client-4, a timed-out attempt written with the time it gave up as its response: the round-trip test goes red.
- **Sabotage:** S-client-4
- **Verifiable here:** yes — tests
- **Prediction:** My first client follows a redirect whose hint is `-` (no leader known) by retrying the same node at once, so during an election one logical client sends hundreds of requests in a second. The first red is a guard I will add in P9-05 on attempts per operation. **Observable:** the largest number of attempts for one operation in P9-05's first run.
- **Outcome:** pending

### P9-07 — The positive control in real processes

- **Task:** Decision 8. `RaftOptions.AnswerAtAppend`. In one test process: the leader's frames to its peers held by a test-only transport hook, writes answered at append, the leader killed, a new leader elected without those writes, and a read of their key: the checker must reject the history. The real host in the same construction must not answer before commit, and its history must be accepted. In Compose, run once with the control on and the caught count recorded.
- **Vacuity:** A construction where the held writes were never answered shows nothing; guarded by asserting at least one answered write absent from the new leader's log before the verdict is read. Sabotage: S-ctl-2, the control on by default: the real host's half goes red.
- **Sabotage:** S-ctl-2
- **Verifiable here:** yes — in a test process; Compose locally
- **Prediction:** Red in the construction on the first run. In ten local Compose runs with the control on and no held traffic, it is caught in fewer than half, because a leader killed under three clients' load holds at most a few answered writes, and most reach a follower before the kill. **Observable:** the caught count of ten Compose runs.
- **Outcome:** pending

### P9-08 — Compose, and its CI job

- **Task:** The done criterion. A `Dockerfile` building the host in the pinned SDK image and running it there (decision 9); `compose.yaml` with three nodes, a client, and two networks; `scripts/compose-run.sh`: up, wait for a leader (`Status|`), run the client's load, find the leader and `docker kill` it, wait for another leader, restart the killed container, continue the load, stop, collect the history and the events, and run `Raft.Host.Tests`' check of that history with P9-05's guards. A required CI job `compose`, which `gates build-collect` requires.
- **Vacuity:** A Compose run whose client never reached the cluster writes an empty history, which is linearizable. Guarded by P9-05's guards on the collected history. Sabotage: S-compose-1, the script kills a follower: the guard goes red.
- **Sabotage:** S-compose-1
- **Verifiable here:** partial — Compose runs locally; the CI job only in CI
- **Prediction:** The first red is start-up, not Raft: the nodes start before their peers' names resolve, and the host's first dial fails. If the dial gives up rather than retrying, no leader is elected in the wait's time. **Observable:** the first failing step of the first Compose run.
- **Outcome:** pending

### P9-09 — The README, and the cold walk's new path

- **Task:** The README gains the Compose run (build, up, the client, the check) in the order a person on a fresh clone would follow it. `scripts/readme-walk.sh` runs the commands it can run in its container, and names the ones it cannot (Docker in Docker), so their absence is visible rather than silent. The person's cold walk this phase includes the Compose path.
- **Vacuity:** A walk script that skips the Compose commands without saying so passes over the phase's whole path. Guarded by the script listing each skipped command in its output, and failing if a fenced command block is neither run nor listed. Sabotage: S-walk-1, a new fenced block in the README that the script neither runs nor lists: the walk fails.
- **Sabotage:** S-walk-1
- **Verifiable here:** partial — the script here; the person's walk is the person's
- **Prediction:** The README walk's container has no Docker, so every Compose command is unrunnable there, and the script's first version runs the blocks it can and drops the rest silently. **Observable:** the walk's output for the Compose section before the listing is added.
- **Outcome:** pending

### P9-10 — The new work's cost in CI

- **Task:** Measure what phase 9 adds to CI on GitHub: the Compose job, each harness shard against phase 8's run 37393582257, the soaks, the per-commit matrix's longest job, and the local run with P9-00's stage, with the runner's ratio applied to every local number quoted. Only runs whose every job completed once.
- **Vacuity:** A measurement on a run with re-run jobs compares different work. Guarded by using only runs whose every job completed once.
- **Sabotage:** ; manual: the comparison is made once against a run with a re-run job, to see its numbers differ and be refused
- **Verifiable here:** partial — CI's numbers only in CI
- **Prediction:** The Compose job takes under 10 minutes on GitHub, most of it building the image and restoring packages, not the run; no harness shard passes 75% of its ceiling, because the host's entries target `Raft.Host.Tests`, a new project, which no other entry's neighbours include. **Observable:** the Compose job's duration and the slowest shard's harness step.
- **Outcome:** pending

## Sabotage ids

New series: S-gate (P9-00), S-frame (P9-02), S-hostdisk (P9-03), S-host (P9-04), S-kill (P9-05),
S-compose (P9-08), S-walk (P9-09). S-client-4 follows S-client-3, S-sess-5 follows S-sess-4, S-ctl-2 follows
S-ctl-1. Each id's `sabotage/<id>/` entry lands in the same commit as the check it proves and is run
on that commit (`gates sabotage --only`) before it is pushed; from P9-00 on, the touched-file stage
runs before every push; the shards run in CI.

## Register rows

Closed here: message corruption on a real transport (decision 2) and session expiry (decision 5).
Opened here: none proposed. Network faults between real processes (loss, delay, partition with
`tc` or a proxy) are not in spec §11's phase 9; the two networks of decision 9 leave room for them,
and they are not promised to any phase unless the reviewer asks.
