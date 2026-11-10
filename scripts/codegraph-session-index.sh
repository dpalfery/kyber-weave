#!/usr/bin/env bash
# codegraph-session-index.sh - build the CodeGraph index for this checkout when it has none.
#
# Called by each coding harness's session-start hook. A fresh git worktree has no .codegraph/
# directory, and without it `docs drift` fails (KW-DOC-DRIFT-001) and code search loses precision.
# The repository root comes from this script's own location, never from a harness variable, so a
# harness that leaves its project-directory variable unset cannot point the build at another place.
#
# Does nothing when the index exists or codegraph is not installed. The build runs in the
# background so the session starts at once. Always exits 0 and prints nothing: a session-start
# hook's output can be added to the model's context.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." 2>/dev/null && pwd)" || exit 0
[ -n "$root" ] && [ "$root" != "/" ] || exit 0
[ -d "$root/.codegraph" ] && exit 0
command -v codegraph >/dev/null 2>&1 || exit 0
(codegraph init "$root" >/dev/null 2>&1 &)
exit 0
