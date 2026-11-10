#!/usr/bin/env bash
# codegraph-session-index.sh - build the CodeGraph index for this checkout when it has none.
#
# Called by each coding harness's session-start hook. A fresh git worktree has no .codegraph/
# directory, and without it `docs drift` fails (KW-DOC-DRIFT-001) and code search loses precision.
# The repository root comes from this script's own location, never from a harness variable, so a
# harness that leaves its project-directory variable unset cannot point the build at another place.
#
# Does nothing when the index exists or codegraph is not installed. The build runs in the
# background so the session starts at once, and its output goes to ~/.cache/kyber-weave/codegraph-index.log.
# Always exits 0 and prints nothing: a session-start hook's output can be added to the model's context.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." 2>/dev/null && pwd)" || exit 0
[ -n "$root" ] && [ "$root" != "/" ] || exit 0
# The database file, not the directory, marks a finished index: an interrupted first build leaves an
# empty .codegraph/ behind, and that must not stop the next session from building it.
[ -f "$root/.codegraph/codegraph.db" ] && exit 0
command -v codegraph >/dev/null 2>&1 || exit 0
# One build per checkout at a time. The lock is a directory named from the root path plus its
# checksum, holding the pid of the build that owns it and a token that only that build knows.
# A lock is taken over when its pid is not a live process, when it is 15 minutes old whatever the
# pid says (a recycled pid must not block forever), or when it has no usable pid and is 2 minutes
# old. If the age cannot be read (the lock just vanished) it counts as stale.
key="${root//[!A-Za-z0-9]/_}"
[ "${#key}" -gt 160 ] && key="${key:$((${#key} - 160))}"
sum="$(printf '%s' "$root" | cksum 2>/dev/null | cut -d' ' -f1)"
lock="${TMPDIR:-/tmp}/kw-codegraph-index-${key}-${sum}.lock"
if ! mkdir "$lock" 2>/dev/null; then
  owner="$(cat "$lock/pid" 2>/dev/null)"
  case "$owner" in
    '' | 0* | *[!0-9]*) [ -n "$(find "$lock" -maxdepth 0 -mmin -2 2>/dev/null)" ] && exit 0 ;;
    *) if [ -n "$(find "$lock" -maxdepth 0 -mmin -15 2>/dev/null)" ]; then
         kill -0 "$owner" 2>/dev/null && exit 0
       fi ;;
  esac
  rm -rf "$lock" 2>/dev/null
  mkdir "$lock" 2>/dev/null || exit 0
fi
# Claim the lock with this script's pid and a token; the build replaces the pid with its own below.
token="$$.$RANDOM$RANDOM"
if ! { printf '%s\n' "$$" >"$lock/pid" && printf '%s\n' "$token" >"$lock/token"; } 2>/dev/null; then
  # The lock cannot record its owner (read-only or full disk). Two builds must never run at once, so none runs.
  rm -rf "$lock" 2>/dev/null
  exit 0
fi
# The build's output replaces the previous run's, so the log holds one run. With no HOME there is no
# log, and never a /tmp path. The output is not piped through anything that could close early: a
# SIGPIPE would kill the build mid-index.
log=/dev/null
if [ -n "${HOME:-}" ] && mkdir -p "$HOME/.cache/kyber-weave" 2>/dev/null; then
  log="$HOME/.cache/kyber-weave/codegraph-index.log"
fi
(
  codegraph init "$root" >"$log" 2>&1
  # Remove the lock only if it is still this build's: a build that outlived its lock must not delete a successor's.
  if [ "$(cat "$lock/token" 2>/dev/null)" = "$token" ]; then
    rm -rf "$lock" 2>/dev/null
  fi
) &
printf '%s\n' "$!" >"$lock/pid" 2>/dev/null
exit 0
