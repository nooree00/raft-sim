# Register of deferred items

Parsed by `gates register` (spec §12). Every deferred item has the phase it is promised to. An
`open` row promised to a completed phase fails the build; a `done` row names the test that proves
it; a `dropped` row cites the commit that changed the spec to drop it. Every method that throws
`NotImplementedException` must be named, as `Namespace.Type.Method`, in an open row.

| Item | Promised | Status | Evidence |
|---|---|---|---|
| Herlihy & Wing published example histories with their published verdicts in Raft.Checker.Tests — blocked: the paper is unreachable from this environment; needs the PDF committed under docs/references/ | P0 | open | — |
| The person's cold walk of the README (spec §12), recorded in the P0 report | P0 | open | — |
| Disk acknowledgements as inputs to Core, instead of the ordered effect list with a persist barrier (spec §4) — decide, with measurements | P10 | open | — |
| Visualiser for a failing execution: timeline of nodes, terms, messages (spec §9, optional) | P10 | open | — |
