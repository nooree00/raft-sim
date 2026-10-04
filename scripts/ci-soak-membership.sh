#!/usr/bin/env bash
# The membership soak (P6-12, spec §12): 10,000 generated executions with membership changes, a soak of
# its own beside scripts/ci-soak.sh, with its own floors and its own known limits
# (ci/known-limits-membership.txt). The suite runs the same test over a 300-execution sample. Required
# by the `build` collect job; never waived.
set -euo pipefail
cd "$(dirname "$0")/.."
git config --global --add safe.directory "$PWD" # the checkout may belong to another user (CI containers)
executions=10000
report=tests/Raft.Membership.Tests/bin/Debug/net10.0/membership-report.txt
rm -f "$report"
status=0
RAFT_MEMBERSHIP_COUNT=$executions dotnet test --project tests/Raft.Membership.Tests --no-build --filter-method "*TheMembershipSampleHoldsEveryInvariant" || status=$?
test -s "$report" || { echo "soak-membership: no report written; the soak did not run"; exit 1; }
cat "$report"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then { echo '```'; cat "$report"; echo '```'; } >> "$GITHUB_STEP_SUMMARY"; fi
head -1 "$report" | grep -q "^$executions executions " || { echo "soak-membership: the report does not cover $executions executions: the soak ran less than it claims"; exit 1; }
exit "$status"
