# Figure 2 checklist — the paper's words

Transcribed from `docs/references/raft-extended.pdf`: Figure 2 (p. 4), Figure 13 (p. 13), and the
§6 and §8 sentences that add node behaviour Figure 2 omits. Wording is the paper's, lightly
reflowed. Every item must have exactly one row in the traceability table of
`docs/design/node-interface.md`; `Raft.Architecture.Tests.TraceabilityTests` enforces it.

## Figure 2 — State

- **F2-01** Persistent state on all servers: (Updated on stable storage before responding to RPCs)
- **F2-02** currentTerm: latest term server has seen (initialized to 0 on first boot, increases monotonically)
- **F2-03** votedFor: candidateId that received vote in current term (or null if none)
- **F2-04** log[]: log entries; each entry contains command for state machine, and term when entry was received by leader (first index is 1)
- **F2-05** commitIndex: index of highest log entry known to be committed (initialized to 0, increases monotonically)
- **F2-06** lastApplied: index of highest log entry applied to state machine (initialized to 0, increases monotonically)
- **F2-07** nextIndex[]: for each server, index of the next log entry to send to that server (initialized to leader last log index + 1) — volatile state on leaders, reinitialized after election
- **F2-08** matchIndex[]: for each server, index of highest log entry known to be replicated on server (initialized to 0, increases monotonically)

## Figure 2 — AppendEntries RPC

- **F2-09** Invoked by leader to replicate log entries (§5.3); also used as heartbeat (§5.2). Arguments: term, leaderId, prevLogIndex, prevLogTerm, entries[], leaderCommit
- **F2-10** Results: term (currentTerm, for leader to update itself), success (true if follower contained entry matching prevLogIndex and prevLogTerm)
- **F2-11** Receiver 1. Reply false if term < currentTerm (§5.1)
- **F2-12** Receiver 2. Reply false if log doesn't contain an entry at prevLogIndex whose term matches prevLogTerm (§5.3)
- **F2-13** Receiver 3. If an existing entry conflicts with a new one (same index but different terms), delete the existing entry and all that follow it (§5.3)
- **F2-14** Receiver 4. Append any new entries not already in the log
- **F2-15** Receiver 5. If leaderCommit > commitIndex, set commitIndex = min(leaderCommit, index of last new entry)

## Figure 2 — RequestVote RPC

- **F2-16** Invoked by candidates to gather votes (§5.2). Arguments: term, candidateId, lastLogIndex, lastLogTerm
- **F2-17** Results: term (currentTerm, for candidate to update itself), voteGranted (true means candidate received vote)
- **F2-18** Receiver 1. Reply false if term < currentTerm (§5.1)
- **F2-19** Receiver 2. If votedFor is null or candidateId, and candidate's log is at least as up-to-date as receiver's log, grant vote (§5.2, §5.4)

## Figure 2 — Rules for Servers

- **F2-20** All Servers: If commitIndex > lastApplied: increment lastApplied, apply log[lastApplied] to state machine (§5.3)
- **F2-21** All Servers: If RPC request or response contains term T > currentTerm: set currentTerm = T, convert to follower (§5.1)
- **F2-22** Followers: Respond to RPCs from candidates and leaders
- **F2-23** Followers: If election timeout elapses without receiving AppendEntries RPC from current leader or granting vote to candidate: convert to candidate
- **F2-24** Candidates: On conversion to candidate, start election: Increment currentTerm; Vote for self; Reset election timer; Send RequestVote RPCs to all other servers
- **F2-25** Candidates: If votes received from majority of servers: become leader
- **F2-26** Candidates: If AppendEntries RPC received from new leader: convert to follower
- **F2-27** Candidates: If election timeout elapses: start new election
- **F2-28** Leaders: Upon election: send initial empty AppendEntries RPCs (heartbeat) to each server; repeat during idle periods to prevent election timeouts (§5.2)
- **F2-29** Leaders: If command received from client: append entry to local log, respond after entry applied to state machine (§5.3)
- **F2-30** Leaders: If last log index ≥ nextIndex for a follower: send AppendEntries RPC with log entries starting at nextIndex
- **F2-31** Leaders: If successful: update nextIndex and matchIndex for follower (§5.3)
- **F2-32** Leaders: If AppendEntries fails because of log inconsistency: decrement nextIndex and retry (§5.3)
- **F2-33** Leaders: If there exists an N such that N > commitIndex, a majority of matchIndex[i] ≥ N, and log[N].term == currentTerm: set commitIndex = N (§5.3, §5.4)

## Figure 13 — InstallSnapshot RPC

- **F13-01** Invoked by leader to send chunks of a snapshot to a follower. Leaders always send chunks in order. Arguments: term, leaderId, lastIncludedIndex, lastIncludedTerm, offset, data[], done
- **F13-02** Results: term (currentTerm, for leader to update itself)
- **F13-03** Receiver 1. Reply immediately if term < currentTerm
- **F13-04** Receiver 2. Create new snapshot file if first chunk (offset is 0)
- **F13-05** Receiver 3. Write data into snapshot file at given offset
- **F13-06** Receiver 4. Reply and wait for more data chunks if done is false
- **F13-07** Receiver 5. Save snapshot file, discard any existing or partial snapshot with a smaller index
- **F13-08** Receiver 6. If existing log entry has same index and term as snapshot's last included entry, retain log entries following it and reply
- **F13-09** Receiver 7. Discard the entire log
- **F13-10** Receiver 8. Reset state machine using snapshot contents (and load snapshot's cluster configuration)

## §6 — Cluster membership changes

- **S6-01** Once a given server adds the new configuration entry to its log, it uses that configuration for all future decisions (a server always uses the latest configuration in its log, regardless of whether the entry is committed).
- **S6-02** Agreement (for elections and entry commitment) requires separate majorities from both the old and new configurations.
- **S6-03** The new servers join the cluster as non-voting members (the leader replicates log entries to them, but they are not considered for majorities).
- **S6-04** The leader steps down (returns to follower state) once it has committed the Cnew log entry.
- **S6-05** If a server receives a RequestVote RPC within the minimum election timeout of hearing from a current leader, it does not update its term or grant its vote.

## §8 — Client interaction

- **S8-01** Clients assign unique serial numbers to every command.
- **S8-02** Each leader commits a blank no-op entry into the log at the start of its term.
- **S8-03** A leader must check whether it has been deposed before processing a read-only request.
- **S8-04** The leader exchanges heartbeat messages with a majority of the cluster before responding to read-only requests.
