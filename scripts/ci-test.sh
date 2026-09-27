#!/usr/bin/env bash
# Run every test project in the solution.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet test --solution Raft.slnx --no-build
