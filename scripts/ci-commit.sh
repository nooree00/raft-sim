#!/usr/bin/env bash
# One non-head commit of a push, with that commit's own scripts (spec §12, P1-12).
#   ci-commit.sh classify SHA   gated | pre-gate (also to $GITHUB_OUTPUT as kind=...)
#   ci-commit.sh run SHA [i/n]  in a worktree of SHA: its preflight, build, gates, tests and harness;
#                               with a shard (P2-01), shard 1 runs everything and its harness share,
#                               other shards build and run their share
#   ci-commit.sh checks [i/n]   the same, in the current directory (used by `gates each-commit`)
# A commit is gated when it has the build, test and gates scripts; one from before them is
# pre-gate and is reported as such by `gates each-commit-collect`, never as passed.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
if [ -n "${CI:-}" ]; then git config --global --add safe.directory "*"; fi

checks() {
  local shard="${1:-}"
  if [ ! -x scripts/ci-gates.sh ] || [ ! -x scripts/ci-build.sh ] || [ ! -x scripts/ci-test.sh ]; then
    echo "pre-gate: this commit predates the gate scripts"
    return 0
  fi
  local steps="ci-preflight ci-build ci-gates ci-test ci-sabotage"
  if [ -n "$shard" ] && [ -f ci/sabotage-shard-size.txt ]; then
    export SABOTAGE_SHARD="$shard"
    case "$shard" in 1/*) ;; *) steps="ci-build ci-sabotage" ;; esac
  fi
  for s in $steps; do
    if [ -x "scripts/$s.sh" ]; then
      echo "::group::$s"
      "scripts/$s.sh"
      echo "::endgroup::"
    else
      echo "$s: not at this commit"
    fi
  done
}

case "${1:-}" in
  classify)
    sha="$2"
    kind=pre-gate
    if git -C "$here" cat-file -e "$sha:scripts/ci-gates.sh" 2>/dev/null \
      && git -C "$here" cat-file -e "$sha:scripts/ci-build.sh" 2>/dev/null \
      && git -C "$here" cat-file -e "$sha:scripts/ci-test.sh" 2>/dev/null; then
      kind=gated
    fi
    echo "kind=$kind"
    if [ -n "${GITHUB_OUTPUT:-}" ]; then echo "kind=$kind" >> "$GITHUB_OUTPUT"; fi
    ;;
  run)
    sha="$2"
    wt="$(mktemp -d)/commit"
    git -C "$here" worktree add -q --detach "$wt" "$sha"
    cd "$wt"
    checks "${3:-}"
    ;;
  checks)
    checks "${2:-}"
    ;;
  *)
    echo "usage: ci-commit.sh classify SHA | run SHA [i/n] | checks [i/n]" >&2
    exit 2
    ;;
esac
