# References

The published work this project cites, by a link that does not depend on a copy here. A citation
names the work and its location in it (figure, theorem, page); the identifier below is how to get it.

| Work | Identifier | Copy in this repository | Cited by |
|---|---|---|---|
| Ongaro & Ousterhout, *In Search of an Understandable Consensus Algorithm (Extended Version)*, Stanford, May 2014 | <https://raft.github.io/> | `raft-extended.pdf`, the authors' own distribution from raft.github.io | spec §1 (normative: Figure 2, Figure 3), `docs/design/figure2-checklist.md`, README |
| Herlihy & Wing, "Linearizability: A Correctness Condition for Concurrent Objects", *ACM Transactions on Programming Languages and Systems* 12(3):463–492, July 1990 | DOI [10.1145/78969.78972](https://doi.org/10.1145/78969.78972) | none (removed, below) | spec §6 and phase 0's row in §11; P0-15: `tests/Raft.Checker.Tests/HerlihyWingTests.cs` (Fig. 1, pp. 468–470), `LocalityTests.cs` (Theorem 1, p. 470); `src/Raft.Checker/QueueModel.cs` (Fig. 3) |

Page numbers in the citations are the journal's, which the DOI resolves to.

## What P0-15 took from Herlihy & Wing, and where it lives now

The paper's Fig. 1 queue histories H1–H4 and their verdicts (H1 and H3 linearizable, H2 and H4
not) are transcribed as the paper's literal event lists in `HerlihyWingTests.Figure1`; Theorem 1
(locality) is the property `LocalityTests` checks. Those transcriptions are the corpus the checker
is held to, and they are unchanged. What left the repository is the copy they were read from: a
reviewer comparing a transcription line by line now reads the paper through the DOI.

## Removed: `herlihy-wing-1990.pdf`

Committed at `040f8f9` (2026-09-27) and removed from the tree in the commit that added this file.
It was the ACM's copy of the TOPLAS article, whose notice permits copying "not made or distributed
for direct commercial advantage" and says "To copy otherwise, or to republish, requires a fee and/or
specific permission." Serving it from a public repository is republishing it, which nothing here
had permission to do. **It remains in history.** Why it stays there, and what removing it would
cost, is an open row in `docs/register.md`.
