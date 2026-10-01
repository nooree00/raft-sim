# Phase 0 — task breakdown

Parsed by `tools/Raft.Gates` (`breakdown` command). Each task is a `### P0-nn`
heading followed by exactly these fields, none empty or placeholder:
`Task`, `Vacuity`, `Sabotage` (ids that resolve under `sabotage/`, optionally
followed by `; manual: …`), `Verifiable here` (`yes` / `partial` / `no`, then
` — ` and a reason), `Prediction` (a mechanism and an `Observable:`), and
`Outcome` (`pending`, or `right` / `wrong` / `partly`, then ` — ` and what
happened). The prediction-order check requires each task's `Prediction` line
to be committed before the first commit carrying that task's `Task:` trailer.

## Ordering

**Blocking set** (phase 0 does not end without these): P0-01, P0-02, P0-03,
P0-04, P0-10, P0-05 — in that order, since CI (P0-05) calls the preflight
(P0-10). I add **P0-06** to the blocking set: without the test-count floor a
green P0-05 can be a run of zero tests, which makes the blocking set's own
done-criterion vacuous. It is small.

**Then**, in this order: P0-07, P0-09, P0-15, P0-11, P0-08, P0-13, P0-12,
P0-14.

**If phase 0 runs long, I would move, in this order:** P0-12 (report
verification — for the phase-0 report I can verify the run through the GitHub
tools and record the id by hand; the script matters from the phase-1 report
on), P0-13 (every-commit — until it exists I push one commit at a time and
check each run), P0-14's CI job (the README walk in a clean container; the
README and `AGENTS.md` themselves stay), P0-08 (the register is nearly empty
in phase 0). I would **not** move P0-07, P0-09, P0-11 or P0-15: P0-07 is what
makes "predict before implementing" hold from the first task, P0-09 is the
backbone of every later phase, and P0-11 and P0-15 are in phase 0's
done-criterion. Moving any of the four would be a spec change, argued first.

## P0-09 duration: decided

The sabotage harness runs **the full manifest on every push**. It stays fast
enough by not rebuilding what a sabotage does not touch:

1. **Gate-type sabotages** (patches to repository content — breakdown,
   register, workflow, `global.json` — whose target is a gate command) run the
   gate binary already built from HEAD against the patched tree. No build.
2. **Code-type sabotages** run in **one** git worktree at HEAD, built once.
   Each sabotage is applied in place, followed by an incremental build of the
   target test project only (so only the patched project and its dependents
   recompile), a run of the target test only, and a revert. Sequential, in a
   fixed path, so MSBuild's incremental state stays valid. (Parallel worktrees
   at different paths would not share incremental state; copying a built tree
   to a new path invalidates it through absolute paths in `obj/`.)
3. **Two guards that deterministic builds make possible.** The patched
   project's output assembly hash must *differ* from the baseline — otherwise
   the patch was not compiled in and the result is a harness error, not
   "survived". After the last sabotage, a rebuild must reproduce the baseline
   hashes exactly — otherwise a revert failed and every later result is
   suspect.
4. **A ceiling.** Total harness time is recorded per sabotage and in total; the
   harness fails above 15 minutes. That forces a decision when the manifest
   outgrows the design, instead of the harness quietly being run less often.

## Tasks

### P0-01 — Toolchain pin

- **Task:** `global.json` set to exactly 10.0.401 with `rollForward: latestPatch`; `Directory.Build.props` sets net10.0, C# 14, nullable, warnings-as-errors, pinned `AnalysisLevel`, deterministic builds; central package management with lock files and locked-mode restore in CI; CI image `mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29`.
- **Vacuity:** `global.json` exists but CI never consults it because it uses an SDK it installed itself; or warnings-as-errors is set in props and switched off by one csproj or by a `.targets` file.
- **Sabotage:** S-tool-1, S-tool-2, S-tool-3
- **Verifiable here:** yes — the same image digest runs locally under Docker.
- **Prediction:** The SDK's analyzers together with the xUnit analyzers raise a warning-as-error in a test project on the very first build, most likely CA1707 on underscore test names. **Observable:** the first `dotnet build` fails in a test project, not in `src/`.
- **Outcome:** wrong (evidence) — the first build of a test project raised no analyzer error: I had not used underscore names. The Recommended analyzers did bite, but in tools/ (CA1861, CA1865, CA1859), never in a test project.

### P0-02 — Solution skeleton

- **Task:** The `src/` projects of spec §4 (Core, Simulation, Checker, Kv, Host), `tools/Raft.Gates`, and only the test projects with real content this phase: Architecture.Tests, Gates.Tests, Checker.Tests. One solution file.
- **Vacuity:** Projects exist on disk but not in the solution, so CI never builds them.
- **Sabotage:** S-skel-1
- **Verifiable here:** yes — local build and the layering test.
- **Prediction:** The templates enable `ImplicitUsings`, which brings `System.Threading.Tasks` and `System.Net.Http` into scope in Core, so Core gets ambient namespaces by default. **Observable:** the P0-04 scan flags a reference as soon as a real type uses one; I disable implicit usings in Core.
- **Outcome:** wrong (forcing) — the mechanism never arose: I wrote the csproj files by hand and Directory.Build.props disables implicit usings, so no template-added namespace reached Core. (The prediction itself named the fix: "I disable implicit usings in Core".)

### P0-03 — Layering test

- **Task:** Read the resolved project graph for every project (evaluated `ProjectReference` and `PackageReference` items, so props-injected references count), compare it with the allowed-edges table, reject any package reference in Core, and fail on any project that is not in the table or not in the solution.
- **Vacuity:** The test finds zero projects because the path is wrong in the container, loops over nothing and passes (it must assert that the discovered set equals the table); parsing csproj text would let a props-injected reference through.
- **Sabotage:** S-layer-1, S-layer-2, S-layer-3, S-layer-4, S-layer-5
- **Verifiable here:** yes — local, in the pinned image.
- **Prediction:** `project.assets.json` flattens transitive project references, so a direct-edge check over it treats Host → Core-through-Kv as direct. **Observable:** a false violation, or a wrongly allowed edge, on the first run — direct edges must come from MSBuild evaluation, with the assets file used only for packages.
- **Outcome:** partly (forcing) — the assets file's `targets` section does flatten transitive project references (checked on Host), but its `projectFileDependencyGroups` section lists direct ones only; I used MSBuild evaluation from the start, so the false violation could not occur. (The prediction steered me to MSBuild evaluation before the first run.)

### P0-04 — Ambient-dependency scan of Raft.Core

- **Task:** Read the compiled `Raft.Core.dll` metadata (type references, member references, method specs, assembly references) with `System.Reflection.Metadata` and check it against an allowlist that fails closed. Core gets one real placeholder type so the scan never runs over an empty assembly.
- **Vacuity:** An empty Core passes trivially; an allowlist like `System.*` admits `DateTime` and `Random`; scanning member references alone misses types used only as field types or in `typeof`; scanning a stale or reference build of Core.
- **Sabotage:** S-amb-1, S-amb-2, S-amb-3, S-amb-4, S-amb-5, S-amb-6, S-amb-7
- **Verifiable here:** yes — local, in the pinned image.
- **Prediction:** Attributes the compiler adds on its own (nullable attributes, `RefSafetyRules`, `Debuggable`, `TargetFramework`, assembly-info attributes) are not in my first allowlist. **Observable:** the first run on the placeholder Core fails, listing `System.Runtime.CompilerServices.*` and `System.Diagnostics.DebuggableAttribute`.
- **Outcome:** right (evidence) — the first run on the placeholder Core failed listing exactly DebuggableAttribute, the CompilerServices attributes, TargetFrameworkAttribute and the assembly-info attributes; they are now a separate allowlist group.

### P0-10 — Preflight

- **Task:** `gates preflight`, first in CI and runnable locally: `dotnet --version` equals `global.json`; `rollForward` is `latestPatch`; the workflow's image literal equals `ci/image.digest`; evaluated warnings-as-errors, nullable, language version and analysis level for every project; a lock file per project; no `continue-on-error` and no `|| true` in the workflows; full (non-shallow) history.
- **Vacuity:** Comparing a value with itself (for example `global.json` against a copy of `global.json`); the per-project loop finds no projects; the workflow checks are text patterns, a known weakness.
- **Sabotage:** S-pre-1, S-pre-2, S-pre-3, S-pre-4, S-pre-5, S-pre-6
- **Verifiable here:** yes — local, in the pinned image.
- **Prediction:** `dotnet msbuild -getProperty` reports a property's value at evaluation time, before any target runs, so a value overridden inside a *target* in `Directory.Build.targets` will not show. **Observable:** a target-based variant of S-pre-3 survives, and the value has to be read from the compiler invocation instead.
- **Outcome:** right (evidence) — a TreatWarningsAsErrors=false set inside a target is invisible to evaluation and the build passed with a live CS0219 warning; the effect is now guarded by the CI build's command-line -warnaserror (S-pre-6).

### P0-05 — CI workflow

- **Task:** `.github/workflows/ci.yml`, on push to any branch; the build job runs inside the digest-pinned container; every step calls `scripts/*.sh` under `set -euo pipefail`, so the YAML holds no logic.
- **Vacuity:** A workflow that triggers only on pull requests, so no run happens; `continue-on-error`; an exit code swallowed by `| tee`; a step that runs zero tests and succeeds.
- **Sabotage:** S-ci-1; shared: S-pre-4; manual: one deliberately red run on `claude/blissful-goodall-358smj-sabotage`, run id recorded in the phase report, branch deleted afterwards
- **Verifiable here:** partial — the scripts run locally in the image; the live run can only be read through the GitHub tools.
- **Prediction:** In a container job the checkout is owned by a different user, so git inside the gates fails with "detected dubious ownership"; and there is no Docker socket inside the job, so gitleaks must be a separate job on the runner. **Observable:** the first run's first git-using step fails on a git command.
- **Outcome:** partly (evidence) — dubious ownership: right, the first CI run failed on the preflight's git calls (actions/checkout marks the tree safe only under a temporary HOME). No Docker socket: wrong, the runner mounts /var/run/docker.sock into container jobs; it is the image that lacks the docker CLI.

### P0-06 — Test-count floor and duration record

- **Task:** `gates testcount`: parse TRX per test project; fail if an expected project has no TRX file or zero *executed* tests (skipped tests do not count), or if a project's count drops below `ci/test-baseline.txt` without the baseline changing in the same commit; write job durations to the step summary.
- **Vacuity:** No TRX files are produced, the parser loops over nothing and passes.
- **Sabotage:** S-count-1, S-count-2, S-count-3
- **Verifiable here:** yes — local, in the pinned image.
- **Prediction:** xunit.v3 runs on Microsoft.Testing.Platform, which ignores `--logger trx` (it wants `--report-trx`). **Observable:** zero TRX files on the first run, and the floor goes red.
- **Outcome:** partly (evidence) — zero TRX files, right; but `--logger trx` is not ignored, it is rejected (exit 5, zero tests run), so it could not have produced a false green. The floor later caught a different loss: two assemblies' TRX files colliding on a timestamp name.

### P0-07 — Breakdown gate and prediction-order gate

- **Task:** `gates breakdown`: every task in every `docs/phases/P*/breakdown.md` has all fields, non-placeholder; every sabotage id resolves under `sabotage/`; `gates trailers`: every commit reachable from HEAD that changes anything outside the documentation allowlist (`docs/**`, root `*.md`) carries a `Task:` trailer naming a task that exists, and the first commit carrying a task's trailer comes after the commit that introduced that task's `Prediction` line; at report time no `Outcome` is `pending`.
- **Vacuity:** The heading format drifts, zero tasks parse and the gate passes; a shallow clone shows one commit, so the order check passes trivially; the gate checks that text is present, not that it is good — prose quality stays the reviewer's.
- **Sabotage:** S-bd-1, S-bd-2, S-bd-3, S-bd-4, S-bd-5, S-bd-6
- **Verifiable here:** yes — local, with fixture repositories.
- **Prediction:** (Revised before implementation. The review's prediction — that the three pre-gate commits have no trailer and the gate lists them — is withdrawn: the rule exempts documentation-only commits, which those three are, so it could only be wrong by construction.) Classifying a commit as documentation-only needs its changed paths, and `git diff-tree --name-only <sha>` prints nothing for a root commit unless given `--root`, so a root commit is classified as touching no paths and exempted for the wrong reason. **Observable:** a fixture whose root commit adds a `src/` file passes the gate when it should fail.
- **Outcome:** partly (forcing) — the mechanism is real (S-bd-5: without --root the root-commit fixture passes), but the observable never occurred, because writing the prediction made me write --root.

### P0-08 — Register gate and unimplemented-code scan

- **Task:** `docs/register.md` (item, promised phase, status, evidence) and `docs/phases/status.md` (completed phases). `gates register`: an open row promised to a completed phase fails; a `done` row must name a test that exists in the built test assemblies; a `dropped` row must cite a spec commit; an unknown phase value fails; every method in the built `src/` assemblies that constructs a `NotImplementedException` must have a register row.
- **Vacuity:** An empty register; a status file that never marks a phase complete, so nothing ever comes due; a row such as `P3 ` skipped instead of failing.
- **Sabotage:** S-reg-1, S-reg-2, S-reg-3, S-reg-4
- **Verifiable here:** yes — local, in the pinned image.
- **Prediction:** A throw inside a lambda or local function compiles into a compiler-generated nested type; if I walk only the declared types' methods, it is missed. **Observable:** S-reg-4 survives on the first run.
- **Outcome:** wrong (forcing) — the lambda case (S-reg-4) was caught on the first run: the metadata method table lists the generated nested types' methods too, and the name maps back to Raft.Core.Sabotage.Later. (The prediction named the pitfall; I walked the whole method table because of it.)

### P0-09 — Sabotage harness

- **Task:** `gates sabotage`: refuses to run on an uncommitted tree; one worktree at HEAD, built once; for each `sabotage/<id>/`: confirm the target passes unpatched, apply the patch cleanly, require the build to succeed, require the named test to be recorded as Failed (or a command target to exit non-zero with its expected message), record neighbouring failures; the hash guards and 15-minute ceiling described above. Controls: S-meta-1 (no-op patch, expects `survived`) and S-meta-2 (compile-breaking patch, expects `build-error`).
- **Vacuity:** Treating any non-zero exit as caught (Microsoft.Testing.Platform exits non-zero when zero tests match a filter); counting a patch that breaks the build as "red"; running stale binaries.
- **Sabotage:** S-meta-1, S-meta-2, S-meta-3
- **Verifiable here:** yes — local, in the pinned image.
- **Prediction:** (Revised before implementation for the decided design — the review's prediction, a full restore and build per sabotage, no longer applies.) The in-place revert via `git checkout -- <files>` gives the reverted file a new modification time, so the *next* sabotage's incremental build recompiles the previously patched project as well; with deterministic builds that is harmless to correctness but costs a recompile per sabotage. **Observable:** per-sabotage build time is roughly two projects' compile time, not one, and the final baseline-hash check passes.
- **Outcome:** partly (evidence) — every code sabotage is followed by a rebuild after revert (a revert always changes the dll, whose PDB id hashes the source), and the baseline hashes reproduce; but the cost is dominated by the target project's test run (the architecture tests' MSBuild evaluations), ~10 s per sabotage. 33 sabotages in 251 s in CI.

### P0-11 — Secret scan

- **Task:** gitleaks from the digest-pinned Docker Hub image, over full history, as its own CI job on the runner host; a harness command sabotage that plants freshly generated tokens in a scratch clone.
- **Vacuity:** Scanning only the working tree or only the last commit; testing with the well-known AWS example key, which scanners allowlist; a config that allowlists whole paths.
- **Sabotage:** S-sec-1, S-sec-2, S-sec-3, S-sec-4, S-sec-5
- **Verifiable here:** yes — the Docker Hub image pulls here.
- **Prediction:** The first full-history scan flags the committed PDF: its compressed streams contain high-entropy strings that trip the generic API-key rule. **Observable:** a finding in `docs/references/raft-extended.pdf`, fixed with an allowlist entry for that single file, not a directory.
- **Outcome:** wrong (evidence) — no finding in the PDF, because gitleaks never scanned it: it skips binary files (208 KB scanned in all; the PDF alone is 554 KB). Recorded as a known limit in scripts/secret-scan.sh.

### P0-12 — CI-run verification for pushes and reports

- **Task:** `gates verify-run <sha>`: query workflow runs *by commit SHA*, require at least one run, every required job successful and none skipped, and extract job durations. `gates reports`: for every `docs/phases/*/report.md`, the commit that last changed it must have a verified green run.
- **Vacuity:** A filter that matches zero runs and loops over nothing; accepting a run for a different SHA.
- **Sabotage:** S-run-1, S-run-2, S-run-3
- **Verifiable here:** partial — the logic runs offline against recorded API responses; the live API only from CI, whose result I can read.
- **Prediction:** Under restricted default permissions the workflow's `GITHUB_TOKEN` cannot read Actions. **Observable:** a 403 on the first live call, fixed with an explicit `permissions: actions: read`.
- **Outcome:** wrong (evidence) — a probe on the sabotage branch (run 36335951168) called the Actions API with only `contents: read` declared and got answers, not a 403 (the repository is public, so run data is readable regardless). The call also proved the missing-job rule live: the old run on 6e6dc04 was rejected for lacking each-commit, secrets and readme-walk. `actions: read` is declared anyway.

### P0-13 — Every commit green

- **Task:** `gates each-commit`: build and run the fast tests for every commit in the pushed range; the full suite runs on the head.
- **Vacuity:** `github.event.before` is all zeros on a new branch, or unknown after a force-push, so the range is empty and nothing is checked — the command must assert that the range is non-empty whenever the push carried commits.
- **Sabotage:** S-each-1, S-each-2
- **Verifiable here:** yes — local, with a fixture repository.
- **Prediction:** After a rebase or force-push the `before` SHA is not in the fetched history. **Observable:** `git rev-list` fails with "bad revision"; the fallback is the merge-base with `main`.
- **Outcome:** partly (evidence) — `git rev-list` over an unknown before SHA does fail (exit 128), but git says "Invalid revision range", not "bad revision"; a test pins the real message.

### P0-14 — README, AGENTS.md and a README walk in a clean container

- **Task:** `AGENTS.md` maps each spec §12 rule to the script that enforces it. A CI job takes a fresh clone into the pinned image with an empty NuGet cache and runs the README code blocks tagged `cold-walk`, in order. The person's cold walk is recorded in the phase report.
- **Vacuity:** The commands run in CI's already-prepared environment, so a missing prerequisite never shows.
- **Sabotage:** S-readme-1, S-readme-2
- **Verifiable here:** partial — the scripted walk is verifiable here; the person's walk should be the reviewer's, not mine, since I wrote the README.
- **Prediction:** On a stock Ubuntu 24.04 host — this machine — the README's first `dotnet build` stops with "A compatible .NET SDK was not found": apt offers 10.0.104 (1xx band) and `latestPatch` does not cross feature bands. **Observable:** a walk without the container stops at step 1, so the README must lead with the container.
- **Outcome:** right (evidence) — with Ubuntu's own dotnet-sdk-10.0 installed on this host (now 10.0.112, 1xx band), a build without the container is refused: "Install the [10.0.401] .NET SDK or update global.json" (wording differs from the prediction).

### P0-15 — Known-bad histories and a brute-force oracle

- **Task:** In `Raft.Checker`: the history model (including indeterminate operations), the single-key KV model, and a brute-force oracle for histories of about 8 operations or fewer. In `Raft.Checker.Tests`: hand-written histories as data — a stale read, a lost write, a committed-then-vanished value, an indeterminate write that a later read observes (good) — and the published Herlihy & Wing examples with their published verdicts.
- **Vacuity:** The oracle and the histories share my misreading of real-time order; the Herlihy & Wing examples are the verdicts that do not come from me.
- **Sabotage:** S-hist-1, S-hist-2, S-hist-3, S-hist-4, S-hist-5
- **Verifiable here:** yes — local, in the pinned image.
- **Prediction:** My first "lost write" history is accidentally linearizable: the lost write's response overlaps the read that should expose it. **Observable:** the oracle returns linearizable for `lost-write` on the first run.
- **Outcome:** wrong (forcing) — my first lost-write history was rejected, as intended, on the first run. (Having predicted the overlap error, I wrote the history to avoid it.)
