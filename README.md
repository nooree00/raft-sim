# raft-sim

Raft (Ongaro & Ousterhout, extended version, 2014 — `docs/references/raft-extended.pdf`) with a
deterministic fault-injecting simulator as an equal deliverable. The contract is
[`RAFT_PROJECT_SPEC.md`](RAFT_PROJECT_SPEC.md); the working rules and the scripts that enforce
them are in [`AGENTS.md`](AGENTS.md). Current phase and deferred items:
[`docs/phases/status.md`](docs/phases/status.md), [`docs/register.md`](docs/register.md).

Raft so far: leader election with `currentTerm` and `votedFor` persisted (phase 3); log replication
and everything after it follow in later phases. Phase 0 stays open on one item, the reviewer's cold walk.

## Build and test from a fresh clone

The only prerequisite is **Docker**. The build runs in the SDK image pinned by digest in
`ci/image.digest`, because `global.json` pins SDK 10.0.401 exactly with `rollForward:
latestPatch`: an SDK from another feature band — for example Ubuntu 24.04's `dotnet-sdk-10.0`
package, which is 10.0.1xx — is refused, deliberately.

```sh cold-walk
scripts/in-sdk.sh scripts/ci-build.sh
scripts/in-sdk.sh scripts/ci-test.sh
```

`ci-build.sh` restores in locked mode and builds with every warning an error; `ci-test.sh` runs
each test project and holds the executed-test counts to `ci/test-baseline.txt`.

## The gates

The same checks CI runs, runnable locally:

```sh
scripts/in-sdk.sh scripts/ci-preflight.sh --allow-dirty   # SDK, pins, evaluated properties, workflows
scripts/in-sdk.sh scripts/ci-gates.sh                     # breakdown, trailers, register (after a build)
scripts/in-sdk.sh scripts/ci-sabotage.sh                  # every sabotage; needs a committed tree
scripts/secret-scan.sh                                    # on the host: gitleaks over full history
scripts/host-sabotages.sh                                 # on the host: sabotages that need docker
scripts/readme-walk.sh                                    # on the host: this README's steps, fresh
```

## Layout

```
src/Raft.Core         the algorithm — BCL only, no I/O, clock, threads or randomness
src/Raft.Simulation   deterministic simulator (phase 1)
src/Raft.Checker      linearizability checker; today: history model, KV spec, brute-force oracle
src/Raft.Kv           key-value state machine
src/Raft.Host         TCP transport and process host
tools/Raft.Gates      the process gates (spec §12) as a program
tests/                one test project per component that has real tests
sabotage/             deliberate breakages, each naming the check that must catch it
docs/phases/          per-phase breakdowns and reports
```
