# raft-sim

Raft (Ongaro & Ousterhout, extended version, 2014 — `docs/references/raft-extended.pdf`) with a
deterministic fault-injecting simulator as an equal deliverable, built in C# over sixteen phases and
now closed. The contract is [`RAFT_PROJECT_SPEC.md`](RAFT_PROJECT_SPEC.md); the working rules, and
the script that enforces each one, are in [`AGENTS.md`](AGENTS.md).

Every check in the project has a deliberate breakage that must make it fail (a *sabotage*, under
`sabotage/`). Every task's outcome was predicted before the work and recorded afterwards as right,
partly or wrong. **What it found about building and measuring systems is in
[`docs/findings.md`](docs/findings.md); start with its first part.** Those findings are worth more
than the Raft.

## What is built

- **The algorithm** (`src/Raft.Core`): leader election, log replication, joint-consensus membership
  changes, log compaction with snapshots, client sessions (each write applied once) and ReadIndex
  reads. It does no I/O, has no clock and no threads: it takes inputs and returns ordered effects,
  with a persist barrier before anything that depends on a write (spec §4).
- **The simulator** (`src/Raft.Simulation`): one seed decides everything. The seed generates an
  explicit fault schedule, and a deterministic runner executes it on a logical clock, so a ten-minute
  execution takes milliseconds and replays byte for byte in another process. It injects:
  - network faults: delay, reordering, duplication, drops, one-way partitions;
  - node faults: crashes, restarts, pauses, clock skew;
  - disk faults: writes lost before their sync, writes reordered, torn writes, renames that did not
    land.
  Coverage is measured as effects observed in state, not as faults injected, and a dimension under
  its floor fails the build. A failing schedule is shrunk to the few events that matter and printed
  as a reproduction.
- **The checkers** (`src/Raft.Checker`): Raft's Figure 3 invariants over every execution, and a
  linearizability checker (Wing–Gong–Lowe) for client histories. The checkers are checked against
  Herlihy & Wing's published histories and against deliberately broken stores. Two soaks of 10,000
  generated executions each, the baseline and one with membership changes, run on every push in CI.
- **The real host** (`src/Raft.Host`): one node per process, framed TCP, real files with checksummed
  records, synced directories and a group commit; a real client; and an open-loop load bench with a
  latency decomposition. Three nodes run in Docker Compose with the leader killed and restarted, and
  the client's history is checked.

## Run it from a fresh clone

The only prerequisite is **Docker**. The build runs in the SDK image pinned by digest in
`ci/image.digest`, because `global.json` pins SDK 10.0.401 exactly with `rollForward:
latestPatch`: an SDK from another feature band — for example Ubuntu 24.04's `dotnet-sdk-10.0`
package, which is 10.0.1xx — is refused, deliberately.

```sh cold-walk
scripts/in-sdk.sh scripts/ci-build.sh
scripts/in-sdk.sh scripts/ci-test.sh
```

`ci-build.sh` restores in locked mode and builds with every warning an error; `ci-test.sh` runs
each test project and holds the executed-test counts to `ci/test-baseline.txt` (given project names,
only those, as a harness entry runs them; CI names none).

One simulated execution of a three-node Raft cluster through a fault schedule generated from a seed.
It prints the seed, the number of faults and the steps run, and the same seed prints the same line in
any process. `--trace FILE` writes the execution's trace, and `--schedule FILE` runs a saved schedule
instead of a seed's:

```sh cold-walk
scripts/in-sdk.sh dotnet run --project tools/Raft.SimRun -- run --seed 7 --generate --raft
```

Raft's checks run over generated executions in the test suite (a 300-execution sample) and in the two
soaks, which CI runs on every push:

```sh not-walked: CI runs each soak as its own job, about 13 minutes each on its runners
scripts/in-sdk.sh scripts/ci-soak.sh              # 10,000 executions: the invariants, linearizability, the coverage distribution
scripts/in-sdk.sh scripts/ci-soak-membership.sh   # 10,000 more, with membership changes
```

## Three nodes in Compose, a killed leader, the checker

Phase 9's done criterion, as CI's `compose` job runs it: three nodes and the real client in Docker
Compose, the leader killed with SIGKILL after 10 seconds and restarted from its volume, and the
client's history checked by the linearizability checker (`tools/Raft.Check`), with guards that the
run did test a leader's death. A process kill keeps the page cache, so this does not test
durability; the simulator does.

```sh cold-walk
scripts/compose-run.sh
```

## The gates

The same checks CI runs, runnable locally:

```sh not-walked: CI runs each of these as its own job, and the walk would only repeat them
scripts/in-sdk.sh scripts/ci-preflight.sh --allow-dirty   # SDK, pins, evaluated properties, workflows
scripts/in-sdk.sh scripts/ci-gates.sh                     # breakdown, trailers, register (after a build)
scripts/in-sdk.sh scripts/ci-sabotage.sh                  # every sabotage; needs a committed tree
scripts/secret-scan.sh                                    # on the host: gitleaks over full history
scripts/host-sabotages.sh                                 # on the host: sabotages that need docker
scripts/readme-walk.sh                                    # on the host: this README's steps, fresh
```

## Where things are

- **The contract:** [`RAFT_PROJECT_SPEC.md`](RAFT_PROJECT_SPEC.md). §11 is the table of all sixteen
  phases with each one's done criterion; §8 states the known limits of persistence and of the host's
  latency.
- **The working agreement and its enforcement:** [`AGENTS.md`](AGENTS.md).
- **Each phase:** [`docs/phases/`](docs/phases). Each phase has a `breakdown.md` (every task's vacuity
  risk, sabotage, prediction and outcome) and a `report.md` (what was built, what was measured, the
  reviewer's decisions). [`status.md`](docs/phases/status.md) marks which phases are complete.
- **The deferred items:** [`docs/register.md`](docs/register.md). Every open, done or dropped item,
  with what proves it; `gates register` holds it to the code.
- **The findings:** [`docs/findings.md`](docs/findings.md).
- **Design notes:** [`docs/design/`](docs/design). These cover the node's interface to its host, the
  log's properties under compaction, the anomaly taxonomy behind the checker's known-bad histories,
  the sabotage audit, and [`performance-target.md`](docs/design/performance-target.md): every latency
  criterion, how it was set, restated and superseded.
- **Measurements:** [`measurements/`](measurements). One JSON record per run, each naming the commit it
  measured; reports cite them as `m:<id>`, and a gate checks the records against git.

## What is open

The project closed with these items open, deliberately. None is expected to change.

- **The latency criteria at the operating point (3,125 writes a second) are missed**, each stated as
  a measured limit in spec §8:
  - the median is 1,478 µs against 1,198, a floor of one sync at a time on the leader's loop;
  - the 99th percentile is 5.4 ms against 5,051 µs: the inputs wait for that same loop's thread to
    be run.
  Neither is to be pursued.
- **The rate past the capacity** (9,375 writes a second, generator separate) is limited by something
  not named. The replication pipeline's one append in flight per follower is the candidate, and
  widening it would reverse a correctness fix (P11-02), so it is not pursued. It is a register row
  with no home.
- **The tail at 3,125 writes a second** has its own row with no home, not to be pursued. Two
  conditions apply if anyone reads through the trace again: the kernel reader is first measured
  against the stream reader under contention, and the rule for the reading is written fresh.
- **Three harness results are unexplained** (one register row): S-fsync-1's `target-not-run`
  (phase 15), S-ci-1's baseline failure at `27744b7`, and S-group-1's target failing unpatched at
  `9719e96`. The harness keeps an entry's output for an unexpected outcome and for a failing
  baseline command. It does not keep it for a test entry's target that fails unpatched (the
  baseline-target branch of `BaselineChecks` in `tools/Raft.Gates/Sabotage.cs`), the path the third
  took, and that is not to be built.
- **The person's cold walk of this README** (phase 0's row): not scripted, never done by the agent.
  `scripts/readme-walk.sh` catches README rot; it is not that walk.
- **Known limits stated in the spec:** the rename barrier is a simplification, not the contract
  (§8). The linearizability checker's budget is memory, with no structural threshold that predicts
  which histories it can decide (§6).

## Layout

```
src/Raft.Core         the algorithm — BCL only, no I/O, clock, threads or randomness
src/Raft.Simulation   the deterministic simulator: generator, schedules, runner, coverage, shrinker
src/Raft.Checker      the invariant checkers and the linearizability checker
src/Raft.Kv           key-value state machine
src/Raft.Host         one node per process (`node`), the real client (`client`), framed TCP, real files, the load bench
tools/Raft.SimRun     one simulated execution from a seed or a printed schedule
tools/Raft.Check      the check of a Compose run: its guards and the linearizability checker
tools/Raft.Gates      the process gates (spec §12) as a program
tests/                one test project per component that has real tests
sabotage/             deliberate breakages, each naming the check that must catch it
scripts/              build, test, gates, harness, bench and walk scripts; ci/ holds their inputs
measurements/         one record per measured run
docs/                 the phases, the register, the findings, design notes
```
