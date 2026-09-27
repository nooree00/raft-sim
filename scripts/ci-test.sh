#!/usr/bin/env bash
# Run every test project with a TRX per assembly, then hold the counts to ci/test-baseline.txt.
# The floor runs even when tests fail, so a run that is red for two reasons reports both.
set -euo pipefail
cd "$(dirname "$0")/.."
rm -rf TestResults
rc=0
dotnet test --solution Raft.slnx --no-build --report-xunit-trx --results-directory TestResults || rc=$?
dotnet tools/Raft.Gates/bin/Debug/net10.0/Raft.Gates.dll testcount --results TestResults
exit "$rc"
