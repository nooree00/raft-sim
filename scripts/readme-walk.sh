#!/usr/bin/env bash
# Runs the README's ```sh cold-walk blocks, in order, the way a person on a fresh clone would:
# in a fresh copy of the working tree (tracked and untracked files, no build output), with a
# scrubbed environment (no CI variables), and — through scripts/in-sdk.sh — in a fresh
# container with an empty NuGet cache. It catches README rot; it is not the person's walk.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
dest="$(mktemp -d)/raft-sim"; mkdir -p "$dest"
(cd "$here" && git ls-files -z --cached --others --exclude-standard | tar -c --null -T - -f -) | tar -x -C "$dest"
steps="$(awk '/^```sh cold-walk$/{on=1; next} /^```$/{on=0} on' "$dest/README.md")"
[ -n "$steps" ] || { echo "readme-walk: no cold-walk steps in README.md"; exit 1; }
echo "readme-walk: $(printf '%s\n' "$steps" | grep -c .) step(s) in $dest"
cd "$dest"
# Only network plumbing survives the scrub (a person behind a proxy has it too); CI's variables do not.
# A fresh NuGet volume per walk: the cache starts empty, as on a new machine.
volume="raft-walk-$$-$RANDOM"; trap 'docker volume rm -f "$volume" >/dev/null 2>&1 || true' EXIT
env -i HOME="$HOME" PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin" RAFT_NUGET_VOLUME="$volume" \
  ${HTTPS_PROXY:+HTTPS_PROXY="$HTTPS_PROXY"} ${HTTP_PROXY:+HTTP_PROXY="$HTTP_PROXY"} ${NO_PROXY:+NO_PROXY="$NO_PROXY"} \
  ${SSL_CERT_FILE:+SSL_CERT_FILE="$SSL_CERT_FILE"} \
  bash -euo pipefail -c "$steps" || { rc=$?; echo "readme-walk: stopped (exit $rc)"; exit "$rc"; }
echo "readme-walk: completed"
