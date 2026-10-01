#!/usr/bin/env bash
# Run each test project with its own, explicitly named TRX, then hold the counts to
# ci/test-baseline.txt. Per-project names because xUnit's default TRX name is
# user_host_timestamp, and two assemblies finishing together overwrote one another (seen in CI).
# The floor runs even when tests fail, so a run that is red for two reasons reports both.
# Usage: ci-test.sh [project...]. With no names (CI) every test project runs. Names run only those
# projects, and the floor covers only them: a harness entry runs what its mechanism needs (P5-07).
set -euo pipefail
cd "$(dirname "$0")/.."
git config --global --add safe.directory "$PWD" # the checkout may belong to another user (CI containers)
rm -rf TestResults
rc=0
scope=()
if [ $# -gt 0 ]; then
  scope=(--projects "$(IFS=,; echo "$*")")
  projects=("$@")
else
  projects=()
  for project in tests/*/*.csproj; do projects+=("$(basename "$project" .csproj)"); done
fi
for name in "${projects[@]}"; do
  dotnet test --project "tests/$name/$name.csproj" --no-build --report-xunit-trx --report-xunit-trx-filename "$name.trx" --results-directory TestResults || rc=$?
done
dotnet tools/Raft.Gates/bin/Debug/net10.0/Raft.Gates.dll testcount --results TestResults "${scope[@]}"
exit "$rc"
