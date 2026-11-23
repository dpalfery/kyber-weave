#!/usr/bin/env bash
# Puts the kyber-weave working tree in front of Copilot: builds a local release from the
# checkout, serves it on loopback, then runs the real `kyber-weave update` and
# `kyber-weave squad update --global` against it. This is the only path that ships
# agent, skill, hook, MCP and CLI changes together; `dotnet run ... squad update` asks
# GitHub for a release that does not exist.
#
# Usage: deploy-kyber.sh            # prints the deployed version on the last line
set -euo pipefail
source "$(dirname "$0")/config.sh"

cd "$HC_KW_REPO"
INSTALLED=$(kyber-weave --version | awk '{print $2}')
BASE=${INSTALLED%%.hc.*}
VERSION="$BASE.hc.$(date +%m%d%H%M)"
TREE="$HC_HOME/release"
LOG="$HC_HOME/deploy-$VERSION.log"

hc_log "building local release $VERSION from $(git rev-parse --short HEAD)$( [ -n "$(git status --porcelain)" ] && echo ' (dirty tree)') -> $LOG"
scripts/release-local.sh --version "$VERSION" --out "$TREE" --no-kyberdash >"$LOG" 2>&1 \
    || { tail -n 30 "$LOG" >&2; hc_die "release build failed"; }

PORT_FILE=$(mktemp "$HC_HOME/server-port.XXXX")
python3 scripts/local-release-server.py --root "$TREE" --port 0 >"$PORT_FILE" 2>>"$LOG" &
SERVER=$!
trap 'kill $SERVER 2>/dev/null; rm -f "$PORT_FILE"' EXIT
for _ in $(seq 60); do PORT=$(head -n 1 "$PORT_FILE" 2>/dev/null || true); [[ "$PORT" =~ ^[0-9]+$ ]] && break; sleep 1; done
[[ "${PORT:-}" =~ ^[0-9]+$ ]] || hc_die "release server gave no port"
export KYBER_WEAVE_RELEASE_ORIGIN="http://127.0.0.1:$PORT"
for _ in $(seq 30); do curl -fsS -o /dev/null --max-time 2 "$KYBER_WEAVE_RELEASE_ORIGIN/healthz" && break; sleep 1; done

hc_log "updating installed binaries to $VERSION"
kyber-weave update "$VERSION" --no-kyberdash --no-menubar >>"$LOG" 2>&1 || { tail -n 30 "$LOG" >&2; hc_die "kyber-weave update failed"; }
hc_log "redeploying agents and skills (global)"
kyber-weave squad update --global --version "$VERSION" --yes >>"$LOG" 2>&1 || { tail -n 30 "$LOG" >&2; hc_die "squad update failed"; }

NOW=$(kyber-weave --version | awk '{print $2}')
[ "$NOW" = "$VERSION" ] || hc_die "installed version is $NOW, expected $VERSION"
grep -qi "kept\|local edit" "$LOG" && hc_log "warning: squad update kept locally edited files; see $LOG"
echo "$VERSION"
