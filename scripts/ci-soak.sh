#!/usr/bin/env bash
# The soak (P3-08, spec §12): invariants 1, 8, 9 and 11 over 10,000 generated executions, and the
# distribution of what happened. The suite runs the same test over a 300-execution sample; this is
# the only place the invariants run at scale. Required by the `build` collect job; never waived.
set -euo pipefail
cd "$(dirname "$0")/.."
git config --global --add safe.directory "$PWD" # the checkout may belong to another user (CI containers)
executions=10000
report=tests/Raft.Scale.Tests/bin/Debug/net10.0/soak-report.txt
rm -f "$report"
status=0
RAFT_SOAK_COUNT=$executions dotnet test --project tests/Raft.Scale.Tests --no-build --filter-method "*TheGeneratedSampleHoldsEveryInvariant" || status=$?
test -s "$report" || { echo "soak: no report written; the soak did not run"; exit 1; }
cat "$report"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then { echo '```'; cat "$report"; echo '```'; } >> "$GITHUB_STEP_SUMMARY"; fi
head -1 "$report" | grep -q "^$executions executions " || { echo "soak: the report does not cover $executions executions: the soak ran less than it claims"; exit 1; }
exit "$status"
