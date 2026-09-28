# Anomaly taxonomy for single-key KV histories

Each category of non-linearizable history the checker must reject (spec §6, breakdown P2-04), one
line each. Every category has at least one hand-written rejected history in
`tests/Raft.Checker.Tests/KnownHistories.cs`. Each rejected history has a near-miss twin: the same
history with one operation changed, which must be accepted. The tests
`KnownHistoryTests.EveryAnomalyCategoryHasARejectedHistory` and
`EveryRejectedHistoryHasAnAcceptedTwinOneOperationAway` enforce both.

| Category | Definition |
|---|---|
| stale-read | A read returns a value that was overwritten by a write that completed before the read began. |
| lost-write | A write that completed has no effect on any later read, which no ordering of the concurrent operations explains. |
| committed-then-vanished | A value observed by one read is absent from a later read, with no write between them that could remove it. |
| duplicate-append | A single Append's effect appears more than once. |
| cas-twice-from-one-value | Two CompareAndSwaps from the same expected value both succeed, with no write restoring that value between them. |
| real-time-order-only | Operations that do not overlap in time take effect in the opposite order; the same history with the intervals overlapping is linearizable. |
| value-never-written | A read returns a value no operation wrote. |
| indeterminate-observed-then-unobserved | An operation with no response is observed to have taken effect, then observed not to have. |
| readers-disagree-on-order | Two clients observe two concurrent writes in opposite orders. |
| delete-resurrected | A read after a completed Delete returns the deleted value, with no write re-creating it. |

## The audit (P2-04, before any WGL code)

Before this task the catalogue held eight rejected histories. Classified:

| Category | Before | After |
|---|---:|---:|
| stale-read | 1 | 1 |
| lost-write | 2 | 2 |
| committed-then-vanished | 1 | 1 |
| duplicate-append | **0** | 1 |
| cas-twice-from-one-value | 1 | 1 |
| real-time-order-only | **0** | 1 |
| value-never-written | 1 | 1 |
| indeterminate-observed-then-unobserved | 1 | 1 |
| readers-disagree-on-order | 1 | 1 |
| delete-resurrected | **0** (no history used Delete at all) | 1 |

Twins before this task: two of the accepted histories happened to be one operation away from a
rejected one (`read-concurrent-with-overwrite`, `appends-in-either-order`). After: all eleven
rejected histories have one.
