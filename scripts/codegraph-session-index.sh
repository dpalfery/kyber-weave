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
# One build per checkout at a time. The lock directory holds the pid of the build that owns it; a lock
# whose build is no longer alive (killed, reboot) is reaped at once, and one with no pid file is
# reaped after 15 minutes.
lock="${TMPDIR:-/tmp}/kw-codegraph-index-$(printf '%s' "$root" | cksum | cut -d' ' -f1).lock"
if ! mkdir "$lock" 2>/dev/null; then
  owner="$(cat "$lock/pid" 2>/dev/null)"
  if [ -n "$owner" ]; then
    kill -0 "$owner" 2>/dev/null && exit 0
  elif [ -z "$(find "$lock" -maxdepth 0 -mmin +15 2>/dev/null)" ]; then
    exit 0
  fi
  rm -rf "$lock" 2>/dev/null
  mkdir "$lock" 2>/dev/null || exit 0
fi
log="${HOME:-/tmp}/.cache/kyber-weave/codegraph-index.log"
mkdir -p "$(dirname "$log")" 2>/dev/null
# Keep the log small: start over once it passes 100 KB.
if [ -f "$log" ] && [ "$(wc -c <"$log" 2>/dev/null || echo 0)" -gt 102400 ]; then
  : >"$log"
fi
(
  codegraph init "$root" >>"$log" 2>&1
  rm -rf "$lock" 2>/dev/null
) &
printf '%s\n' "$!" >"$lock/pid" 2>/dev/null
exit 0
