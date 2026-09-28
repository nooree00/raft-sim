#!/usr/bin/env bash
# Run each test project with its own, explicitly named TRX, then hold the counts to
# ci/test-baseline.txt. Per-project names because xUnit's default TRX name is
# user_host_timestamp, and two assemblies finishing together overwrote one another (seen in CI).
# The floor runs even when tests fail, so a run that is red for two reasons reports both.
set -euo pipefail
cd "$(dirname "$0")/.."
git config --global --add safe.directory "$PWD" # the checkout may belong to another user (CI containers)
rm -rf TestResults
rc=0
for project in tests/*/*.csproj; do
  name=$(basename "$project" .csproj)
  dotnet test --project "$project" --no-build --report-xunit-trx --report-xunit-trx-filename "$name.trx" --results-directory TestResults || rc=$?
done
dotnet tools/Raft.Gates/bin/Debug/net10.0/Raft.Gates.dll testcount --results TestResults
exit "$rc"
