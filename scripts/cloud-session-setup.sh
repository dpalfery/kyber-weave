#!/bin/bash
# Keeps a Claude Code cloud session on the newest Kyber-Weave release candidate, with the
# Kyber-Squad Claude target deployed per-user under ~/.claude.
#
# A cloud session starts from a fresh clone. This repository's committed Squad deployment is
# the Copilot one under .github/, so without this script a Claude session has no kyber-weave
# binaries, no kyber-weave MCP server, none of the Squad agents or skills, and no .NET SDK to
# run the gates in AGENTS.md.
#
# It runs from two places, both cloud-only:
#   - the SessionStart hook in .claude/settings.json, on every start and resume, so a session
#     picks up a release candidate published since the environment was cached;
#   - the cloud environment's setup script, so the binaries are already on PATH when Claude
#     Code launches and starts the kyber-weave MCP server declared in .mcp.json. The setup
#     script is cached for about seven days, which is why the hook re-checks.
#
# The Claude target is deployed with --global rather than into the clone: a project deployment
# would rewrite the tracked Copilot receipt under .kyber-weave/ and dirty every session's tree.
#
# Never fails the session. Details go to the log; stdout stays one line because a SessionStart
# hook's stdout is added to Claude's context.

set -u

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
    exit 0
fi

OWNER="dpalfery"
REPO="kyber-weave"
BIN_DIR="${KYBER_WEAVE_INSTALL_DIR:-$HOME/.local/bin}"
LOG_DIR="$HOME/.cache/kyber-weave"
LOG="$LOG_DIR/cloud-session-setup.log"
CLI="$BIN_DIR/kyber-weave"

mkdir -p "$LOG_DIR"
exec 3>&1 >>"$LOG" 2>&1
echo "=== $(date -u +%Y-%m-%dT%H:%M:%SZ) cloud-session-setup"

# Spectre.Console renders progress glyphs instead of text on a non-interactive terminal.
export TERM=dumb NO_COLOR=1 COLUMNS=200

# squad reads products/kyber-squad when run from this repository's root, and the release's own
# pack anywhere else. Deploy the release candidate's content, not whatever the clone holds.
INSTALLER="$(cd "$(dirname "$0")" && pwd)/install.sh"
cd "$HOME" || exit 0

report() {
    echo "$1; $DOTNET_NOTE"
    echo "$1; $DOTNET_NOTE" >&3
    exit 0
}

# The repository's gates need the .NET SDK, which the cloud image does not ship. Ubuntu's own
# archive carries dotnet-sdk-10.0 (a 10.0.1xx band that global.json's latestFeature roll-forward
# accepts); dotnet-install.sh is no option because the session proxy denies
# builds.dotnet.microsoft.com, where every Microsoft download link redirects.
if ! command -v dotnet >/dev/null 2>&1; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get install -y dotnet-sdk-10.0 \
        || { apt-get update && apt-get install -y dotnet-sdk-10.0; } \
        || echo "dotnet-sdk-10.0 install failed"
fi
DOTNET_NOTE="no .NET SDK"
if command -v dotnet >/dev/null 2>&1; then
    DOTNET_NOTE=".NET SDK $(dotnet --version 2>/dev/null)"
fi

# Highest pre-release by version, not the first one GitHub lists: list order is what let a
# mistyped tag shadow the real release (docs/todo/mistyped-release-tag.md).
latest_rc() {
    curl -fsSL --retry 3 "https://api.github.com/repos/$OWNER/$REPO/releases?per_page=50" \
        | python3 -c '
import json, sys
tags = [r["tag_name"].lstrip("v") for r in json.load(sys.stdin)
        if r.get("prerelease") and not r.get("draft")]
print("\n".join(tags))' \
        | sort -V \
        | tail -n 1
}

installed_version() {
    [ -x "$CLI" ] && "$CLI" --version </dev/null 2>/dev/null | awk '{print $2}'
}

LATEST="$(latest_rc)"
CURRENT="$(installed_version || true)"
echo "latest rc: ${LATEST:-<unresolved>}; installed: ${CURRENT:-<none>}"

if [ -z "$LATEST" ] && [ -z "$CURRENT" ]; then
    report "kyber-weave: could not resolve the latest release candidate and none is installed; see $LOG"
fi

if [ -n "$LATEST" ] && [ "$LATEST" != "$CURRENT" ]; then
    # The sibling installer when running from the clone (the hook); main's copy when piped in
    # by the setup script, where $0 is bash and no sibling exists.
    if [ -f "$INSTALLER" ]; then
        sh "$INSTALLER" --version "$LATEST" --no-kyberdash --install-dir "$BIN_DIR"
    else
        curl -fsSL --retry 3 "https://raw.githubusercontent.com/$OWNER/$REPO/main/scripts/install.sh" \
            | sh -s -- --version "$LATEST" --no-kyberdash --install-dir "$BIN_DIR"
    fi || echo "install of $LATEST failed; keeping ${CURRENT:-nothing}"
    CURRENT="$(installed_version || true)"
fi

if [ -z "$CURRENT" ]; then
    report "kyber-weave: install failed; see $LOG"
fi

# The global receipt lives under the .NET ApplicationData folder, which resolves to an empty
# path — and fails every --global command — when ~/.config does not exist yet.
mkdir -p "${XDG_CONFIG_HOME:-$HOME/.config}"

# install creates the global receipt and refuses once one exists; update then refreshes it to
# this binary's release. squad status is no probe for that: it exits non-zero when installed.
"$CLI" squad install --global --target claude </dev/null \
    || "$CLI" squad update --global --target claude --replace-managed </dev/null
SQUAD=$?

if [ "$SQUAD" -ne 0 ]; then
    report "kyber-weave $CURRENT installed; Squad Claude deployment failed (exit $SQUAD); see $LOG"
fi
report "kyber-weave $CURRENT installed; Squad Claude target deployed to ~/.claude"
