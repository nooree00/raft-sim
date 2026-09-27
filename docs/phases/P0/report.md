# Phase 0 — report

**Status: accepted by the reviewer; one register row promised to P0 is open** — the person's cold
walk of the README, which the reviewer is doing — so `docs/phases/status.md` still says
`P0: in progress`.

**Update after acceptance.** The Herlihy & Wing paper was supplied and committed
(`docs/references/herlihy-wing-1990.pdf`). Fig. 1's H1–H4 are transcribed as the paper's literal
event lists and the oracle agrees with every published verdict; Theorem 1 (locality) is a property
test over 400 seeded two-key histories. Sabotages S-hist-4 and S-hist-5 prove both. That register
row is done.

Branch protection on `main` (making these checks required) is the reviewer's, after this.

This report certifies the commit that contains it. `gates reports` verifies that commit's CI run
on the next push; the run ids below are for the commit before it and for the deliberate red run.

## What exists

| Task | Delivered | Sabotages (all give their expected result) |
|---|---|---|
| P0-01 | SDK 10.0.401 exact (`latestPatch`), image by digest, AnalysisLevel 10.0 Recommended, lock files, locked restore under CI | S-tool-1..3 |
| P0-02 | `src/` Core, Checker, Kv, Simulation, Host; `tools/Raft.Gates`; `Raft.slnx` | S-skel-1 |
| P0-03 | Layering test over the *evaluated* project graph; disk = table = solution | S-layer-1..5 |
| P0-04 | Allowlist scan of `Raft.Core.dll` metadata (TypeRefs, denied members, assembly refs, P/Invoke) | S-amb-1..7 |
| P0-10 | `gates preflight` | S-pre-1..6 |
| P0-05 | Workflow: jobs build, each-commit, secrets, readme-walk; YAML only sequences scripts | S-ci-1, S-pre-4, and the deliberate red run below |
| P0-06 | `gates testcount`: exact per-project counts against `ci/test-baseline.txt` | S-count-1..3 |
| P0-07 | `gates breakdown`, `gates trailers` (prediction committed before implementation) | S-bd-1..5 |
| P0-09 | `gates sabotage`: every entry, every push; hash guards; 15-minute ceiling | S-meta-1..3 (controls) |
| P0-15 | History model, KV spec, brute-force oracle, 15 hand-written histories — **H&W pending** | S-hist-1..3 |
| P0-11 | gitleaks (digest-pinned) over full history | S-sec-1..2 (host) |
| P0-08 | `gates register`; every `NotImplementedException` site must be listed | S-reg-1..4 |
| P0-13 | `gates each-commit` over the pushed range | S-each-1..2 |
| P0-12 | `gates verify-run`, `gates reports` | S-run-1..3 |
| P0-14 | README (container first), `AGENTS.md` (rule → script, and what nothing enforces), README walk | S-readme-1..2 (host) |

50 sabotage entries: 46 run by the harness in the build job, 4 (`runner: host`, they need docker)
by `scripts/host-sabotages.sh` in the secrets job. Test projects: Architecture 26, Checker 47,
Gates 39.

## CI

- **Deliberate red run** (spec decision 3): run [36337029617](https://github.com/nooree00/raft-sim/actions/runs/36337029617) on `claude/blissful-goodall-358smj-sabotage`,
  commit `160d1e8`, which applies S-ci-1's failing assertion (and bumps the baseline so it is the
  only reason). Result: the build job failed at its **test** step — Raft.Gates.Tests 40 run, 1 failed (`SabotageFails`), test-count floor ok, exit code 2 from `dotnet test` — so the workflow goes red on a failing test and for that reason alone. (readme-walk and the host sabotages also went red, because both run the tests.) **Deleting the branch was refused** by the environment's push proxy (HTTP 403, organisation policy); it must be deleted by hand.
- **Last green run before this report:** [36337147988](https://github.com/nooree00/raft-sim/actions/runs/36337147988) on the working branch, commit `0923866` (and [36336623930](https://github.com/nooree00/raft-sim/actions/runs/36336623930), same commit, on the sabotage branch).
- **Job durations** (run 36337147988):

  | Job | Total | Steps |
  |---|---:|---|
  | build | 456 s | preflight 9 s, build 7 s, gates 1 s, reports 1 s, test 14 s, **sabotage 410 s** (46 entries; ceiling 900 s) |
  | each-commit | 178 s | 162 s over the push's non-head commits |
  | secrets | 43 s | host sabotages 38 s, secret scan <1 s |
  | readme-walk | 38 s | walk 35 s |

  The harness is 90% of the build job and grows about 9 s per code sabotage; at the current rate the
  15-minute ceiling is reached near 95 entries.
- Shakedown runs on the sabotage branch before the working branch (runs 1, 5, 6, 8, 9) were red,
  each for a reason recorded in `docs/findings.md`.
- **One red commit did reach the working branch**: this report's first version, `7380c6d`
  (run [36337685451](https://github.com/nooree00/raft-sim/actions/runs/36337685451)). Committing
  the report broke sabotage S-reg-1, whose patch created this file; I had pushed a docs-only commit
  without running the harness. Fixed in the next commit; recorded in `docs/findings.md`.
  History on the sabotage branch was rewritten; the working branch's commits were only ever
  pushed after the same content ran green there.

## Predictions

15 made, before each task, each with a mechanism and an observable. They are counted in two
classes (reviewer's rule, now enforced by `gates breakdown`): a **forcing** prediction changed the
work, so it could not come true — a checklist item; an **evidence** prediction did not, so it
could have been wrong.

- **Evidence (10):** right 3 (P0-04, P0-10, P0-14), partly 4 (P0-05, P0-06, P0-09, P0-13),
  wrong 3 (P0-01, P0-11, P0-12).
- **Forcing (5):** partly 2 (P0-03, P0-07), wrong 3 (P0-02, P0-08, P0-15). Two predictions were revised before implementation, with the reason
recorded (P0-07: the original was wrong by construction given the rule's documentation
exemption; P0-09: the decided harness design no longer matched it). Per-task outcomes are in
`breakdown.md`.

What the forcing ones say: writing the prediction down changed what I wrote — a forcing
function, not evidence about my model of the system. The informative wrong ones are P0-11 (gitleaks never scanned the PDF at all —
a real limit, now recorded) and P0-12 (the token can read runs because the repository is public).

## Findings

`docs/findings.md` has nine, each with why nothing caught it at the time. The ones that matter
most for later phases:

- **`dotnet test` exits 0 when it finds no test projects.** Found by the README walk (each
  `in-sdk.sh` call was a fresh container, so the test step had no packages). Only the test-count
  floor caught it.
- **Hand verification tested a different configuration than CI** (no `CI=true`, so no locked
  restore). The harness now always runs under CI settings.
- **Sabotage patches rot** as the code under them changes; the harness reports it as
  `apply-failed` or `build-error`, never silently. Lock-file-carrying patches rot whenever the
  project graph changes. This is the harness's main maintenance cost.
- **Coverage is per task, not per assertion.** The layering test's package-reference check had no
  sabotage until I noticed (S-layer-5). Nothing mechanical finds such gaps.

## Deviations from the breakdown

- The harness rebuilds after **every** revert, not only once at the end: stale binaries corrupted
  the next baseline in hand runs.
- Secret-scan and README sabotages run on the runner host, not in the harness (no docker CLI in the
  SDK container); the harness lists them as deferred instead of skipping them silently.
- A `changes-assemblies: no` field exists for sabotages whose target reads files at run time.
- The phase-0 done-criterion in the spec says "a real assertion in each test project"; that was
  amended (D8) to "every test project that exists", enforced by the floor.

## Cold walk

- **Scripted** (`scripts/readme-walk.sh`, CI job readme-walk): completes — fresh copy, scrubbed
  environment, empty NuGet volume, 112 tests run.
- **On this host without the container**: stops at step 1, as predicted — Ubuntu's SDK (10.0.112)
  is refused by `global.json`.
- **The person's walk**: not done. Open register row.

## Environment

Verified here: everything in the build job (the pinned image under Docker), the host sabotages,
the secret scan, the README walk. Not verifiable here: GitHub's runners (read back through the
GitHub tools); the Herlihy & Wing paper (blocked).
