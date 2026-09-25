#!/usr/bin/env bash
# Kyber-Weave Cloud Agent install: idempotent bootstrap for the .NET 10 engine,
# the KyberDash TypeScript surfaces, and the harness CLI/MCP/Squad tooling.
set -uo pipefail

REPO_ROOT="$(pwd)"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export TERM=dumb NO_COLOR=1 COLUMNS=200
LOG="${TMPDIR:-/tmp}/kyber-weave-setup.log"

log() { echo "[install] $*"; }

# --- .NET 10 SDK (repo build/test) ------------------------------------------
if ! /opt/dotnet/dotnet --version >/dev/null 2>&1; then
  log "installing .NET 10 SDK -> /opt/dotnet"
  sudo mkdir -p /opt/dotnet && sudo chown "$(id -u):$(id -g)" /opt/dotnet
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir /opt/dotnet
else
  log ".NET SDK already present: $(/opt/dotnet/dotnet --version)"
fi
sudo ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet
export PATH="/opt/dotnet:$PATH"

# --- Node 24 (dash/.nvmrc: node:sqlite parity) ------------------------------
export NVM_DIR="$HOME/.nvm"
# shellcheck disable=SC1091
[ -s "$NVM_DIR/nvm.sh" ] && . "$NVM_DIR/nvm.sh"
NODE_VERSION="$(cat "$REPO_ROOT/dash/.nvmrc" 2>/dev/null || echo 24.21.0)"
if ! nvm which "$NODE_VERSION" >/dev/null 2>&1; then
  log "installing Node $NODE_VERSION"
  nvm install "$NODE_VERSION"
fi
nvm alias default "$NODE_VERSION" >/dev/null
nvm use "$NODE_VERSION" >/dev/null

# Make Node 24 authoritative. The harness injects an /exec-daemon node shim (v22.x)
# ahead of the nvm bin on PATH; the full KyberDash suite (node:sqlite parity in
# parse-workers, report exit-codes) fails on that older runtime. ~/.local/bin sits
# ahead of /exec-daemon on PATH in both direct and login shells, so pinning the
# node/npm/npx/corepack links there wins for every command the agent runs.
NODE_BIN="$(dirname "$(nvm which "$NODE_VERSION")")"
mkdir -p "$HOME/.local/bin"
for b in node npm npx corepack; do
  [ -e "$NODE_BIN/$b" ] && ln -sf "$NODE_BIN/$b" "$HOME/.local/bin/$b"
done
export PATH="$HOME/.local/bin:$PATH"
hash -r 2>/dev/null || true
log "node: $(node --version)  npm: $(npm --version)  (via $(command -v node))"

# --- sqlite3 (docs drift reads the CodeGraph index via the CLI) -------------
command -v sqlite3 >/dev/null 2>&1 || { sudo apt-get update && sudo apt-get install -y sqlite3; }

# --- .NET restore + pinned tools (ReSharper CLT for InspectCode) ------------
dotnet tool restore
dotnet restore KyberWeave.sln

# --- KyberDash TypeScript deps (root + web dashboard) -----------------------
npm --prefix "$REPO_ROOT/dash" ci --no-audit --no-fund
npm --prefix "$REPO_ROOT/dash/web" ci --no-audit --no-fund

# --- Kyber-Weave CLI + MCP (latest RC) -> ~/.local/bin ----------------------
if ! "$HOME/.local/bin/kyber-weave" --version >/dev/null 2>&1; then
  log "installing kyber-weave CLI + MCP (prerelease)"
  curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh \
    | sh -s -- --prerelease --no-kyberdash >>"$LOG" 2>&1 \
    || log "kyber-weave install FAILED - see $LOG"
else
  log "kyber-weave CLI already present: $("$HOME/.local/bin/kyber-weave" --version 2>/dev/null)"
fi

# --- CodeGraph MCP (code exploration + docs drift index) --------------------
command -v codegraph >/dev/null 2>&1 || npm i -g @colbymchenry/codegraph
# Pin codegraph into ~/.local/bin so the .cursor/mcp.json stdio server launches by
# absolute path with Node 24 on PATH, regardless of the MCP launcher's environment.
# Resolve the real codegraph from the Node bin (NODE_BIN) with ~/.local/bin excluded
# from the lookup, so re-running install never links the symlink to itself.
CODEGRAPH_REAL="$(PATH="$NODE_BIN:/usr/local/bin:/usr/bin:/bin" command -v codegraph 2>/dev/null || true)"
if [ -n "$CODEGRAPH_REAL" ] && [ "$CODEGRAPH_REAL" != "$HOME/.local/bin/codegraph" ]; then
  ln -sf "$CODEGRAPH_REAL" "$HOME/.local/bin/codegraph"
fi
codegraph telemetry off >/dev/null 2>&1 || true
if [ ! -f "$REPO_ROOT/.codegraph/codegraph.db" ]; then
  log "building CodeGraph index"
  codegraph init "$REPO_ROOT" >>"$LOG" 2>&1 || log "codegraph init FAILED - see $LOG"
fi

# --- Kyber-Squad agents + skills for Claude -> ~/.claude --------------------
mkdir -p "${XDG_CONFIG_HOME:-$HOME/.config}"
"$HOME/.local/bin/kyber-weave" squad install "$HOME" --global --target claude </dev/null >>"$LOG" 2>&1 \
  || "$HOME/.local/bin/kyber-weave" squad update "$HOME" --global --target claude --replace-managed </dev/null >>"$LOG" 2>&1 \
  || log "kyber-squad install FAILED - see $LOG"

log "install complete"
