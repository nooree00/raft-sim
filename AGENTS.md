# Working agreement, and what enforces it

The contract is `RAFT_PROJECT_SPEC.md`. Spec §10 predicts that a rule naming something to be
careful about will not hold, and a rule tied to a mechanical act will. So each §12 rule below
names the script that enforces it, or says plainly that nothing does.

| Rule (spec §12) | Enforced by | Runs |
|---|---|---|
| A breakdown for every phase: vacuity risk, sabotage, verifiability, prediction, outcome (evidence or forcing) per task | `gates breakdown` | CI, build job |
| Predict before implementing | `gates trailers`: each task's first `Task:` commit must descend from the commit that introduced its current prediction | CI, build job |
| Every non-documentation commit names its task | `gates trailers` (`Task: Pn-nn` trailer) | CI, build job |
| Sabotage is a standing practice, from a committed tree | `gates sabotage --shard i/n` (container), one job per shard, the shard count derived from the manifest (`ci/sabotage-shard-size.txt`), 15-minute ceiling per shard; `gates build-collect` requires every shard; `scripts/host-sabotages.sh` (host), every push; the shards run in CI only, not before a push (phase 7 acceptance); `gates sabotage --touched` (container), the entries whose patch or control names a file the push changed, at its head, before the push, its plan printed first (P9-00) | CI, sabotage i/n, build and secrets jobs |
| Every commit green | CI: `gates each-commit-list`, one matrix job per non-head commit and harness shard running that commit's own scripts (`scripts/ci-commit.sh`: shard 1 runs its preflight, build, gates, tests and harness share; pre-gate commits reported as such), and `gates each-commit-collect` requiring one result per (commit, shard). The head gets the full run. Before a push, locally, only the head's full run (P4 acceptance, below); the per-commit matrix is verified in CI and must be green before merge | CI, each-commit-list / commit SHA / each-commit jobs |
| Invariants at scale: the harness's verdict covers the suite's sample only; 10,000 executions run in each soak, never waived (spec §12) | Two soaks, each its own workload and floors (P6-12): `scripts/ci-soak.sh` (the baseline) and `scripts/ci-soak-membership.sh` (membership changes), 10,000 executions each, each refusing a report covering fewer, in CI only, not in the local pre-push run (P5 acceptance); `gates build-collect` requires exactly one passing `soak` and one passing `soak-membership` job; `gates reports` requires a successful `soak` job in the run certifying any report from phase 3 on, and `soak-membership` too from phase 6 on; branch protection lists `soak` and `soak-membership` (the person's setting) | CI, soak, soak-membership and build jobs |
| Phase 9's done criterion: three processes in Compose, a real client, a killed leader, the checker green | `scripts/compose-run.sh` (the run and its guards, `tools/Raft.Check`); `gates build-collect` requires one passing `compose` job; S-compose-1 (a follower killed) in `scripts/host-sabotages.sh`; adding `compose` to branch protection is the person's | CI, compose and build jobs |
| Keep a register; unimplemented throws and is listed | `gates register` | CI, build job |
| A phase report certifies the commit that contains it | `gates reports` (`gates verify-run --sha`). **Known residual: it has no local check, structurally** — it needs the GitHub API to find the certifying run, so a head whose full local run passed can still fail it. It produced phase 3's one red run (the report commit was not the push head); `gates each-commit`'s report-at-head rule (P4-10) is the local part that can be checked | CI, build job only |
| No test project runs zero tests; counts change only visibly | `gates testcount` against `ci/test-baseline.txt` | CI, build job |
| Pins and environment checked before any result is believed | `gates preflight` | CI, first step |
| No secrets in history | `scripts/secret-scan.sh` (gitleaks, digest-pinned) | CI, secrets job |
| Walk it cold, once per phase | `scripts/readme-walk.sh` covers README rot only; **the person's walk is not scripted** — the phase report must record it | CI, readme-walk job; person |
| Ask before adding a dependency | **not scripted**; `Directory.Packages.props` changes are visible in review, and the layering test fixes each project's allowed packages | review |
| Tests first for `Raft.Core`, proven against hand-built traces | **not scripted** yet (no Core tests until phase 3); the breakdown's sabotage column is the check | review |
| Report honestly; never weaken a test to make it green | **not scripted** | review |
| Keep a findings log | **not scripted**; `docs/findings.md` | review |

## Conventions

- Conventional commit messages. A non-documentation commit carries `Task: Pn-nn`.
- A sabotage lives in `sabotage/<id>/` (`patch.diff`, `sabotage.txt`) and lands in the same commit
  as the check it proves.
- A changed test count means editing `ci/test-baseline.txt` in the same commit.
- A prediction's outcome is `(evidence)` or `(forcing)`. Forcing: writing it changed the work, so
  it could not come true. Evidence: it could have been wrong about something that was then not
  changed. Only evidence tests the prediction; report the two counts separately.
- Before every push, run the head's CI sequence locally, documentation-only pushes included
  (sabotages patch documentation too, P0 findings log): the report-at-head rule (`gates
  each-commit --check`), preflight, build, gates, tests, host sabotages, secret scan, README walk,
  and **the touched-file sabotages** (P9-00): `gates sabotage --touched <the previous push's head>`
  runs, at the head, every entry whose patch or control names a file the push changed. It prints its
  plan before anything runs (the count, the fraction of the manifest, the estimate from
  `ci/sabotage-touched.txt`), and a selection estimated past that file's threshold runs only with
  `--accept-estimate`, a decision the phase report records with the fraction. Why the push's range
  and not each commit: S-lin-4 broke at a commit that never touched its patch's file (phase 8).
  **The harness shards are not run locally** (phase 7 acceptance): the local run had reached 90 to
  105 minutes, most of it the ten shards, which run in parallel in CI and are required there. The
  fast stages stay local because they catch the push-breaking mistakes: format errors, stale
  patches, report ordering. **What this costs:** a sabotage caught for the wrong reason, or a shard
  over its ceiling, is found after a push, on the branch. That has happened twice (S-rare-1 in phase
  2, and phase 7's shard 10 at 879 s of 900 locally, over on GitHub), so it is a real cost. When a
  shard is run locally anyway, its time means nothing until GitHub's ratio to the local machine
  (about 1.1 to 1.5) is applied to it. `gates reports` is
  the one gate this cannot include (it needs the GitHub API; see its row): a passing local run says
  nothing about it. **The soak is not run locally** (P5 acceptance): it runs in CI, is required, and
  must be green before merge. It is deterministic and runs identically there, a red soak is a
  finding rather than something fixed before a push (KL-1), and it had become a quarter of the local
  run (1,364 s of about 92 minutes at P5-07) and grows with the checker, while the rest stayed
  nearly flat (about 70 minutes against 65 at P4). **What this costs:** a soak failure is
  discovered after a push, on the branch, not before it. If the local run passes 90 minutes again,
  that goes to the person again; it is not decided here. Per-commit
  verification runs in CI, is required, and must be green before merge; it is not run locally
  (P4 acceptance: 73 (commit, shard) jobs were about 17 hours in series, against minutes in CI's
  parallel matrix). A bad intermediate commit is therefore caught after the push, on the branch,
  not before it.
- Build and test through `scripts/in-sdk.sh`; the host SDK is not trusted.
