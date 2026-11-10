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
# One build per checkout at a time. The lock directory is named from the whole root path (no hash to
# collide) and holds the pid of the build that owns it. A lock is reaped when its pid is not a live
# process, when it is older than 15 minutes whatever the pid says (a recycled pid must not block
# forever), or when it has no readable pid for two minutes.
key="${root//[!A-Za-z0-9]/_}"
[ "${#key}" -gt 200 ] && key="${key:$((${#key} - 200))}"
lock="${TMPDIR:-/tmp}/kw-codegraph-index-${key}.lock"
if ! mkdir "$lock" 2>/dev/null; then
  owner="$(cat "$lock/pid" 2>/dev/null)"
  case "$owner" in
    '' | *[!0-9]*) [ -z "$(find "$lock" -maxdepth 0 -mmin +2 2>/dev/null)" ] && exit 0 ;;
    *) if [ -z "$(find "$lock" -maxdepth 0 -mmin +15 2>/dev/null)" ]; then
         kill -0 "$owner" 2>/dev/null && exit 0
       fi ;;
  esac
  rm -rf "$lock" 2>/dev/null
  mkdir "$lock" 2>/dev/null || exit 0
fi
# Claim the lock with this script's own pid at once; it is replaced by the build's pid below.
printf '%s\n' "$$" >"$lock/pid" 2>/dev/null
# The log lives in the user's cache directory only; with no HOME there is no log, never a /tmp path.
log=/dev/null
if [ -n "${HOME:-}" ] && mkdir -p "$HOME/.cache/kyber-weave" 2>/dev/null; then
  log="$HOME/.cache/kyber-weave/codegraph-index.log"
  [ -L "$log" ] && log=/dev/null
fi
(
  codegraph init "$root" >>"$log" 2>&1
  # Keep the last 100 KB. Only the build holding the lock gets here, so nothing else is writing.
  if [ "$log" != /dev/null ] && [ "$(wc -c <"$log" 2>/dev/null || echo 0)" -gt 102400 ]; then
    tail -c 102400 "$log" >"$log.tmp" 2>/dev/null && mv "$log.tmp" "$log" 2>/dev/null
  fi
  rm -rf "$lock" 2>/dev/null
) &
printf '%s\n' "$!" >"$lock/pid" 2>/dev/null
exit 0
