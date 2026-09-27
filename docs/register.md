# Register of deferred items

Parsed by `gates register` (spec §12). Every deferred item has the phase it is promised to. An
`open` row promised to a completed phase fails the build; a `done` row names the test that proves
it; a `dropped` row cites the commit that changed the spec to drop it. Every method that throws
`NotImplementedException` must be named, as `Namespace.Type.Method`, in an open row.

| Item | Promised | Status | Evidence |
|---|---|---|---|
| Herlihy & Wing published example histories (Fig. 1, H1–H4) with their published verdicts, and the locality theorem as a property test | P0 | done | Raft.Checker.Tests.HerlihyWingTests.OracleAgreesWithThePublishedVerdict |
| The person's cold walk of the README (spec §12), recorded in the P0 report | P0 | open | — |
| Disk acknowledgements as inputs to Core, instead of the ordered effect list with a persist barrier (spec §4) — decide, with measurements | P10 | open | — |
| Visualiser for a failing execution: timeline of nodes, terms, messages (spec §9, optional) | P10 | open | — |
| Every-commit check covers build and tests only: `gates each-commit` does not run the gates or the sabotage harness on non-head commits, so a non-head commit that is gate- or harness-red goes undetected — the same shape as a CI that is not running | P1 | open | — |
| Message codec canonical form: two encodings of the same message must not both decode, or a corruption that yields a valid alternative encoding is invisible — decide and write down when Core takes the codec | P3 | open | — |
| Sabotage-harness cost: the ceiling was raised from 15 to 20 minutes in phase 1 as a stopgap (868 s of 900 s at 78 entries on GitHub). Decide the structural fix — shard the harness across jobs, cheaper entries (e.g. run a test entry's target class only), or a larger runner — before phase 2's entries land | P2 | open | — |
