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
