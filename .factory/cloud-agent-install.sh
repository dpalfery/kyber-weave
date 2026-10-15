#!/usr/bin/env bash
# Kyber-Weave Factory (Droid) Environment install: idempotent repo setup for the
# .NET 10 engine, the KyberDash TypeScript surfaces, and the harness CLI/MCP/Squad
# tooling. Mirrors .cursor/cloud-agent-install.sh for use as a Factory Environment
# "Repository setup script" — runs in the repository directory after Factory's
# global setup. REPO_ROOT is derived from this script's own location, so it
# holds even when the script is invoked from another directory.
#
# Factory Environment build semantics: "Build scripts run with Bash and stop on
# command failures." This script therefore runs with `set -Eeuo pipefail` and
# explicitly wraps best-effort network steps in `|| echo warn` rather than
# silently swallowing them, so a real failure is visible in the build log.

set -Eeuo pipefail

# Piping this script on stdin leaves BASH_SOURCE unset, which under set -u
# silently resolves REPO_ROOT to "/"; sourcing it would run the whole install
# inside the caller's shell (errexit included). BASH_SOURCE differs from $0
# only when sourced, so both mis-invocations fail loudly — the script works
# only executed from its file inside the repository.
if [ -z "${BASH_SOURCE[0]:-}" ] || [ "${BASH_SOURCE[0]}" != "$0" ]; then
  echo "error: run this script from its file (bash .factory/cloud-agent-install.sh), not piped or sourced" >&2
  exit 2
fi
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Anchor the rest of the script to the repository root so relative paths
# (KyberWeave.sln, the local tool manifest) resolve regardless of the
# caller's working directory.
cd "$REPO_ROOT"

# Droid Environments typically run scripts as root; tolerate non-root by routing
# privileged writes through sudo. /opt/dotnet and apt's system locations prefer
# root, so this lets the same script run in either case.
is_root() { [ "$(id -u)" -eq 0 ]; }
run_privileged() {
  if is_root; then "$@"; else sudo "$@"; fi
}

# A fresh Factory image ships a cold apt cache, so installing without an update
# first can fail on a stale package index. Every apt install chains the update
# rather than assuming the image warmed the cache.
apt_install() {
  run_privileged apt-get update -qq && run_privileged apt-get install -y "$@"
}

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export TERM=dumb NO_COLOR=1 COLUMNS=200 DEBIAN_FRONTEND=noninteractive
LOG="${TMPDIR:-/tmp}/kyber-weave-droid-setup.log"

log() { echo "[install] $*"; }

# --- Prerequisites (curl/git) ------------------------------------------------
# curl feeds the .NET and nvm installers below and git the nvm one; a cold
# image may ship neither, so they install before their first use rather than
# inside the nvm branch, where the .NET step would already have needed curl.
command -v curl >/dev/null 2>&1 || apt_install curl
command -v git  >/dev/null 2>&1 || apt_install git

# --- .NET 10 SDK (repo build/test) ------------------------------------------
# Factory-managed sources ship without a usable .NET runtime, so install from
# the scripts index that global.json's latestFeature roll-forward accepts.
# Droid Environments run the script as a non-root user with passwordless sudo;
# mkdir alone does not make /opt/dotnet writable for the user that then tries
# to cp into it, so chown -R takes ownership of the dir-tree first. We keep the
# install location at /opt/dotnet (cluster-identifiable) and surface the binary
# through ~/.local/bin so MCP-launched, $PATH-from-shell commands find dotnet
# without needing a /usr/local/bin write.
if ! /opt/dotnet/dotnet --version >/dev/null 2>&1; then
  log "installing .NET 10 SDK -> /opt/dotnet"
  run_privileged mkdir -p /opt/dotnet
  run_privileged chown -R "$(id -u):$(id -g)" /opt/dotnet
  curl -fsSL https://dot.net/v1/dotnet-install.sh \
    | bash -s -- --channel 10.0 --install-dir /opt/dotnet
else
  log ".NET SDK already present: $(/opt/dotnet/dotnet --version)"
fi
mkdir -p "$HOME/.local/bin"
ln -sf /opt/dotnet/dotnet "$HOME/.local/bin/dotnet"
export PATH="$HOME/.local/bin:$PATH"

# --- sqlite3 (docs drift reads the CodeGraph index via the CLI) -------------
# Deliberate trade in AGENTS.md: the read-only adapter avoids
# Microsoft.Data.Sqlite's unresolved advisory. Ubuntu's archive ships
# libsqlite3-0; the CLI lives in a separate package.
command -v sqlite3 >/dev/null 2>&1 || apt_install sqlite3

# --- Node 24 (dash/.nvmrc: node:sqlite parity) ------------------------------
# bash -lc passes NVM_DIR but nvm itself keys on the source-able script; source
# it before checking which/nvm so the rest of the step is unconditional.
export NVM_DIR="$HOME/.nvm"
# shellcheck disable=SC1091
if ! command -v nvm >/dev/null 2>&1; then
  if [ ! -s "$NVM_DIR/nvm.sh" ]; then
    log "installing nvm -> $NVM_DIR"
    # $NVM_DIR lives under $HOME, so a plain mkdir keeps it user-owned; a
    # privileged mkdir would recreate the root-owned-dir bug fixed for
    # /opt/dotnet above, blocking the unprivileged nvm installer.
    mkdir -p "$NVM_DIR"
    curl -fsSL https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.1/install.sh | bash
  fi
  # shellcheck disable=SC1091
  . "$NVM_DIR/nvm.sh"
fi
NODE_VERSION="$(cat "$REPO_ROOT/dash/.nvmrc" 2>/dev/null || echo 24.21.0)"
if ! nvm which "$NODE_VERSION" >/dev/null 2>&1; then
  log "installing Node $NODE_VERSION"
  nvm install "$NODE_VERSION"
fi
nvm alias default "$NODE_VERSION" >/dev/null
nvm use "$NODE_VERSION" >/dev/null

# Pin node/npm/npx/corepack links into ~/.local/bin. The MCP servers in
# .factory/mcp.json are launched with PATH-from-shell, and the MCP launcher's
# environment may not source nvm; ~/.local/bin sits ahead of /usr/bin on PATH
# in both direct and login shells, so the absolute-path links win for every
# command Droid runs.
NODE_BIN="$(dirname "$(nvm which "$NODE_VERSION")")"
mkdir -p "$HOME/.local/bin"
for b in node npm npx corepack; do
  # An absent binary is expected (a Node build may ship no corepack), so the
  # guard skips it; a failing ln stays fatal. Errexit does not fire on the
  # non-final command of an AND list, so the old `[ -e ] && ln` form was
  # already safe — this is the explicit form of the same behavior.
  if [ -e "$NODE_BIN/$b" ]; then
    ln -sf "$NODE_BIN/$b" "$HOME/.local/bin/$b"
  fi
done
export PATH="$HOME/.local/bin:$PATH"
hash -r 2>/dev/null || true
log "node: $(node --version)  npm: $(npm --version)  (via $(command -v node))"

# --- .NET restore + pinned tools (ReSharper CLT for InspectCode) ------------
dotnet tool restore
dotnet restore KyberWeave.sln

# --- KyberDash TypeScript deps (root + web dashboard) -----------------------
# Node 24 picks up node:sqlite natively; the dash parse-workers and report
# exit-codes need that parity, and the .tsbuildinfo cache is invalidated by
# the rm below when global.json tooling changes.
rm -f "$REPO_ROOT/dash/.tsbuildinfo" "$REPO_ROOT/dash/web/.tsbuildinfo" 2>/dev/null || true
npm --prefix "$REPO_ROOT/dash" ci --no-audit --no-fund
npm --prefix "$REPO_ROOT/dash/web" ci --no-audit --no-fund

# --- Kyber-Weave CLI + MCP (latest RC) -> ~/.local/bin ----------------------
CLI="$HOME/.local/bin/kyber-weave"
if ! "$CLI" --version >/dev/null 2>&1; then
  log "installing kyber-weave CLI + MCP (prerelease)"
  curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh \
    | sh -s -- --prerelease --no-kyberdash --install-dir "$HOME/.local/bin" \
        >>"$LOG" 2>&1 \
    || log "kyber-weave install FAILED - see $LOG"
else
  log "kyber-weave CLI already present: $("$CLI" --version 2>/dev/null)"
fi

# --- CodeGraph MCP (code exploration + docs drift index) --------------------
# Install under $HOME/.local so the binary path is known and stable; the Cursor
# install relied on npm's default /usr/local prefix, which a non-root Droid
# Environment cannot write. Re-running install must skip the symlink-from-self
# branch, so resolve the global with ~/.local/bin pulled out of the lookup.
CG_LINK="$HOME/.local/bin/codegraph"
if ! command -v codegraph >/dev/null 2>&1; then
  log "installing CodeGraph MCP into ~/.local"
  npm i -g --prefix "$HOME/.local" @colbymchenry/codegraph >>"$LOG" 2>&1 \
    || log "codegraph install FAILED - see $LOG"
else
  CG_REAL="$(PATH="$NODE_BIN:/usr/local/bin:/usr/bin:/bin" command -v codegraph 2>/dev/null || true)"
  if [ -n "$CG_REAL" ] && [ "$CG_REAL" != "$CG_LINK" ]; then
    ln -sf "$CG_REAL" "$CG_LINK"
  fi
fi
"$CG_LINK" telemetry off >/dev/null 2>&1 || true
if [ ! -f "$REPO_ROOT/.codegraph/codegraph.db" ]; then
  log "building CodeGraph index"
  "$CG_LINK" init "$REPO_ROOT" >>"$LOG" 2>&1 \
    || log "codegraph init FAILED - see $LOG"
fi

# --- Kyber-Squad agents + skills for Factory Droid -> ~/.factory ------------
# The global receipt lives under the .NET ApplicationData folder, which fails
# every --global command when ~/.config does not exist yet. install creates a
# fresh deployment; update refreshes an existing one and preserves any local
# edits. squad status is no probe for "no deployment": it exits non-zero even
# when installed.
mkdir -p "${XDG_CONFIG_HOME:-$HOME/.config}"
"$CLI" squad install "$HOME" --global --target factory </dev/null >>"$LOG" 2>&1 \
  || "$CLI" squad update "$HOME" --global --target factory --replace-managed </dev/null >>"$LOG" 2>&1 \
  || log "kyber-squad install FAILED - see $LOG"

log "install complete"
