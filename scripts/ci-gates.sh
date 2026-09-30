#!/usr/bin/env bash
# The spec §12 process gates over repository content, history, and the built assemblies (run
# after ci-build.sh). Each gate runs even if an earlier one failed, so one run reports all of them.
set -euo pipefail
cd "$(dirname "$0")/.."
git config --global --add safe.directory "$PWD" # the checkout may belong to another user (CI containers)
gates() { dotnet tools/Raft.Gates/bin/Debug/net10.0/Raft.Gates.dll "$@"; }
rc=0
gates breakdown || rc=1
gates trailers || rc=1
gates register || rc=1
gates patches || rc=1
exit "$rc"
