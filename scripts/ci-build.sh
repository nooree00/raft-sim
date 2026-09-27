#!/usr/bin/env bash
# Locked restore (CI=true turns on RestoreLockedMode), then a build in which any warning fails.
# -warnaserror is on the command line, not only in Directory.Build.props, because a property
# overridden inside a target is invisible to the preflight's evaluation (sabotage S-pre-6).
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet restore Raft.slnx -nologo
dotnet build Raft.slnx --no-restore -warnaserror -nologo
