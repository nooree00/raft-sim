# Working agreement, and what enforces it

The contract is `RAFT_PROJECT_SPEC.md`. Spec §10 predicts that a rule naming something to be
careful about will not hold, and a rule tied to a mechanical act will. So each §12 rule below
names the script that enforces it, or says plainly that nothing does.

| Rule (spec §12) | Enforced by | Runs |
|---|---|---|
| A breakdown for every phase: vacuity risk, sabotage, verifiability, prediction, outcome (evidence or forcing) per task | `gates breakdown` | CI, build job |
| Predict before implementing | `gates trailers`: each task's first `Task:` commit must descend from the commit that introduced its current prediction | CI, build job |
| Every non-documentation commit names its task | `gates trailers` (`Task: Pn-nn` trailer) | CI, build job |
| Sabotage is a standing practice, from a committed tree | `gates sabotage` (container) and `scripts/host-sabotages.sh` (host), every push | CI, build and secrets jobs |
| Every commit green | `gates each-commit` over the pushed range; the head gets the full run | CI, each-commit job |
| Keep a register; unimplemented throws and is listed | `gates register` | CI, build job |
| A phase report certifies the commit that contains it | `gates reports` (`gates verify-run --sha`) | CI, build job |
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
- Run the full CI sequence locally before every push, documentation-only pushes included —
  sabotages patch documentation too (P0 findings log).
- Build and test through `scripts/in-sdk.sh`; the host SDK is not trusted.
