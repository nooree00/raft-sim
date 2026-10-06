# Findings log

## Patterns

Numbered when a lesson has recurred often enough to be named (reviewer, at phase 8's approval).

1. **The oracle stands outside the thing it tests.** A check whose answer comes from the system
   under test reproduces that system's bugs exactly. Each time this project needed a new oracle, it
   had to be placed where the system could not influence it: the simulator's ghost entry ids, not
   the node's command bytes (spec §5); commitment in fact, computed from the copies the simulator
   saw, not the leader's commit index (P4); the configuration the simulator records, not the one
   the node reports (phase 6, decision 6); the entries a snapshot covers, recorded at compaction,
   not read from the node (phase 7, decision 5); and a client retry recorded as the operation it
   is, so that "applies once" is judged by the client history, not by a replay that runs the same
   state machine whose deduplication it checks (phase 8, decision 1).

Spec §12: the bugs are less valuable than the reasons the tests did not catch them. One entry per
finding: what happened, why no existing check caught it, what now catches it.

## Phase 0

- **The workflow could not run git at all.** actions/checkout marks the workspace safe only under a
  temporary HOME; later steps in the container job hit "dubious ownership". *Not caught* because
  nothing had run in a container job before. *Now:* `ci-preflight.sh` marks it safe; the preflight's
  history check is what failed first.
- **A target-level `TreatWarningsAsErrors=false` is invisible to evaluation.** *Not caught* by the
  preflight, which reads evaluated properties. *Now:* the CI build passes `-warnaserror` on the
  command line (S-pre-6).
- **My manual sabotage runner reverted sources but kept binaries**, so the next baseline ran stale
  code. *Not caught* because the runner trusted the revert. *Now:* the harness rebuilds and requires
  the baseline assembly hashes after every revert.
- **Three layering sabotages only "worked" without locked restore.** My hand checks ran without
  `CI=true`; under CI the new reference broke restore, a build error rather than a catch. *Not
  caught* by the hand check, which tested a different configuration than CI. *Now:* the harness
  runs under CI settings and classifies build errors separately; the patches carry lock-file changes.
- **A comment-only edit changes the dll** (its embedded PDB id hashes the source). The harness's
  not-compiled-in guard therefore detects "the build never consumed the patch", not "the IL is
  unchanged"; S-meta-3 was rebuilt around a file no project compiles.
- **Two TRX files collided** in the harness worktree (xUnit names them user_host_timestamp) and the
  floor reported a project with no results. *Caught by* the floor, as designed. *Now:* one named
  TRX per project.
- **Sabotage patches rot** when the files under them change (S-pre-4 after a workflow edit, S-layer-3
  after a new project changed a lock file). *Caught by* the harness as apply-failed / build-error on
  its next run, never silently.
- **The layering test's package-reference check had no sabotage.** *Not caught* because nothing
  checks that every assertion has a sabotage — only that every task does. *Now:* S-layer-5.
  Residual: the gap class remains; the breakdown gate works per task, not per assertion.
- **Following the README ran zero tests and could have gone green.** Each `scripts/in-sdk.sh` call
  is a fresh container; the NuGet cache filled by the build step was gone by the test step, so
  `dotnet test` found no test projects — and exited 0. *Not caught* by CI's own test job, which runs
  build and test in one container. *Caught by* the README walk, and within it by the test-count
  floor, not by `dotnet test`. *Now:* the cache is a named volume.
- **The every-commit check collided with itself.** Its worktree had a fixed path, and the tests of the
  commit being checked run `EachCommit` on fixtures — which tried to create a worktree at that same,
  occupied path. *Not caught* locally, where the checked commits predated those tests. *Caught by*
  CI's each-commit job on the first commit that contained them. *Now:* a unique path per run (the
  harness keeps its fixed path, which incremental builds need).
- **A documentation commit turned the working branch red.** S-reg-1's patch created
  `docs/phases/P0/report.md`; committing the real report made the patch stop applying, and CI run
  36337685451 on `7380c6d` failed. *Not caught* because I pushed a docs-only commit without running
  the harness, on the assumption that documentation cannot break it — false whenever a sabotage
  patches documentation. *Now:* S-reg-1 no longer depends on the report's absence; and the harness
  runs locally before every push, documentation included.

## Phase 1

**The limits of shrinking — the most important finding of phase 1** (reviewer, at acceptance):

- **A 1-minimal schedule can still carry faults that matter only through absolute timing, and
  nothing distinguishes them from noise.** The planted duplicate-then-crash bug needs the crash
  inside a 1–3-unit write window. An earlier crash and restart of the same node redraws its
  stagger, and that decides where the windows fall. Removing that pair moves the windows away from
  the planted crash, so ddmin must keep it. The result is 1-minimal (no single removal still fails)
  but has 5 events against a conceptual 3. *Not a shrinker bug:* the result fails with the right
  signature, and no event can be removed. *A limitation of the technique,* found in phase 1 where
  it cost nothing: a shrunk Raft schedule can contain faults whose only role is timing, and neither
  ddmin nor the swap probe detects them. Moving faults in time is not something the shrinker tries.
- **Removal proves an order is sufficient, not that it is the cause.** Removal never reorders what
  it keeps, so a failing schedule shrinks to its events in the failing order (the ordering case
  shrank to exactly [Drop, Duplicate]). That shows the order suffices. Only the swap probe, which
  exchanges the two times, shows the order is part of the cause.

- **Lock-file-carrying sabotage patches went stale a third time** (S-layer-1/2/5, when P1-03 added a
  project). *Caught by* the local harness run before the push, as apply/build errors. *Now:* the
  cause is removed rather than the patches regenerated again — dependency sabotages declare
  `restore: force` and carry no lock-file changes; the harness re-evaluates restore for that build
  only, and the revert's baseline-hash check proves the tree came back.
- **Two test-baseline miscounts in one task**: I wrote 9 for a project that runs 7, and missed that
  a new layering-table row adds two theory cases elsewhere. *Caught by* the floor, both times,
  before the push.
- **A latent runner bug: a write could never complete.** Disk latency was drawn per write, so a
  later write could be scheduled to complete before an earlier one; its completion then saw another
  write at the head of the queue, took itself for lost, and every send behind it was held forever.
  *Not caught* because the counter protocol writes once per 100 units against 1–3 units of latency,
  so writes never overlapped. *Found* by reasoning while designing the slow-disk fault, before any
  test hit it. *Now:* completion times are monotone in issue order.
- **The traceability table traced F2-13 to members that cannot carry it.** "Delete the existing
  entry and all that follow" was traced to append and write-at, neither of which can shrink a file.
  This is exactly the residual the P1-01 task named (a member that exists but cannot carry its rule),
  found when the torn-write fault showed the trivial protocol could not drop a torn tail either.
  *Now:* `PersistTruncate`, and F2-13 re-traced. Residual: the same class may hide in other rows until
  phase 3 uses them.
- **The canonical trace was ambiguous.** A field value containing spaces and `=` split into different
  fields on reading. *Caught by* a disk-fault test that parsed it. *Now:* the trace rejects such
  values at write time, and the contract says so for node events.
- **A "behaviour-preserving" rewrite changed every random draw.** Replacing string interpolation
  with invariant formatting (the simulator's stream purposes must not depend on the machine's
  locale), I dropped one word from a purpose: `node:n1:incarnation:1` became `node:n1:1`. *Caught by*
  the golden trace, on its first day: 2,280 lines moved. *Now:* the purpose text is restored; the
  golden trace is the check that such rewrites are what they claim.
- **A wall-clock assertion failed under the harness's load.** P1-04's runner test required ten
  simulated minutes in under 10 s of wall time (1.6 s alone). With the harness on four parallel
  workers, S-count-1's unpatched baseline went red; six concurrent copies of the suite reproduced it
  in four (10.1–11.4 s). *Not caught earlier* because each earlier harness run happened to leave that
  test enough CPU. *Now:* the test bounds the step count, which is deterministic and is what drives
  the wall time; the wall time is reported, not asserted. Rule taken: no wall-clock assertion in a
  suite the harness runs.
- **The each-commit gate pruned a worktree it could not see.** `gates each-commit` runs
  `git worktree prune` inside the container, where a worktree outside the mounted repository looks
  missing; my scratch worktree's metadata was removed (its files were not). Harmless in CI; locally,
  scratch worktrees must live where the container can see them.
- **Coverage fact, not a bug: some events are effectively untested at 200 executions.** P1-10's
  "crashed" and "crashed with unsynced writes" were 153 against 8, because a write is in flight about
  2% of the time. Measured at acceptance over the same 200 executions:
  - never: a Reordered crash that reorders anything, a node fully isolated, all nodes down;
  - 1: two writes in flight at a crash;
  - 9: two of three nodes down.

  Time-placed faults meet the node's state only by chance. *Now:* phase 2's P2-02 (state-triggered
  faults and a coverage floor), and the table is in `docs/phases/P2/breakdown.md`.
- **A coverage gate fails only the dimensions it lists; an unlisted event is invisible, not 0%.**
  Three events phase 3 depends on happened in none of 200 executions, and the P1-10 gate was green:
  - writes actually reordered at a crash, which AppendEntries' consistency check exists for;
  - a node fully isolated, which is how two candidates reach the same term;
  - every node down, spec A6's "any crash schedule including all nodes".

  None had a dimension. The reviewer's general form: a dimension named after a *fault* measures the
  injection; one named after an *effect* measures the test. Two of the ten dimensions were
  injection counts ("delayed" counted the Delay fault firing, "paused" counted resumes). The P1-01
  traceability table's "stressed by" column names faults too; F2-01 and F2-04 cite crash-reordered,
  whose effect never happened.
  - *Correction:* the network-reorder dimension (189/200) was already an effect. The "69" I first
    reported for reordering was my scratch test mis-parsing an empty write list for disk crashes in
    the reordered-writes loss mode; the true figure is 2.
  - *Not caught* because the 0%/100% rule can only fail a dimension that exists, and the dimensions
    were chosen from the fault list.
  - *The general form* (reviewer): a coverage dimension list derived from the injection mechanism
    cannot contain an event the mechanism does not name. And a gate that fails only the dimensions
    it lists cannot report an absence: no signal, not a zero someone might notice.
  - *Now:* P2-02. Dimensions become effects the algorithm depends on, and the traceability column is
    rewritten in them and required to clear a floor, so the list of events is owned by the rules,
    not by the faults.
- **Five Core interface members were never driven by any test.** `ClientRequest` is never
  constructed. No node emits `ClientResponse`, and the simulator's barrier release discards it
  anyway. `PersistWriteAt`, `PersistRename` and `PersistDelete` are never constructed; `SimDisk`
  implements them untested.
  - *Scope:* no P1 assertion depended on a client response (there is no client), so nothing needs
    re-running for that. But P1's done criterion, "every fault in §7 … has a test proving it
    fires", was not met for rename, and phase 1 was accepted with that gap unreported: my miss.
  - *Latent bug, found by reading, no test yet:* after a completed rename, the lose-synced-write
    control restores the source file and leaves the destination.
  - *Not caught* because the traceability test proves a member *exists*, which is all it was
    written to prove. The residual the P1-01 task named, "a member that exists but cannot carry its
    rule", is wider: a member can exist and never be carried at all.
  - *Now:* P2-03. A census over test traces requires every Core input and effect type to reach the
    world in at least one simulator test.
  - *The pattern* (reviewer): measure use, not existence. P2-03's check reads what reached the
    world, not what was declared or constructed. Any later "every X is covered" check follows it.

## Phase 2

- **P2-01's first push went red on GitHub: the sharded harness jobs could not run git.** In GitHub's
  SDK container the checkout belongs to another user, so git refuses it ("dubious ownership") until
  it is marked safe. The old build job ran the preflight first, and the preflight marks it; the new
  `sabotage i/n` jobs start with the build and the harness, and nothing marked it. Every shard
  failed in under a second (run 36374522047).
  - *Not caught locally:* my container wrapper and `scripts/in-sdk.sh` both mark the checkout safe,
    and the local container runs as the checkout's owner. The local mirror was more permissive than
    CI in exactly this respect.
  - *Reproduced* in a container with a copy owned by another uid and no safe.directory setting, and
    the fix shown there.
  - *Now:* every CI script that runs git or the gates marks its own checkout safe. A test
    (`CiScriptTests`) requires it, and sabotage S-ci-2 removes it from the shard script.
- **The rename bug, recorded red before the fix** (P2-03). Run against the unfixed `SimDisk`:

  ```
  failed DiskOperationTests.LosingASyncedRenameRestoresBothFiles
    Assert.Equal() Failure: Strings differ   Expected: "old"   Actual: "new"
  failed DiskOperationTests.LosingASyncedRenameOntoNothingRemovesTheDestination
    Assert.Null() Failure: Value is not null
  ```

  - *Cause:* the lose-synced control's undo record held one file, and a rename changes two.
  - *Now:* the record holds every file an operation changed. Sabotage S-iface-1 reinstates the bug on
    every push and expects the test to catch it.
  - *The same first run* also failed a torn write-at test, but that failure was mine, not the disk's:
    I expected a 3-byte prefix written at offset 4 of an 8-byte file to truncate the file, and it
    does not. The expectation was fixed, not `SimDisk`.
- **The census found a gap in its own scenario list on its first run.** `PersistTruncate` was
  driven by none of the three runs chosen to exercise every member. Echo-counter recovery issues it
  only after a torn crash, which the preset schedule never produced. Measuring use rather than
  existence caught the choosing, too.
- **An equivalent mutant in the WGL checker: "may take effect" equals "must take effect".** The
  first S-wgl-1 forced every indeterminate operation to be linearized, and it survived. That is
  correct, not a gap. An indeterminate operation's output is unchecked, so the search can always
  place it last, where no later operation observes it. For the checker, "an indeterminate operation
  may or may not have taken effect" accepts exactly the histories that "it took effect, at some
  point after its invocation" accepts; spec §6's warning is about treating it as *failed*
  (excluded), which S-wgl-1 now does, and which is caught. *Consequence:* a test of indeterminate
  handling must include a later read that observes the operation, or it cannot tell the two apart.
- **My manual sabotage checks left sabotaged binaries behind.** After verifying S-hist-7 by hand I
  reverted the source but did not rebuild. The next build failed on an analyzer rule, so the tests
  ran against the stale, sabotaged `Raft.Checker.dll`: four confusing failures, which cleared once
  it was rebuilt. The harness rebuilds after every revert and checks the assembly hashes (P0), for
  exactly this reason; the hand loop did neither.
- **Rejecting a long Append-heavy history is infeasible for the checker; phase 3 must bound it.**
  A 200-operation Append-only history on one key, ending in a read no order explains, is undecided
  at 10^6 states even at concurrency 1. Adjacent intervals that touch are concurrent (real-time
  order is strict), and an Append's state is the whole string, so memoisation never merges two
  orders. Put-only reaches 246,785 states at concurrency 8, mixed 883,438 (one run at 10^6; the
  suite's table runs at 10^5). *Consequence for phase 3:* the client workload must bound Appends per
  key, or spread them over many keys, where per-key decomposition keeps rejection cheap (162 states
  over 20 keys against 5,920 on one, for the same mixed history). Linearizable histories are cheap
  either way: the search finds the first order at once (200 states for 200 operations).
- **A simulated history needs two modelling rules the checker cannot supply** (P2-09).
  1. *A client that times out moves on, but its operation stays open*, so the client's next
     operation overlaps it, and a sequential client cannot express that. As in Knossos and Jepsen,
     a timed-out client is treated as crashed, and its later operations belong to a fresh logical
     client.
  2. *Real-time order is strict.* A client that issued its next operation at the instant its reply
     arrived made its own consecutive operations concurrent. That gave the checker freedom to
     explain anomalies away: store (a)'s rejection rate doubled (13 to 26 of 40 fault-free runs) once
     clients waited one unit.

  *Caught by* `History.Problems` on the first recorded history, whose overlapping operations for
  one client it rejects. Nothing else would have noticed: the checker happily accepts the extra
  concurrency.
- **A state-placed fault removes phase 1's timing enablers.** Shrunk end to end (P2-10), a
  store-(b) failure, an acknowledged write lost to a crash, came down to one event in 13 runs: the
  state-placed crash itself. Phase 1's time-placed version of a similar bug kept a crash and restart
  whose only role was to shift the node's phase into a write window (5 events against 3). A crash
  that fires *when* a write is in flight needs no event to arrange the timing. *Consequence for
  phase 3:* state-placed faults are also the way to keep shrunk Raft schedules free of timing-only
  events, where the property allows it. A store-(a) stale read shrank to no faults at all: the
  anomaly needs none.

- **Sabotage S-det-1 could survive, about one run in 36, and did on the P2 stack.** Since P1-08 it
  ordered the three FINAL lines by `string.GetHashCode`, which .NET randomizes per process. The
  target compares three processes, so it passes whenever all three draw the same order of three
  hosts: (1/6)² ≈ 1/36. It had passed every earlier run.
  - *Not caught:* the harness runs each entry once. A sabotage whose catch is itself random looks
    exactly like a reliable one until the unlucky run.
  - *Now:* the sabotage writes the hash value into the trace line, so every process differs. The
    bug class is unchanged: process-randomized state reaching the output. The rule going forward:
    a sabotage must be caught with certainty, not with high probability. A random element in a
    patch needs a reason why it cannot come out equal.
- **Sabotage S-rare-1 never caught its target.** It makes a state-placed crash ignore `MinPending`,
  and it named the reachability test for reordered writes as the check that catches it. That test
  passes with the sabotage in place: its slow disk (150 against a 100-tick period) has two writes in
  flight whenever the crash can fire, so the reordering is reached with or without `MinPending`.
  The sabotage was committed on the reasoning that it would be caught. It was not run on its
  commit: the full harness ran only at the stack head, and there it reported "survived".
  - *Not caught earlier:* I ran the harness locally only at the stack head. Per commit, CI's matrix
    would have gone red on P2-02's own commit.
  - *Now:* S-rare-1 targets the check that exercises `MinPending` directly. A crash that needs five
    writes in flight never fires under the echo protocol, and must be traced UNFIRED. The
    reachability test's claim does not depend on `MinPending`, and its comment no longer says it
    does. The general form: a sabotage's target is chosen by running it, not by reading.
- **A sabotage verified only at the stack head is verified against the final code, not its own
  commit** (reviewer's general form, from S-det-1 and S-rare-1). Every intermediate commit's
  sabotages are unverified. The manifest only grows, so the gap grows with every phase.
  - *The two cases fail in opposite directions.* S-det-1 was caught 35 runs in 36. A flaky sabotage
    is worse than a flaky test: a test that passes wrongly gets investigated, while a sabotage that
    survives once reads as "the guard has a gap" and sends the search to the wrong place. S-rare-1
    was never caught. That is the fourth time, across two projects, that a target chosen by reading
    rather than by running aimed a sabotage at the wrong thing.
  - *Now:* CI's per-commit matrix runs every commit's own harness shards. Locally, `gates
    each-commit` does the same before each push. Landing P2-01..P2-03 before the checker work is
    what made this catch possible inside the phase. An open register row audits whether each
    target fails on its sabotage's mechanism or only incidentally, since the harness checks that
    the target goes red, not why.
  - *Lineage:* this is the shape of the CRDT project's §13.31, two mechanisms with one observable
    and the weaker silently standing in for the other, now showing up in the sabotage manifest
    itself. A target that goes red through an incidental path (the golden trace goes red at almost
    any simulator change) looks the same as one that goes red through the mechanism.
- **A forcing prediction reads more like a success than any other outcome** (P2-06). The
  constructed generator's first rate, 77%, fell outside the predicted 25–75%. I changed the
  mutation share until it came inside (66%). That turned the prediction into a target the work was
  steered to, not a claim the work could refute. It is exactly the failure mode the
  evidence/forcing classification exists to catch, and it would have been easy to count as
  "right". *Now:* it is classed forcing and excluded from the counts.

## After phase 2

- **The secret scan reported a dead Docker daemon as "leaks found".** During P2's report run the
  daemon went down; the scan printed `secret-scan: leaks found (exit 1)`. I read it correctly only
  because I knew the daemon had died. The message alone said the opposite of what happened, in the
  gate whose false positive is most alarming and whose false negative would be worst.
  - *Why:* the script treated every non-zero exit of `docker run … gitleaks` as a finding. gitleaks
    exited 1 on leaks, and the docker CLI also exits 1 when it cannot reach the daemon; a gitleaks
    error is 1 too, a bad flag 126, a missing docker 127 (all measured). Exit status 1 could not
    distinguish "asked and found something" from "could not ask".
  - *Not caught:* S-sec-1/2 plant a real secret and require the message, so they prove the scan
    fires. Nothing ever took the scanner away and asked what the gate says then. The CRDT project's
    gate that cried wolf had the same shape.
  - *Now:* three outcomes. gitleaks runs with `--exit-code 3`; 0 is clean, 3 is "leaks found" (exit
    1), and anything else is `NO VERDICT` (exit 2), which is neither a finding nor a pass. Tests
    with a fake `docker` cover each measured exit (`SecretScanTests`). S-sec-3 (every failure a
    finding again) and S-sec-4 (a finding rendered as no verdict) sabotage the distinction both ways,
    and S-sec-5 takes the real daemon away on the host. S-sec-1/2 now require the script's own line
    (`secret-scan: leaks found`): gitleaks prints its own "leaks found: 1", which would have matched
    too. The rule going forward: a gate that depends on a tool must say when the tool did not run,
    and a harness entry must take the tool away to prove it.

## Phase 3

- **Two sabotages were caught for the wrong reason, and had been since they were written.** S-cov-5
  renamed the isolation dimension; the undeclared name crashed the shared coverage measurement
  with a `KeyNotFoundException`, so its target went red before reaching the assertion the entry
  claimed to test. S-cov-6 lowered the floor in one of two comparisons; a dimension with one or
  two hits then fell into the other branch, which read a dictionary that did not hold it, and
  threw. Both entries reported `caught` on every push.
  - *Not caught:* the harness checked that a target went red, never why (the P2 register row).
    Red-by-exception and red-by-assertion looked the same.
  - *Now:* the harness prints each caught target's own failure message. A `reason:` makes the
    message a requirement, `wrong-reason` otherwise; broad targets must have one
    (`ci/broad-targets.txt`), and every entry names its `mechanism:`. The audit table
    (`docs/design/sabotage-audit.md`) records the message each entry produced. The two patches are
    repaired to reach their assertions, and the rules test names each rule in its message.
  - *Lineage:* the CRDT project's §13.31 shape, two mechanisms with one observable (here, "the
    target went red"), in the sabotage manifest itself.
  - *General form* (reviewer): **a failure that precedes the assertion is not evidence about the
    assertion.** Same family as the Z.1 fixture rejected for having no task sections, and the
    sabotage that fired before reaching the code under test.
  - *Reading the rate:* two of 120 is also the rate the old harness could report by construction.
    Any entry whose incidental path to red was a crash, not a distinguishable message, would have
    passed forever. The `reason:` requirement is what makes the count meaningful rather than a
    lower bound, and only for the entries that state one.
- **Tests first for the election (P3-05), and a test of mine that failed before its assertion.** The
  13 election tests were written against a stub node that returns no effects, and all 13 failed
  there. Two of them failed for the wrong reason: they sorted node ids with `.Order()`, and
  `NodeId` is not comparable, so they threw before reaching their assertions. Against the stub that
  looked like the expected red; it was the general form above, in a test I had just written.
  - *Found:* when the implementation arrived, those two still failed, with the same exception.
  - *Now:* they sort by value. Re-run against the stub, they fail at their assertions: one on the
    persisted vote, one at its precondition, because the node never stands. The red run is kept in
    the phase report.
  - *Consequence:* a tests-first run shows only that each test goes red. Reading why each went red
    is the part the run does not do by itself, the same gap P3-01 closed for the harness.
- **Invariant 11 in its phase-3 form cannot see disruption.** Under a one-way partition (n3 sends,
  hears nothing), with the §6 rule removed, n3's candidacies depose the leader about every 900
  ticks: 20 leaders per run over 200 runs, against none with the rule. "An elected leader acts
  within K election timeouts" still holds in every run, because each deposed leader is replaced
  within a few hundred ticks. P3-06 predicted it would fail in at least half the runs; it failed in
  none.
  - *Why:* the phase-3 scope of invariant 11 (decision 3) keeps only "a leader exists"; the clause
    that disruption breaks, "a submitted command commits", needs a log. A leader that exists for
    900 ticks at a time is a leader for this property and nearly useless for clients.
  - *Now:* the rule's test asserts its own effect, no leader deposed after the first window, and
    the rule-off measurement is reported. S-disrupt-1, removing the rule, is aimed at that
    simulated test (leader continuity alone would let it survive). Phase 4's commit clause should
    make the churn visible to the invariant itself; the P3 report carries it forward.
- **The fsynced-then-lost positive control is loud in one invariant and quiet in the two the spec
  names.** Losing a node's first fsynced vote record, state-placed right after the vote is
  answered, turns term monotonicity red in every run: the rollback lowers the durable term. Election
  Safety or Vote Uniqueness goes red far less often: 10–17 of 200 runs with the node back 5 ticks
  later, none when it is back after 50 or more (the election is over before it returns), and 39 of
  200 in the spec §8 shape (n1 and n3 unable to hear each other, the node back after 20). P3-07
  predicted more than half for the state-placed loss.
  - *Why:* a lost vote only matters if a second candidate in the same term reaches the voter after it
    restarts. Once a leader heartbeats, the restarted voter adopts the term and never votes in it
    again. Two candidates in one term are what the spec's example assumes; in fault-free timing they
    happen in 1.5% of elections (P3-05).
  - *Consequence:* the control is asserted as the spec states it (Election Safety or Vote Uniqueness
    red, in at least the coverage floor of runs, naming the node), with term monotonicity naming it
    in every run, and the same crash without the fsynced loss green in every run. Invariant 9 is the
    one that catches an unpersisted term directly, as spec §5 says it would.
- **The soak's first run found a node that could not restart: recovery cut a torn tail only in
  memory.** P3-03's recovery read past a torn final record, and P3-05's node then appended its next
  record after the torn bytes. The next crash met them mid-file: "record 10 at byte 200 fails its
  checksum and is not the last: corruption". The node refused to start, as designed, and the
  timing run of 300 generated executions threw.
  - *Not caught:* P3-03's tests recovered one crash at a time and never appended after a recovery.
    Spec §8 says recovery "truncates a torn final record"; my recovery computed where to cut and
    left the cutting to no one.
  - *Now:* a node that recovers a torn tail emits `PersistTruncate` to the valid length before any
    other effect. Tests written first and run red: the first effect after a torn recovery is the cut,
    and a torn crash, restart, new record and second recovery comes back clean. Sabotage S-elect-6.
- **Three checker defects, found by the first soak-scale runs, each a false positive on a correct
  node.** Over 300 generated executions the checkers reported ten violations, none of them real:
  - *Order:* term monotonicity merged durable states and sends with a sort by time. A paused node
    working through its backlog persisted terms 2, 3 and 4, each reply released behind its own
    record, all at one instant; the sort put the final term 4 before the term-2 replies.
  - *Stale grants:* a grant delayed past its candidate's next candidacy was counted toward the old
    term, which "elected" a node that had already moved on.
  - *Silent step-down:* a leader that sees a higher term in a response steps down without sending
    anything; the "elected but never acts" check did not count the durable higher term as leaving.
  - *Not caught:* the hand-built traces put one event per instant, never delayed a grant past a new
    candidacy, and never stepped a leader down on a response. Each is now a twin trace.
  - *Now:* the checkers follow the observation stream's own order (each record keeps its position),
    count a grant only while its candidate's latest durable record is that term with a vote for
    itself, and treat a durable higher term as leaving office. 1,000 generated executions: no
    violation. Sabotages S-inv-6..8.
  - *Consequence:* a checker proven only on hand-built traces is proven on the traces its author
    thought of. The same lesson as the S-rare-1 target, one layer up.
- **The 10,000-execution soak found a fourth checker false positive: a grant arriving while the
  candidate's next record was still being written.** n2's term-11 candidacy was durable; n2 stood
  again in term 12, and while that record was in flight n3's term-11 grant arrived. The checker
  judged candidacy by the latest *durable* record, still term 11, and counted the grant; the node
  had already moved to term 12 and ignored it. Reported as "elected, never acts"; it could as well
  have reported two leaders in term 11.
  - *Now:* the simulator also observes issued writes (with their bytes: what the node decided, not
    what it said about itself), and a grant counts only while the candidate's latest record,
    issued or durable, is that term with its own vote; after a crash, only the durable one.
    Sabotage S-inv-9. 10,000 generated executions: no violation.
  - *Consequence:* the durable view lags the node by one disk latency, and a checker that reads only
    it will misjudge anything that happens inside that lag. The P3-04 prediction was about the wire
    missing durable votes; the soak found the reverse, the disk missing decisions in flight.
- **The soak costs less than predicted, and its value was in the first minutes.** 10,000
  generated executions of 20,000 ticks take about 4 minutes locally (4 min 17 s in the SDK
  container); P3-08 predicted at least two shards on GitHub. Before the soak existed as a job, its first runs found the torn-tail restart bug and four
  checker false positives, none of which 300 executions or the hand-built traces had shown.
  - *In the sample:* one effect, writes completed out of order at a crash, is 0 of 300 with Raft
    nodes (8 of 10,000 in the soak): a node writes only when its term or vote changes. It is
    declared rare for the sample only, with that reason; the soak must still clear the floor.
- **The coverage floor is absolute, and weak at soak scale.** P2-02's floor is 3 executions whether
  the run is 200, 300 or 10,000. At 10,000, writes completed out of order at a crash clear it with
  8 (0.08%); a floor proportional to the count would have flagged them. Decided at P3 acceptance
  (P3-10): the floor is max(3, 1% of the executions), the soak holds the same rate with its own
  floor, and the rate is printed beside the count wherever a floor applies. The count alone is what
  let 0.08% through.
- **A test that passes against a stub is a test whose subject is not in the code under test.** When
  the election tests were re-run against an injected stub (`Handle` returning no effects), one
  passed: `ACorruptTermVoteFileRefusesToStart`. Its subject, refusing a corrupt record, lives in the
  constructor, which that stub kept. Against the first stub, whose constructor did nothing, it
  failed. A tests-first run shows which tests go red; a green one there is a statement about where
  its subject lives, and the stub has to remove that place too.
- **A report commit that is not the head of its push can never be certified.** The P3 acceptance
  push carried the report's commit and then the P4 breakdown. GitHub runs CI for the head only, so
  the report's commit had no run of its own, and `gates reports` failed the head's run: "no ci
  workflow run for this commit". Every other job was green.
  - *Not caught:* `gates reports` needs the GitHub API and is the one gate the local sequence
    skips, so the full local run was green.
  - *Now:* a new head commit touches the report, so it certifies a commit CI runs.
  - *Proposed, not built (phase 4 has not started):* make the rule mechanical. `gates each-commit`
    can fail any non-head commit in the pushed range that changes a `docs/phases/*/report.md`.
    That is a local check, and it would have stopped this push. The alternative, letting
    `gates reports` accept the per-commit jobs for that sha in a later run, weakens "the run for
    that commit" and I do not propose it.


## Phase 4

- **An index in each log record was not enough: a surviving write can depend on a lost one.** P4-02
  predicted that records carrying their index, with a later record for an index overriding
  everything from it, recover correctly from any crash. The crash-during-write test (3 loss modes,
  200 seeds, 3 crash-and-restart cycles each) found 3 reordered crashes where they did not. A
  truncate-and-append of term-4 entries at 4..6 was lost, the next append (7..9) survived, and it
  landed right after the old 4..6: contiguous, so accepted, and the recovered log joined two logs
  that never coexisted.
  - *Not caught by the design:* later-index-wins handles a lost truncate followed by an append that
    rewrites the same indices. It does not handle an append whose predecessor write was lost when
    the indices happen to line up.
  - *Now:* each record also carries the previous entry's term, and recovery takes a record only if
    it chains onto what it has recovered: AppendEntries' consistency check, on disk. It is sound
    because within one node's writes an (index, term) is always the same entry, the induction Log
    Matching rests on. The approved decision 4 said records carry (index, term, command); they now
    carry the previous term too (the report lists it as a deviation). S-logfile-1 removes the chain
    and the crash test goes red.
  - *The control as predicted:* a format without indices, read in order, fails 60 of the same runs.
  - *Also found while writing it:* truncating from an index must cut the file at the end of the live
    entry before it, not at the live record itself. After a lost truncation the live record can sit
    after stale records, and cutting at it brings them back (S-logfile-3).
- **The clock-skew effect measures the protocol, not the clock, once nodes write logs.** P2-02
  defined `clock-rate-diverged` as one node's write rate diverging from the others' by 8% or more.
  An echo node writes on its ticks, so for it the measure follows the clock. A Raft node's writes
  follow its role, and once the leader writes every entry first, the effect fired in 290 of 300
  soak-sample runs. The P2 rule (95% or more must be declared) caught it.
  - *Now:* declared always-on in the soak, with that reason, until P4-07 measures skew by what it
    does in state, the node's perceived time, independent of protocol.
  - *General form:* an effect defined through a side effect of the node under test measures that
    node. It stays valid only while the node's behaviour holds still.
- **A deposed leader truncated its log before persisting the term that deposed it.** The log
  checkers' leader append-only check fired on the first simulated runs with one-way partitions. A
  leader of term 1 was still in office by its durable record when it truncated its suffix, because
  it adopted term 2, applied the new leader's entries, and saved the term only at the reply. The
  truncation is a decision the new term authorises, so the term is persisted first now, in the
  `AppendEntries` path before any log write.
- **The crash tests cannot see a leader that sends before it persists.** S-dur-3 moved the leader's
  write of a client entry after the AppendEntries that carry it. 100 crash runs (every node or a
  majority at once, 16% catching a write in flight under the patch) stayed green. The leader's own
  write takes at most 3 ticks, and the first acknowledgement it could count takes at least 3 (two
  hops and a follower's write), so the window where it counts a copy it lacks is at most one tick.
  - *Now:* the order is held by a unit test on the effects (`ReplicationTests`), and S-dur-3 targets it.
  - *General form:* a timing window the simulator's latency bounds close is out of reach of any
    schedule of faults. Holding such a property needs a test of the order itself, or bounds that open the window.
- **An early acknowledgement is a spurious claim, not a lost commit.** P4-05 predicted that a
  follower acknowledging before its write would break invariant 6 only when every node crashed
  inside the window. The checker counts a copy toward commitment in fact only once it is durable
  (P4-01), so the leader's claim runs ahead of the fact at every such commit, and invariant 7 fires
  in every run, crash or not (100 of 100 generated, 50 of 50 constructed; invariant 6 in none).
  - *General form:* where a checker defines the fact by durable state, a durability bug shows as a
    claim ahead of the fact, found without the crash that would make it matter.
- **A leader that can send but not hear holds office and commits nothing.** P3-06's one-way
  partition cuts n3 off from hearing its peers. In 49 of 200 seeds n3 was the leader when it began:
  its heartbeats still arrive, so n1 and n2 never time out, and their acknowledgements never reach
  it, so nothing commits until the partition heals. Leader continuity passed in all 49; the commit
  clause (P4-06) is the first check to see it. It is not a bug under this spec: CheckQuorum, the
  remedy, is out of scope (spec §2), and invariant 11 asks for a commit only after faults heal.
  - *Now:* those seeds are counted and reported, not asserted; the clause is asserted on the rest.
  - *For the reviewer:* if a never-healing one-way partition around the leader should be survivable,
    that is CheckQuorum, a scope change.
- **The commit clause is blind to the disruption it was predicted to expose.** With the §6 rule
  off, leaders changed 21 times per run and throughput halved, but in 0 of 151 runs did 3,000
  ticks pass without a commit (P4-06, wrong). A liveness clause bounded by a window of ten election
  timeouts sees outages, not degradation.
  - *General form:* a bound generous enough to hold for every correct run is too generous to see a
    slowdown; degradation needs a rate, measured, not a bound, asserted.
- **The log checker judged a message by the sender's log at release, not at composition.** The
  first soak with logs failed log matching on seed 4: "n1 sent entry 26 of term 1 that its log does
  not hold". n1, leader of term 1, composed an AppendEntries carrying 26, held by the barrier behind
  its write of 26; it then learnt of term 2 and truncated 26; the write completed and the barrier
  released the old message. The message was true when composed, and a late release is only network
  delay. The checker (P4-01) compared it with the log at release.
  - *Now:* the simulator records the step that emitted each send (`SentObservation.Step`), and the
    checker accepts an entry a later step removed.
  - *General form:* a checker that reads an effect at the time it is observed, where the system
    decided it earlier, sees a history that never happened. Every observation needs the moment of
    decision as well as the moment of effect.
- **Clients without a pause made the soak cost an hour.** Three clients that issue their next
  operation one tick after a reply commit about 900 entries a run; an execution cost 410 ms locally
  against 54 without clients, and 10,000 would have taken about an hour, against P4-07's prediction
  that the cost would roughly triple. The cost follows the log's volume (bytes checksummed, copied,
  decoded and checked), not the number of steps (69,000 against 57,000).
  - *Now:* each client pauses 100 ticks before a new operation (`ClientCall.After`): about 145
    entries a run and 63 ms, with the commit clause checked as often.
- **With log writes, state-placed crashes fire, and the stable suffix moved.** A crash armed to fire
  when writes are in flight almost never fired on a node that wrote only term changes; with a write
  per replicated batch it fires at once, and its restart (up to 3,000 later) left too short a suffix
  in 27% of the sample, below the 75% the soak requires. The run now lasts 22,000 ticks, not 20,000.
- **`clock-rate-diverged` is now the node's perceived time.** The skew fault's line carries its rate,
  and the effect is a node up for a timeout or more under a rate 8% or more from real. It fires in
  53% of the sample, where the write-rate measure fired in 97%, and its always-on declaration is gone.
- **No test saw a leader count an acknowledgement from an earlier term.** Planned as S-soak-4, the
  sabotage (the leader's `r.Term != _term` guard loosened to `r.Term > _term`) left the whole Core
  project green, the 300-execution soak sample included. A stale success reaches a leader only if it
  led the earlier term too, and matters only if its log changed in between, so generated runs almost
  never meet it.
  - *Now:* a unit test (`ALeaderIgnoresAnAcknowledgementFromAnEarlierTerm`) and S-repl-9. S-soak-4
    now reinstates P4-03's bug, which the sample does catch.
  - *General form:* a guard whose failure needs two rare events together is not covered by a sample
    of generated runs; it needs a constructed input.
- **Two of the configured limits did not exist.** P4-09 predicted a limit in the wrong place; the
  codec's 32-bit fields carry a full batch of 1 MiB commands. Instead, commands had no bound at all,
  and no option was checked: a heartbeat just under the election timeout, or a timeout spread of
  one tick, was accepted and broke elections (57 of 100 runs re-elected; 100 of 100 never elected).
  - *Now:* `MaxCommandBytes`, and `RaftOptions.Refusal`, checked when a node is built.
  - *General form:* spec §10's rule presumes each limit is written down. The first finding of a
    limit census is the limits nobody wrote.
- **The default batch hides Figure 8.** P4-04's construction needs the old term's entry to reach a
  majority without the new leader's no-op. With 64 entries per AppendEntries the old entry travels
  with the no-op in one write and commits legitimately, so the shape cannot occur and a
  commit-by-counting bug is masked. The Figure 8 test runs with a batch of 1; a second test records
  that the default masks it.
  - *General form:* an optimisation that coalesces steps can remove the interleaving a safety rule
    exists for; the test of the rule must turn the optimisation off.
- **A bounded-liveness assertion is satisfied by a system that thrashes fast enough.** Two
  instances, one level apart. P3-06: with the §6 rule off, a node that cannot hear the leader
  deposed it about 20 times per run (3,995 leaders over 200 runs), and invariant 11's leader clause never failed, because each deposed
  leader was replaced within the window. P4-06: with the rule off again, leaders changed 21 times per
  run and committed commands halved (74,470 against 148,810 over 200 runs), and the commit clause
  never failed in the 151 runs where the cut-off node did not lead, because no 3,000-tick stretch
  passed without a commit. Both clauses are deadlines: something must happen within K. A deadline
  generous enough for every correct run is met by a degraded one that still does the thing, just
  often and badly. Liveness-as-a-deadline cannot express liveness-as-a-rate.
  - *What the rate version would need* (not built):
    - *A baseline per execution:* the same seed, workload and schedule with the fault removed, which
      the deterministic simulator gives for the cost of a second run.
    - *A rate measured over windows:* commands committed in fact per window (by ghost id), and
      leader changes per window, over the stretch the fault is meant to be tolerated in.
    - *A stated tolerance:* for instance, at least half the baseline's commit rate and no more than
      one leader change per window. A number chosen and justified, as K was, not inferred.
    - *A scope:* which faults a correct protocol must tolerate at full rate (the one-way partition
      with the §6 rule, where the rule is the claim) and which only at a deadline (a partition around
      the leader without CheckQuorum, spec §2).
    - *Its own vacuity guard:* the rate check is only as good as the baseline's rate, so the baseline
      must itself clear a floor.
  - *For later phases:* every remaining liveness criterion (membership changes in phase 6,
    compaction in phase 7, sessions in phase 8) is written as a deadline. Each should say whether a
    thrashing system that meets the deadline passes, and if it should not, carry a rate.
  - *Related:* the earlier entry on the commit clause's blindness is this finding's second instance.
- **A check made with a different tool from the one that enforces it is not the same check.** I
  checked sabotage patches with `patch --dry-run`; the harness applies them with `git apply`.
  `patch` applies with fuzz, `git apply` does not, so my check was more permissive than the rule it
  stood for. Four stale patches (S-disk-3, S-sim-2, S-loginv-7, S-soak-3) and a fifth I rewrote
  by hand (S-iface-2) passed my check. They were committed at the commits that broke them, so the
  per-commit matrix would have reported apply-failed on exactly the commits introducing the
  mechanisms those entries test. Found only because the head's full local run reached them.
  Same family as the CRDT project's two compilers: verified on one, enforced by another, nothing
  signalling that they disagree.
  - *Now:* `gates patches` runs `git apply --check --whitespace=nowarn` (the harness's command) on
    every patch and control with every build, and so on every commit in the per-commit matrix;
    S-patch-1 proves it names a patch made stale by an edit elsewhere. The stale patches were
    fixed in the commits that broke them. Nothing verifies a patch with any other tool.
  - *The sweep* (P4 acceptance: where else a check and its enforcement use different tools):
    - *Build and tests:* local runs use the digest-pinned SDK image CI uses, through the same
      scripts (`ci-build.sh`, `ci-test.sh`, `ci-soak.sh`), and preflight checks the pins. Same tool.
    - *Secret scan:* the same digest-pinned gitleaks. Same tool.
    - *Breakdown, trailers, register, testcount:* my edits are checked by running the gates
      themselves before a commit. Same tool.
    - *The harness shard count:* my local staged-CI script hard-coded 6 shards while CI derives
      the count from the manifest (`gates sabotage-plan`); at 189 entries the count became 7. A
      second instance, caught by reading, not by a run. Fixed: the local script asks
      `gates sabotage-plan`.
    - *Measuring a sabotage in a copy of the working tree* (`.sab`, uncommitted files included)
      against the harness's committed-tree worktree: a patch measured in the copy is always run
      through the harness from a commit before it is reported. Same verdict tool, different
      input; kept as exploration only.
    - *`gates reports`:* needs the GitHub API, so it has no local check at all. Not a different
      tool, an absent one; it runs in CI on the push.
    - *The README walk:* the script checks rot; the person's cold walk is the real check. Known and
      recorded (AGENTS.md).
    The sweep took under an hour; no register row.
  - *General form:* when a check is performed with a tool other than the enforcing one, either use
    the enforcing tool or make the difference itself a tested fact.
- **A tooling-cost fix landing at commit N leaves commits 1..N-1 permanently slower to verify; and if
  the fix is measured on a faster machine than the one that enforces, the phase cannot be verified
  at all.** Each commit is checked in CI with its own contemporaneous tooling (`scripts/ci-commit.sh`
  at that commit), so P4-11's split could not help the commits before it: their per-commit jobs ran
  the whole Core suite in every harness entry. The cost grows with the stack's depth and is paid on
  every run that carries those commits. P4-11 was placed correctly given what was known (the ceiling
  was met only at the head's full run), but it was measured locally and passed (slowest shard 12.6
  min), and GitHub's runners are about 1.4 times slower: the head's own shard 3 took 15.8 min there.
  - *Rule:* a tooling-cost fix belongs at the start of a phase, not at its end, or the phase's own
    history carries the old cost for as long as it is re-verified.
  - *Rule:* measure on the machine that enforces. Same family as the CRDT project's two compilers and
    `patch` against `git apply`: verified on one, enforced by another, nothing signalling that they
    disagree. P4-12's prediction names the slowest GitHub shard as its observable for this reason.
  - *What it cost here:* the per-commit matrix has a hole at P4-03..P4-12 (thirteen commits with `50f1720`, the jobs
    listed in the P4 report under "Commits never verified in CI"), four of whose jobs were cancelled
    at the 40-minute job timeout and have no verdict at all. Accepted as unverifiable in CI
    (reviewer, at the ceiling), not exempted: the commits are permanent, so an exemption would be
    too. It is S-rare-1's class returning, verified at the head and not at its own commit, for a
    bounded, named set.
  - *And the rule applied to itself:* P4-12's own prediction commit carries the tooling before
    P4-12, so it too is checked with the slow harness as predicted: in run [36810331470](https://github.com/nooree00/raft-sim/actions/runs/36810331470), `50f1720` shard 3 failed the ceiling (its checks step about 16.5 min) while the head's seven shards, with P4-12's tooling, passed (slowest 12.75 min). P4-12 was measured on GitHub, the enforcing machine, and still missed its 12-minute observable by about 0.75 min; it was under the ceiling, which is what the rule protects.

## Phase 5

- **An identifier that resolves to the wrong thing produces a green verdict about something that
  was not tested.** The first draft of the phase-5 breakdown gave P5-01 the sabotage ids S-hist-1..3,
  which P0-15 has owned since phase 0, and `gates breakdown` accepted it: it checked that each id
  resolves to an entry, not whose entry it is. Had it gone further, P5-01's `sabotage/S-hist-1/`
  either overwrites phase 0's entry, deleting a checker sabotage with every gate still green (the
  id exists and is cited), or leaves it standing, and P5-01's requirement is met by phase 0's
  checker entries: the harness reports caught against a mechanism P5-01 never touched. Same family
  as the stale patches (`patch` against `git apply`) and the two compilers: something resolves, and
  nothing checks that it resolves to the right thing.
  - *Now:* a sabotage id is owned by one task across every breakdown; another task relying on it
    cites it as `; shared:` (P5-00, S-bd-7, S-bd-8).
  - *The sweep found a second instance:* `gates register` accepted a done row whose evidence named
    any method in the built test assemblies, a private helper included. Evidence must now be a
    method marked [Fact] or [Theory], which `gates testcount` requires to run (S-reg-5).
  - *And a third, while fixing the first:* I numbered the new register sabotage S-reg-3, taken by
    P0-08 since phase 0. The new rule named it on its first run. The rule was written because I make
    this mistake; I made it again within the hour, which is the argument for the gate over care.
- **A correct fix for a wrong reason, the reason measurably wrong.** With 37 of 10,000 soak
  searches undecided, the reviewer ordered the per-key budget first, on the ground that an undecided
  verdict named whichever key was being searched when the shared budget ran out, so every hardness
  number was suspect. Measured on the eight examined seeds at three keys, that ground was false: the
  other keys cost at most 2,198 states, every undecided key kept its key, and the 53-71 operation
  range stood. The fix earned its place for two reasons nobody gave. At six keys the misattribution
  was real (a 24-operation key reported). And the old search stopped at the first undecided key, so
  a later key that could not be linearized went unreported: an undecided verdict masked a rejection.
  That is soundness, not reporting (S-wgl-6).
  - *Cleaner than most instances:* the stated reason was tested before the change landed, and the
    measurement says it was wrong, while the change is right.
- **Matching an answered write to a committed entry by its bytes fails for operations without a
  unique value. Twice in one phase, same cause, two places.** At P5-03 the soak's
  `command-retried-and-duplicated` effect counted two committed `Delete|k` entries as a retry,
  because writes were assumed unique by bytes and a `Delete` carries no value (fixed: Gets and
  Deletes are excluded). At P5-04 my check of the early-answer variant matched answered writes to
  committed entries by bytes, and the lost `Delete|k2` in seed 288 matched an unrelated committed
  one, so a catch with the predicted mechanism looked like an unexplained second mechanism. Same
  family as P5-00's sabotage ids: an identifier that resolves, to the wrong thing.
  - *Rule:* identity by content holds only where content is unique; a workload with value-less
    operations needs identity by origin (client and request id), or the check must exclude them.

- **A feature that makes under-testing expressible ships with the guard against expressing it by
  accident.** P5-07 let the test script run named projects only, so harness entries could stop
  paying for the whole suite. That opened two ways to under-test: a misspelt project name narrows a
  run to nothing, and a project list in the CI test step narrows the run that enforces everything.
  Both were closed in the commit that opened them (S-count-4, S-count-5).
  - *Rule (reviewer, at P5-07):* the pattern to keep. A change that adds a way to run, check or
    count less carries, in the same commit, the check that stops it being used where it must not be.
- **"Fixed cost" held the cost it was supposed to exclude.** The harness counts everything until
  the slowest worker reaches its first entry as fixed cost, and a command entry's baseline is run
  there. For a whole-suite entry that baseline is a full test run, so about 350 s of the 433-487 s
  read as fixed cost in four shards was the entries' own cost. The reviewer's reading (design 1
  would leave it at 433 s) was a guess about composition the code already answered; the per-worker
  split, printed since P5-07, shows it directly.
- **The measurement existed and the inference did not.** I measured that phase-5 trees before P5-07
  exceed the harness ceiling, and that measurement was the reason for P5-07; I did not connect it to
  the per-commit matrix, which runs exactly those trees. The first push's CI run went red on the
  three commits between the budget commit and P5-07's code (shard 3, 978 s of 900).
  - *Fixed by order:* P5-07 moved before the budget commit, force-pushed with the reviewer's
    approval, so the fix precedes the problem it solves (P4's rule for tooling cost).
  - *A loophole, found and declined:* a push's matrix covers only the commits since the previous
    push, so the next push would have been green while the three commits stayed red for good.
    "Every commit green" would have been false and looked true, the shape of four earlier findings
    here. Not taken; brought to the reviewer.
  - *Second instance, one layer up:* the force-push that reordered P5-07 left CI unable to resolve
    the previous head, and `each-commit-list` fell back to the merge-base with `origin/main`. I had
    checked that the fallback range covers every rewritten commit, and it does; I had not checked
    what that range does to the other rules reading it. The report-at-head rule rejected 13 phase
    reports from P0 to P4, each the head of its own push, and the run (37146478876) checked none of
    the six rewritten commits. The range was checked for one property and not the others it feeds.
    Recovered with a ref at the old head and a re-run of the same event; the defect is in the
    register (a fallback that silently changes its meaning).
- **A duration on GitHub's runners cannot falsify a prediction at the spread they show.** The same
  soak, on identical code, took 1,275 s in one run (37090873754) and 924 s in another (37146478876),
  38% apart. P5-05's prediction was wrong at either figure, but a prediction within that band could
  have come true or false by the runner drawn, which makes it no prediction.
  - *Constraint (reviewer, at phase 6's approval):* no prediction is a duration compared across
    runs. State it within one run (a ratio, or one quantity against another measured beside it), or
    in a deterministic quantity (states explored, executions, entries). Phase 6's P6-01 and P6-10
    were restated before they started.

## Phase 6

- **The first generated sample with membership changes found the checker wrong, not the node.**
  Invariant 6 (P6-04) asked a committed entry to stay durable on a quorum of the configuration at
  its index, forever. Seed 152 of P6-08's sample committed an entry under `{n1,n2,n3}`, then moved to
  a configuration that dropped two of them; the entry was left on the servers the newer
  configuration kept, which is correct. Every construction in P6-04 had stayed within one
  configuration change, so none reached it. Invariant 6 now asks a quorum of the latest committed
  configuration; S-joint-4 is the old rule.
- **A sabotage that removes a dimension can fail an invariant first.** S-cov-11 (membership requests
  only after the faults) was built by delaying each request 12,000 units, which silenced its client
  for that long; at seed 38 no client command was created in the stable suffix and liveness failed
  before the coverage check ran. The harness reported it as wrong-reason, on a commit not yet pushed;
  the rebuilt sabotage draws an ordinary operation instead. My working notes already listed it as
  caught on that commit before the harness had run it there; the run said otherwise.
- **A measure that goes red on correct code is a measure to read, case by case.** P6-07's disruption
  measure reported 120, then 21 adoptions with the rule on, against a node whose rule is one line.
  Neither was a disruption: the first counted servers with no live leader (the rule allows those
  adoptions), the second attributed a later term record to an earlier delivery of a retried request.
  Each was found by printing the flagged cases (seed, sequence, sender, receiver, terms) and reading
  them, not by changing the measure until it passed; the sabotaged node still shows 864 adoptions.
  The third error was in the control: runs without the rule were described as the same executions
  replayed, and they are other executions, since the rule changes who leads.
- **P6-07's measure was wrong a fourth time, found the same way.** With the spares quiet (P6-14), the
  disruption test went red on one adoption (seed 84). Reading it: the receiver's clock ran 20% fast,
  and it had waited the rule's 150 units on its own clock, 136 of the simulation's. The node followed
  the rule; the measure counted the window in the simulation's time. Four errors in one measure, each
  in what "a disruption" means rather than in arithmetic: an adoption the rule allows, an adoption by
  a later request, executions that were not the same ones, and a window on the wrong clock.
- **A distribution at 100% was a bug, not a fact about five servers.** P6-10's measurement had
  split-vote and requestvote-ignored in every membership execution, and P6-11 predicted they would
  stay there "because five servers elect differently". Six executions printed showed the spares
  standing from about 300 on: `RaftNode` took itself plus its peers for its initial configuration,
  so a spare, given the three members, believed it belonged to a fourth. `ManualCluster` never
  ticked its spares, so no construction reached it. The reviewer's §13.30 reading of a rate at 100%
  (the generator cannot produce the other case) was right in spirit and pointed one layer deeper:
  the cluster could not. Every membership measurement from P6-07 to P6-10 ran with it.
- **A dimension named after an effect still measured the mechanism: §13.30 one level deeper.**
  "Node isolated for a timeout" counted a node as isolated only if every peer had tried to send to
  it, which happens mainly when the isolated node is the leader (its followers stand and ask it for
  votes); an isolated follower's peers have no reason to send. 10.8% of baseline executions counted,
  against 30.9% once the isolated node's own view decides, and 0.1% of membership executions, against
  31.3% (47.3% while the spares campaigned). Phase 2 restated the dimensions as effects instead of
  the faults injected (P2-02, the CRDT project's §13.30); this one already carried an effect's name
  and was still the mechanism, observed from the peers. *Rule (reviewer, at phase 6's acceptance):*
  restating a dimension as an effect is not sufficient; the effect has to be observed from the party
  it happens to. My prediction blamed the spares and was wrong twice over: excluding idle nodes moved
  it from 1 to 6 of 1,000. The reviewer had asked for a generator change; the generator was fine.
- **Both of the reviewer's diagnoses this phase were wrong, and measurement corrected both:** the
  isolation rate (a generator problem, read; the detector, found) and split-vote and
  requestvote-ignored at 100% (the generator, read; a spare campaigning from a configuration of the
  members plus itself, found). The numbers were real both times; the reading was not, and the
  correction came from printing the cases behind them.
- **The membership soak found the node wrong.** Its first local run stopped at seed 1462: a node
  recovered a torn term-vote record, issued the cut and then a record, and a crash kept the record
  and lost the cut, which the disk model allows (any subset of writes in flight survives) and which
  P3-08's fix (cut before appending) assumed away. The next restart refused the file. Both
  recoveries now skip a torn record that a valid one follows within a record's length; a corrupted
  whole record is still refused. Before the fix every torn length refused (availability lost, no
  data). Found at 1,462 executions of a workload whose spares, crashes and five nodes the baseline's
  10,000 never combine.
- **The running count** (reviewer, at P6-09's acceptance): the checker wrong about a correct node,
  four times (P3 twice, P4 once, P6's invariant 6 at seed 152), the node wrong twice. That ratio is
  the argument for why the checker gets its own vacuity guards. This phase also ran the other way:
  generated executions found the node wrong twice (P6-14, P6-15), and the measures of what happened
  wrong six times (P6-07 four times, the isolation detector, and S-cov-11 caught for the wrong reason).

## Phase 7

- **The model's leniency is the dangerous direction: P6-15 and the rename, a pair.** In P6-15 the
  simulated disk was stricter than a real file system (an append lands at the end of whatever file
  survives, where a real one fixes the offset at issue), and the node was fixed for the model: a
  stricter model can only produce extra failures, each a real one under its own contract. The
  rename is the inverse. `SimDisk` treated a rename whose source data was lost as a no-op, while a
  real file system can make the rename durable without the data and leave an empty file under the
  real name: a real crash could produce a state the simulator could not generate, so no execution
  could ever show the node mishandling it. Found by reading the disk model while designing
  compaction, before any node code issued a rename. *Rule:* where the model and reality differ,
  the model may be stricter and never laxer; the model now leaves the empty file (P7-00), and the
  barrier that keeps the node clear of it is recorded as a simplification, not a solution (spec §8,
  the phase-10 row).
- **The barrier held only one side of the rename.** P7-00 held a rename until the writes before it
  were durable, and released the writes queued behind it the moment the rename was issued. The
  compaction crash test went red on its first run with the barrier (P7-05, reordered loss, seed
  170): a crash kept a truncation computed for the compacted file and lost the rename, so the
  truncation cut the old file at the new file's offset and a durable entry vanished. The simulated
  disk applies writes by name; a real file system sends a write made after the rename to the new
  file, which a lost rename never put in place, so this state is the model's, not reality's. That
  is the strict direction: the failure was real under the node's contract (writes apply by name),
  and the world now holds every write after a rename until the rename is durable. The rename
  barrier test only checked the writes before the rename, so it could not see this; it now checks
  both sides (S-barrier-4). *Rule:* a barrier is about ordering, and ordering has two sides. A test
  of one side passes for a barrier that holds only that side.
- **Invariants over what was applied do not check what the state is.** Every log invariant compares
  ghost ids: which entry a node applied at which index. A restore that drops one key applies the
  right entries and holds the wrong state, and no invariant can see it: under S-agree-1 the
  invariants went red in none of 100 executions, linearizability in 7 (only where a client read the
  dropped key after the restore), and P7-08's replay comparison in 97. The positive control told the
  same story from the other side (P7-10): a node compacting past its commit index was caught in 18 of
  300 executions by the invariants and in 118 by the replay. Compaction is the first mechanism that
  moves state without applying entries, so it is the first the ghost-id invariants are blind to. The
  replay comparison runs in every soak execution from P7-11. *Rule:* when a mechanism produces a
  state by a path other than the one the checker watches, check the state itself, against a replay
  from the recorded inputs, not against another node.
- **"Issued" meant two things, and they came apart when the world began to hold writes.**
  `IssuedObservation` was documented as the node's decision and recorded when the world issued the
  write to the disk. They were the same moment until P7-00 made the world hold writes behind a
  rename. Then a follower that installed a snapshot applied entries whose appends were held, and the
  checkers, building its log from writes issued to the disk, reported it applying entries it did not
  hold. The same gap showed twice, first for the restore event and then for the appends. Both were
  found by the scale tests and the agreement sample, not by the hand-built traces. A write is now
  observed when the node emits it; the disk's order stays in the trace. *Rule:* when a component
  starts delaying what it used to pass straight through, every consumer of the "when" has to be
  re-read, because each of them assumed the two moments were one.
- **A construction that passes against the bug it targets tests nothing.** P7-07's stale-chunk
  construction passed against the implementation it was written to catch: the older snapshot's
  chunk at offset 8 was byte-identical to the newer one's (both records' length and term fields
  agree there). Running the construction against the naive implementation before fixing it is what
  showed this. The same check found a second flaw nobody predicted (a repeated first chunk restarted
  the transfer). *Rule:* run a new construction against the wrong implementation first. A red there
  is what makes its green worth anything, the same reasoning as a sabotage, applied before the fix.
- **A local clock at 98% of the ceiling is a red on the slower runner.** The full local run before
  phase 7's first push showed harness shard 10 at 879 s of its 900 s ceiling. I read that as green
  and pushed; on GitHub the shard took about 1,000 s and failed. Most of it was one entry, S-dur-2,
  at 713 s (122 s a few commits earlier): a sample test added that phase ran all 300 executions to
  count catches, and under that sabotage its executions slowed until it took 10 minutes, in the
  neighbours of every Scale entry. A test that measures a rate by running to the end costs its run
  time in every harness entry beside it, at the sabotaged system's speed, not the real one's. The
  local run is the check of record before a push, but its numbers are a local machine's. *Rule*
  (reviewer, at acceptance): a margin under 5% on a deterministic ceiling is a failure on a slower
  runner, so a local number means nothing until the runner's ratio is applied to it; and a test that
  only needs a floor stops at the floor. The harness shards have since moved to CI only.
- **The local run was not one indivisible cost.** At phase 7's acceptance the local run before a
  push was 90 to 105 minutes, and the reviewer was reluctant to weaken the rule that runs it.
  Moving the ten harness shards to CI took it to 17 minutes (1,036 s): the shards were not a share
  of the cost, they were nearly all of it, and the stages kept local are the ones that catch the
  mistakes that break a push. *Rule* (reviewer): before deciding whether a rule is too expensive to
  keep, measure which part of it is expensive; the rule may be cheap once that part moves.


## Phase 8

- **The undecided histories were made by how retries were recorded.** Until P8-00 a retry was a new
  operation with the same bytes, and a retry the client gave up on was indeterminate, open to the
  end of the history, concurrent with everything after it. With sessions a retry is one operation,
  answered by whichever attempt was answered. The baseline soak's checking time fell from 399 s to
  5 s, the hardest decided history from 21,232,723 states to 2,477, and KL-3, seed 8741's key k5, has
  been undecided with three histories since phase 6 and is now decided in at most 103 states over its
  keys. I had predicted it would stay undecided (P8-09), reasoning that a hardness surviving two
  workload changes was not timing; it was not timing, it was the recording. No check could have
  caught this earlier: the checker was right about every history it was given, and the histories
  were as hard as they looked. *Rule:* before measuring what makes a check expensive, ask whether the
  input's shape is the system's or the recorder's. Phase 11's structural-measure row now has no
  undecided key from either soak to measure against.
- **Before accepting an expensive measurement as a property of the system, ask which part of it is
  a representation choice** (reviewer, at phase 8's acceptance). KL-1, KL-2 and KL-3, the budget
  curves, the 32,000,000-state probe and the known-limit machinery of phases 5 and 6 were all
  measuring a choice this project made: a retry recorded as a separate operation, an abandoned one
  open to the end of the history. The machinery was correct and the measurements honest, and P5-05's
  structural finding (observed indeterminate operations drive cost) still holds; the magnitude was
  ours, and the 32,000,000-state wall is not a fact about WGL checkers. The question was never asked
  across two phases. Each phase took the previous phase's cost as given and built on it: a budget,
  then a curve, then a recorded limit, then a phase (11) to explain it. Nothing in that sequence
  looks at the recorder, because every step is downstream of it. *Rule:* when a measurement is
  expensive enough to build machinery around, list the choices between the system and the number
  (what is recorded, how it is keyed, what is left open) and change one before building.
- **A server path the workload could not reach.** The stale-sequence dimension was at 0 in both
  300-execution samples. A sequential client never sends a number below its latest, and network
  delay is short against the client's timeout, so the state machine's `stale|` path was exercised
  only by the unit tests. The floor found it, as floors are meant to; nothing had predicted it. The
  session clients now send a late duplicate every 16th sequence number when the two before it were
  answered (177 of 300 executions). The first version sent it whatever its original's fate, and a
  copy of an unanswered command closed that operation after the client had moved on (a malformed
  history at seed 1). *Rule:* a code path the generator cannot reach is tested only by its unit
  tests; a dimension at zero is a question about the generator before it is one about rarity.
- **A sabotage made vacuous by a change upstream of its patch.** S-lin-4 answers a write at append.
  Its patch keyed on the first byte of the command, and from P8-02 every write starts `Session|` and
  every registration `Register|`, so registrations were answered `ok` with no session in them, no
  client ever held a session, and no write was sent. Its target checked forty histories of reads
  alone and accepted them; the soak sample's content floors, its neighbours, went red. Found before
  the push by running the 94 sabotages whose patches touch the changed files; the harness shards run
  in CI only, so otherwise CI's per-commit jobs would have found it, on the branch. The patch now
  answers session writes, the target asserts that writes completed, and S-lin-7 is the patch as it
  had become. This is a new variant (reviewer): the `reason:` check verifies that the target fails
  with the right message, and nothing verifies that the target still exercises anything, so a patch
  that degrades its own target passes every guard the harness has. The manual step that found it is
  to be made mechanical (P9-00): when a push changes a file any patch touches, those entries run
  before it. *Rule:* a target must assert that what its sabotage removes is present, or a protocol
  change that removes it for everyone turns the sabotage into a test of nothing.
- **A positive control too rare for a floor in the sample.** A leader answering reads from its own
  state is caught in 7 of 3,000 executions (one in the first 300): it needs a deposed leader still
  reached by a client, or a new leader read before its no-op. A sample running to its third catch
  stops at seed 1,418, and under a sabotage that slows executions it would run to its cap, which is
  how phase 7's shard 10 went over its ceiling. The floor was kept and the sample replaced by the
  three executions the measurement found it caught in, each also green with the real node, as the
  budget tests name their seeds. *Rule:* when a check's natural rate is below its floor, name the
  executions that show it, with the rate measured once beside them, rather than lowering the floor
  or running the sample to the end.
- **ReadIndex needs a wire field.** An acknowledgement confirms a read only if it answers a request
  sent after the read arrived. Without a round number echoed in the response, the answer to a
  heartbeat sent just before the read counts: S-read-4 is that variant, and the slow-follower
  construction catches it, though no construction in the breakdown named it. *Rule:* "a heartbeat
  round" in a description is a set of requests, and a set needs a name on the wire.
