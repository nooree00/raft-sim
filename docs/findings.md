# Findings log

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

