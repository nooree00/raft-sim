# Phase 2 — task breakdown

The checker's rejecting half (spec §6, §11 phase 2), preceded by what phase 2 is blocked on: the
harness sharding, and a generator whose coverage measures effects the algorithm depends on and
reaches the three events it never produced. Format as in `docs/phases/P1/breakdown.md`, parsed by
`gates breakdown`; outcomes will say `(evidence)` or `(forcing)`.

**Done when** (spec §11): the WGL checker rejects every known-bad history, agrees with the
brute-force oracle on random small histories, and rejects histories produced by a deliberately
broken non-Raft store in the simulator. And, as amended by the reviewer at P1 acceptance:

- the sabotage harness is sharded and back under its 15-minute ceiling per shard;
- every coverage dimension is an observable effect the algorithm depends on, never a fault name;
- the three events at zero (writes actually reordered across a crash, a node fully isolated, every
  node down) each appear above the coverage floor;
- every Core input and effect is driven by at least one simulator test.

All four land before any checker work.

## Ordering

P2-01, P2-02 and P2-03 first, in that order, before any other phase-2 work: a green phase 2 on the
phase-1 generator would mean very little. Then P2-04 → P2-05 → P2-06 → P2-07 (the known-bad
histories before the checker, spec §6), and P2-08 → P2-09 → P2-10 (clients, broken stores,
end-to-end shrinking).

**Blocking set:** P2-01 through P2-06, P2-08 and P2-09. **Deferrable if phase 2 runs long:** P2-07
(checker cost limits) and P2-10 (linearizability as a shrinkable failure) to phase 3. Both must exist
before phase 3's cluster runs produce histories worth checking and shrinking.

## Decisions for review

1. **The ceiling goes back to 15 minutes in P2-01's commit, not before.** In run 36356675462 one
   job's harness took 1023 s. Reverting ahead of sharding would make any push red whenever it lands
   on a slow runner. The revert and the sharding land together, and P2-01 closes the register row.
2. **A correct store as the control in P2-09.** A checker that rejects everything passes every
   rejecting-half test. P2-06's oracle agreement guards that; a trivially correct single-node store
   whose histories must be *accepted* is a second guard. It is not the accepting half (phase 5:
   Raft histories, indeterminate operations, at length); it is a positive control.
3. **Required-check names stay as they are.** The job that runs build and tests becomes
   `build-core`; a collect job named `build` requires it and every harness shard. Branch protection
   (build, each-commit, secrets, readme-walk) needs no change.
4. **Larger runner: not pursued.** As far as I can tell, GitHub's larger hosted runners are billed
   and need an organisation plan. Not verifiable from here; additive to sharding if it is free.
5. **No new dependencies.** WGL, the random-history generator and the client driver are hand-written.

## Where phase 1's coverage stood (audited at P1 acceptance)

**What each dimension measured.** The P1-10 gate has ten dimensions:

| Dimension | What it counted | Kind |
|---|---|---|
| asymmetric-block | a message blocked while the partition table left the reverse direction open | partition table, not traffic |
| dropped | a message the Drop fault took that was never delivered | effect |
| duplicate-delivered | a message delivered twice | effect |
| reordered-delivered | a message delivered after one sent later on its link | effect |
| delayed | the Delay fault fired on a message | **injection** |
| crashed | a crash happened | injection (the crash is its own effect) |
| crash-lost-writes | a crash lost or tore a write in flight | effect |
| torn-record | a crash left a partial record | effect |
| paused | a node resumed from a pause | **injection** |
| clock-skewed | one node's write rate diverged 8% or more | effect |

**What it could not see.** The gate fails a dimension at 0%, but only for dimensions it lists; an
event nobody listed is not 0%, it is invisible. Measured over the same 200 executions, from trace
effects:

| Event | Executions |
|---|---:|
| writes actually reordered at a crash (≥2 in flight, a later one survives an earlier lost) | **0** |
| a node fully isolated (partitioned both ways from both peers at once) | **0** |
| every node down at once | **0** |
| ≥2 writes in flight at a crash | 1 |
| a crash that lost writes, by loss mode: Pending / Torn / Reordered | 2 / 4 / 2 |
| a crash while paused | 5 |
| two of three nodes down at once (majority lost) | 9 |

**The traceability table's "stressed by" column names faults, not effects.** Its 43 rows cite
crash-pending 20 times, partition 17, drop 15, reorder 12, crash-reordered 3. F2-01 and F2-04 are
"stressed by crash-reordered", an event whose effect never happened.

**Interface members nothing drives.**

| Member | Status |
|---|---|
| `ClientRequest` | never constructed anywhere |
| `ClientResponse` | no node emits it, and the barrier release discards it |
| `PersistWriteAt`, `PersistRename`, `PersistDelete` | never constructed; `SimDisk` implements them untested |

The traceability test proves each member *exists*, which is all it was written to prove. No P1
assertion depended on a client response (there is no client), so none needs re-running for that.
But P1's done criterion ("every fault in §7 … has a test proving it fires") was not met for rename,
and reading `SimDisk` shows one latent bug. After a completed rename, the lose-synced-write control
restores the source file and leaves the destination: its undo record holds one file, and a rename
changes two. Found by reading only; no test yet.

## Tasks

### P2-01 — Shard the sabotage harness; ceiling back to 15 minutes

- **Task:** The harness takes `--shard i/n` and runs only its share, dealt round-robin over the sorted entry ids so a shard's content changes only when entries are added or removed. The shard count is derived from the manifest, not chosen per run: `n = ceil(entries / K)`, with `K` in `ci/sabotage-shard-size.txt`, a committed number, so adding entries adds shards. The per-shard ceiling returns to 15 minutes as a backstop. In the workflow, the harness leaves the build job: `build-core` (preflight, build, gates, reports, tests), one `sabotage i/n` job per shard, and a collect job named `build` (decision 3). The collect recomputes the plan from the manifest and requires every shard job to have succeeded, with every entry dealt to exactly one shard. The per-commit matrix becomes commit × shard. A commit that has no `ci/sabotage-shard-size.txt` (before this task) runs its own unsharded harness in shard 1, and its other shards report `unsharded commit`. The harness also reports its fixed cost (worktrees and baseline builds) separately from per-entry time. Closes the register row.
- **Vacuity:** A shard plan that deals some entry to no shard, or runs zero shards, reports green having run less, which is the zero-job CI again. Guarded: a unit test over the plan (union equals the manifest, shards disjoint, none empty); the collect job recomputes the plan and requires one successful job per shard; and the existing per-entry "expected result" rule still applies inside each shard.
- **Sabotage:** S-shard-1, S-shard-2, S-shard-3, S-ci-2
- **Verifiable here:** partial — the plan, the collect decision and the harness's `--shard` run locally; the workflow matrix only in CI, read back through the GitHub tools.
- **Prediction:** Sharding saves less than its count suggests, because each shard pays a fixed cost that one unsharded run pays once: its own container, restore, and four worker worktrees with a baseline build each. I expect that fixed cost to be at least 90 s per shard on GitHub. From the slowest runner seen (1023 s for 81 entries on four workers, about 12.6 s per entry), a shard that stays under half the ceiling holds `K ≈ (450 − 90) / 12.6 ≈ 28` entries: three shards today. **Observable:** in the first CI run, every shard's reported fixed cost is ≥ 90 s, and the slowest shard's total exceeds its entries × 12.6 s by at least that fixed cost.
- **Outcome:** pending

### P2-02 — Coverage measures effects the algorithm depends on; the three zeros become reachable

- **Task:** **Every dimension restated as an observable effect**, named for what the algorithm depends on, never for a fault: "a message delivered after one sent later than it on its link"; "a message never delivered to a live receiver"; "a message delivered twice"; "a message delivered later than the network's normal maximum delay"; "a node took a step after missing more than one election-timeout's worth of time" (for pause); "a node's clock rate diverged"; "a write acknowledged to no one and lost at a crash"; "a partial record on disk at recovery"; "writes that completed out of issue order across a crash"; "a node that could neither send to nor receive from any peer, for at least one timeout"; "no node up"; "a majority down". **The traceability table's "stressed by" column** is rewritten in these effect names, and a test requires every name it uses to be a dimension that the coverage run hits above the floor. The table then states which effects each Figure 2 rule depends on, and the coverage run proves those effects happen. **Relationships, not only zeros** (reviewer, at approval): the restated dimensions are checked pairwise. Identical execution sets fail as one measurement wearing two names, and a rate at or above 95% is reported and must be declared always-on with a reason, because it usually means the generator cannot produce the other case. Phase 1's "reordered-delivered" at 189/200 gets that second look: it is either trivially easy to trigger or counting something broader than it claims (delay jitter on non-FIFO links reorders without any Reorder fault), and the restated dimension says which. **The floor rule:** a dimension hit in fewer than `F` of `N` executions is *effectively untested* and fails unless declared rare with a reason. `F` is set so that a bug needing that event alone would be missed with probability below 5%. **Faults placed by state, so the zeros become reachable:** `CrashWhenInFlight(At, Node, MinPending, Loss)` is armed at `At` and fires at the first moment that node has at least `MinPending` writes in flight; `Isolate(At, Node, Until)` blocks every link to and from the node; `CrashAll(At, Until)` and `CrashMajority(At, Until)`. Each is still data: it serialises, generates from its own stream, shrinks and reproduces like every other fault. A fault still armed at the end of a run is traced as `UNFIRED`.
- **Vacuity:** A dimension named for its fault counts the injection, and passes when the fault fires but changes nothing, which is how three zeros sat behind a green gate. Guarded: every dimension is computed from trace effects, never from schedule or fault-fired lines; a test per dimension builds one execution where the fault fires with no effect and requires the dimension *not* to be hit; and the traceability test ties each Figure 2 rule to effect dimensions that must clear the floor. An unlisted event stays invisible, which is the residual; the traceability column is the list, owned by the rules rather than by the faults.
- **Sabotage:** S-cov-4, S-cov-5, S-cov-6, S-rare-1, S-rare-2
- **Verifiable here:** yes — locally.
- **Prediction:** Two of the ten phase-1 dimensions are injection counts (delayed, paused). Restated as effects, "delayed" falls by more than a third: an extra delay of 20–200 on a link that carries a message every few units is often overtaken by nothing, and often lands inside the normal jitter spread of the next one. "Paused" barely moves (less than 5%), because a pause of 100–3,000 units almost always exceeds a timeout's worth of time. Separately, no rate of time-placed crashes reaches writes reordered across a crash with the echo protocol: two writes are in flight only when a slow disk's latency exceeds the 100-tick period. **Observable:** in the first run of the restated gate, delayed ≤ 2/3 of its phase-1 count (128) and paused ≥ 95% of its phase-1 count (118); at ten times the default crash rate, writes-reordered-at-crash stays at 2 or fewer of 200, and with `CrashWhenInFlight(MinPending = 2)` at the default rate it appears in at least half the executions that contain one.
- **Outcome:** pending

### P2-03 — Every Core input and effect driven by a simulator test

- **Task:** **A mechanical check:** a test lists every concrete `Input` and `Effect` type in `Raft.Core` by reflection, and requires each to appear in the trace of at least one simulator test (a trace-kind census across the test suite's runs). The check fails today, on `ClientRequest`, `ClientResponse`, `PersistWriteAt`, `PersistRename` and `PersistDelete`. **The client path:** the simulator can inject a `ClientRequest` into a node, from a schedule entry or a client, and route the node's `ClientResponse` back through the barrier to a recorded receiver; the release no longer discards it. **Disk operations:** write-at, rename and delete each get effect tests under every crash mode, including the latent lose-synced-after-rename bug above. Its test is written before the fix and run red against today's `SimDisk`, with the failing output recorded in `docs/findings.md`. Every commit must be green, so the test and the fix land together with a sabotage entry whose patch reinstates the bug and expects the test to catch it. The bug is then a recorded failure, re-demonstrated on every push, rather than a corrected line. **The P1 criterion gap is closed:** rename is proven to fire by its effect, and the register row opened for it at acceptance is closed.
- **Vacuity:** A census that counts types *constructed* rather than types that *reached the world* would pass on a response built and then discarded, which is exactly today's bug. Guarded: the census reads the trace, where an effect appears only after the simulator has acted on it, and a test drops the client's receiving side and requires the response to be missing from the trace.
- **Sabotage:** S-iface-1, S-iface-2, S-iface-3
- **Verifiable here:** yes — locally.
- **Prediction:** Beyond the rename bug already found by reading, driving write-at, rename and delete through every crash mode exposes at least one more `SimDisk` defect. The torn and reordered modes were written and tested only against appends: torn keeps a prefix of an append's or write-at's data, but a write-at's prefix lands at an offset, and reordered applies survivors in issue order even when a rename's destination depends on a write the crash dropped. **Observable:** at least one of the new disk-operation tests, other than the rename/lose-synced one, fails on its first run against today's `SimDisk`.
- **Outcome:** pending

### P2-04 — The known-bad catalogue, audited before the checker

- **Task:** An anomaly taxonomy for single-key KV histories, written down with one line of definition each: stale read; lost write; committed-then-vanished; duplicate apply of an Append; CompareAndSwap succeeding twice from one expected value; real-time order violated between non-overlapping operations; a value read that was never written; an indeterminate write observed and then un-observed; two readers observing concurrent writes in contradictory orders; Delete resurrected. Every existing hand-written history (P0-15) is classified. Each empty category gains at least one hand-written history, and each also gains a *near-miss twin*: the same history made linearizable by the smallest change, so the category is shown by a pair, not a lone reject. The brute-force oracle must reject every bad one and accept every twin. All of this lands before any WGL code (spec §6: "write those before the checker").
- **Vacuity:** Bad histories the author believes bad because the author's model of linearizability says so; the oracle shares that model. Guarded by the H&W verdicts from outside the project (P0) and by the twins: a history that stays bad under the smallest change is testing something other than its label.
- **Sabotage:** S-hist-6, S-hist-7
- **Verifiable here:** yes — locally.
- **Prediction:** The existing fifteen histories cluster in the categories that are easiest to write by hand (stale read, lost write, CompareAndSwap). **Observable:** at least two taxonomy categories are empty before this task. I expect duplicate apply of an Append, and contradictory observation order between two readers.
- **Outcome:** pending

### P2-05 — The WGL checker, decomposed per key

- **Task:** Wing & Gong / Lowe linear search with memoisation over reachable (set of linearized operations, model state), in `Raft.Checker`: **Decomposition:** per key first (Herlihy & Wing locality, valid because every operation is single-key). **Indeterminate operations:** each may take effect at any point after its invocation or never, and is unconstrained by any response. **Output:** the verdict, the rejecting key and its sub-history, and the longest linearizable prefix found. **Tests:** it must reject every known-bad history (P2-04 and H&W), accept every twin, and agree with the whole-history search on the locality property test (P0).
- **Vacuity:** A checker that rejects everything passes this task alone. Guarded by the twins (they must be accepted), by P2-06, and by decision 2's control.
- **Sabotage:** S-wgl-1, S-wgl-2, S-wgl-3
- **Verifiable here:** yes — locally.
- **Prediction:** On the catalogue, memoisation barely matters: histories are short, so the search cost is dominated by the per-key split. Where it does matter, the model decides: Put's state is the last value, so reachable states grow as subsets × values, while Append's state is the whole string, which differs per order, so states grow factorially. **Observable:** on a non-linearizable history of k concurrent operations on one key followed by a Get matching no order, the states explored (a deterministic counter the checker reports) grow by at least 4× per added operation for Appends, and by at most 2.5× for Puts, over k = 4..8.
- **Outcome:** pending

### P2-06 — Differential testing against the brute-force oracle

- **Task:** A seeded generator of random small histories (up to 8 operations, 1–2 keys, 1–3 clients, a share of indeterminate operations): **Linearizable by construction:** run a random sequential execution, then stretch each operation's interval without breaking the order constraints. **Non-linearizable by mutation:** alter one output, or move one interval. WGL and the oracle must agree on every history. The test reports the verdict split and fails unless both verdicts make up at least 25%. A disagreement prints the history in the text form the hand-written tests use, so it can be committed as a regression case.
- **Vacuity:** Agreement on a distribution that is 99% one verdict says almost nothing: a checker with one verdict hard-wired agrees 99% of the time. Guarded by the verdict-split floor and by the stretch/mutate construction, which produces both verdicts on purpose.
- **Sabotage:** S-diff-1, S-diff-2
- **Verifiable here:** yes — locally.
- **Prediction:** A naive generator (random operations with random outputs and random intervals) yields almost only non-linearizable histories, which is why the construction above exists. **Observable:** the naive variant, run once for the record, gives fewer than 5% linearizable histories; the constructed generator gives between 25% and 75%.
- **Outcome:** pending

### P2-07 — The checker's cost, measured deterministically

- **Task:** The checker reports states explored (never wall time; P1 findings: no wall-clock assertion in a suite the harness runs). Given a budget in states, it reports "undecided" rather than running unbounded. Neither "undecided" nor "linearizable" counts as a rejection. Measured and recorded: the concurrency (operations overlapping on one key) at which a 200-operation history exhausts a budget of 10⁶ states, for Put-only, Append-only and mixed workloads. These numbers set phase 3's client count and operation mix.
- **Vacuity:** A budget that is never reached tests nothing, and a budget reached on every history hides the checker behind "undecided". Guarded by tests on both sides: a history that must decide within the budget, and one constructed to exhaust it, which must report undecided, never a verdict.
- **Sabotage:** S-wgl-4
- **Verifiable here:** yes — locally.
- **Prediction:** Per-key decomposition, not memoisation, is what makes long histories feasible. A 200-operation history over 20 keys at concurrency 3 decides in under 10⁴ states in total. The same history on one key is still decidable, but explores more than 10× as many states. **Observable:** the recorded table.
- **Outcome:** pending

### P2-08 — Clients and histories in the simulator

- **Task:** **Clients:** simulated clients issue `ClientRequest` inputs to nodes and receive `ClientResponse` effects over the simulated network, so every network fault applies to client traffic too. **Recording:** each client operation's invocation and response are recorded at logical time, and an operation with no response before its timeout is recorded as indeterminate. **Text form:** the history has one, is written beside the trace, and reproduces byte-identically from the schedule. The path itself (a request in, a response out, through the barrier) is P2-03's; this task adds clients, timeouts and the recorded history on top of it.
- **Vacuity:** A history recorded from the client's intentions (requests) rather than from what came back would show responses that never arrived. Guarded: responses are recorded only on delivery to the client, and a test drops the response link and requires the operation to be indeterminate.
- **Sabotage:** S-client-1, S-client-2, S-client-3
- **Verifiable here:** yes — locally.
- **Prediction:** Under the default fault mix, most indeterminate operations come from dropped or partitioned *responses*, not from crashes: client traffic crosses the network twice, and network faults are far more frequent than crashes. **Observable:** over 200 executions, more than two-thirds of indeterminate operations belong to a request that the node did handle, with a response sent and never delivered.
- **Outcome:** pending

### P2-09 — Broken stores in the simulator, and a correct control

- **Task:** Three non-Raft KV stores built on the Core node interface, in the test project (like P1-11's planted bugs): **(a)** asynchronous primary-backup, with clients reading from the backup (spec §6's example); **(b)** a primary that acknowledges before replicating, with failover to the backup when the primary crashes, which loses acknowledged writes; **(c)** a correct single-node store that persists before it responds (decision 2). Across generated schedules, the checker must reject the histories of (a) and (b) in a stated share of executions and accept every history of (c). Each rejection names its key and anomaly.
- **Vacuity:** Broken stores whose histories happen to be linearizable in every generated execution (a workload too sparse for the anomaly to show) make "rejects broken stores" a claim about a few seeds. Guarded by reporting the rejection rate per store and requiring it above a floor, and by (c), which must be accepted every time.
- **Sabotage:** S-store-1, S-store-2, S-store-3
- **Verifiable here:** yes — locally.
- **Prediction:** Store (a) is rejected mostly *without* faults: replication delay (1–10 units) alone opens the stale-read window when a client reads just after another client's write, so its failures come from the workload's read-after-write density, not from the schedule. Store (b) is rejected only when a crash hits the primary between an acknowledgement and its replication: an interleaving of the P1-10 kind, and rare. **Observable:** store (a)'s rejection rate on fault-free schedules is within 10 points of its rate under the default mix; store (b)'s is below 10% under the default mix and above 50% with P2-02's state-triggered crash.
- **Outcome:** pending

### P2-10 — Linearizability as a failure signature: shrinking a rejected run

- **Task:** A checker rejection becomes a failure signature (`linearizability@<key>`), so the P1-11 shrinker reduces a broken store's failing schedule end to end. A test shrinks a store-(b) failure and requires the result to contain the crash of the primary. The shrunk result reports which faults the swap probe marks order-sensitive. Phase 1's finding applies here too: removal proves the kept events are sufficient, not that each is a cause.
- **Vacuity:** A signature keyed on "some key rejected" lets the shrinker converge on a different anomaly at a different key, as P1-11's first sabotage showed. Guarded: the signature carries the key, and a test plants two anomalies on different keys and shrinks each to its own.
- **Sabotage:** S-shrink-4
- **Verifiable here:** yes — locally.
- **Prediction:** A store-(a) failure shrinks to zero faults, because its anomaly needs none (P2-09's prediction), while a store-(b) failure keeps the primary's crash and restart and at least one other event whose role is timing. That is phase 1's 5-versus-3 finding recurring on a real anomaly. **Observable:** the store-(a) shrink returns an empty schedule with the same signature; the store-(b) shrink returns at least three events, including the crash.
- **Outcome:** pending

## Sabotage ids

New ids continue the existing series: S-hist-6/7 follow S-hist-1..5, S-cov-4..6 follow S-cov-1..3,
and S-shrink-4 follows S-shrink-1..3. Each id's `sabotage/<id>/` entry lands in the same commit as
the check it proves; `gates breakdown` requires it once the task has its first commit.
