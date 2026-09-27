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
