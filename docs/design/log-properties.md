# The log's properties (P7-02)

Spec §8: "anything that removes data participates in every invariant the data participated in".
Before compaction removed anything, every property the log satisfies was enumerated from the code
that reads it, not from §8's list: `RaftNode`, `LogStore`, `EntryLog.Recover`/`Resume`, the
checkers (`LogHistory`, `LogHistory.FileView`, `ElectionHistory`) and the codec. §8 names four
(1-4); the rest are the ones this code depends on that §8 does not name. Each has a test in
`tests/Raft.Core.Tests/LogPropertyTests.cs` that takes logs as input, so that compaction's change to
it is visible when compacted logs are added (P7-06). The last column is filled as compaction lands.

| # | Property | Who relies on it | What compaction does to it | Test |
|---|---|---|---|---|
| 1 | **Referential** (§8): every index from 1 to the last resolves to an entry and a term, and index 0 to term 0 | `RaftNode`: `TermAt` in the consistency check, the election restriction and commitment; `At` in apply and the configuration lookup; `From` in AppendEntries | indices at or below the snapshot's stop resolving; `TermAt(lastIncludedIndex)` must give `lastIncludedTerm` | `EveryIndexResolves` |
| 2 | **Sequential** (§8): indices are dense; the entry at position *i* has index *i* | `LogStore` (a list indexed by `index - 1`), `TruncateFrom`, the chain check | dense from the snapshot's index + 1, not from 1 | `IndicesAreDense` |
| 3 | **Reconstructive** (§8): replaying the log from the empty state gives the state machine's state | `RaftNode.Apply` from `lastApplied`; a restarted node replays from 1 | the snapshot's state, then the retained suffix | `ReplayingTheLogGivesTheState` |
| 4 | **Evidential** (§8): the log is what `matchIndex` and the consistency check are judged against: a log recovered from its file answers every `(prevLogIndex, prevLogTerm)` as the in-memory log does | `RaftNode.OnAppendEntries` (`TermAt(prevLogIndex)`), `OnAppendEntriesResponse`, `AdvanceCommit` | a `prevLogIndex` below the snapshot cannot be answered from the log: InstallSnapshot instead | `TheRecoveredLogAnswersAsTheLogDid` |
| 5 | **Chained** (P4-02): recovery takes a record only if its previous-entry term matches the entry before it; a record whose predecessor was lost is dropped | `EntryLog.Resume` | the first retained record's predecessor is in the snapshot; recovery must chain it onto `lastIncludedTerm`, or it drops every retained entry | `AnUnchainedRecordIsDropped` |
| 6 | **Overriding** (P4-02): a later record for index *i* discards every entry at *i* or above, so a lost truncation cannot bring a suffix back | `EntryLog.Resume`, `TruncateFrom` | a record at or below the snapshot's index must never override the snapshot (committed entries); no truncation goes below the commit index | `ALaterRecordOverridesTheSuffix` |
| 7 | **Located** (P4-02): each entry knows where its record ends in the file, and a truncation cuts the file there | `LogStore.TruncateFrom`, `CutTornTail` | offsets are positions in the current file; a rename to a new file changes all of them | `ATruncationCutsTheFileWhereTheEntryEnded` |
| 8 | **Configuration-bearing** (P6-03, P6-05): the configuration in effect at index *i* is the latest configuration entry at or below *i*, found in the log | `RaftNode.ConfigurationAt`, `Configuration`, recovery of the configuration on restart (phase 6 decision 2) | for *i* at or below the snapshot, the configuration is the snapshot's (register: the snapshot carries it) | `TheConfigurationAtAnIndexIsTheLatestAtOrBelowIt` |
| 9 | **Last-term-bearing**: `LastTerm` is the last entry's term, 0 when the log is empty | `RaftNode`: the election restriction (`RequestVote`'s `lastLogTerm`), `Stand` | with an empty retained log, `LastIndex`/`LastTerm` are the snapshot's, not 0 | `TheLastTermIsTheLastEntrysTerm` |
| 10 | **Observable by appends and truncates** (checkers): every change to `entries.log` reaches the checkers as an append or truncate of `entries.log`, and an incremental view of the file equals a full recovery of it | `LogHistory.FileView` (ignores any other operation), `ElectionHistory` (rebuilds logs from the writes a node issued) | a rename onto `entries.log` is observed under its source's name and ignored by the view; the checkers must take a replaced file whole | `AnIncrementalViewOfTheFileEqualsItsRecovery` |
