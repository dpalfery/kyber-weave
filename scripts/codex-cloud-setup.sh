#!/usr/bin/env bash
# Prepare a Codex Cloud environment to use Kyber-Weave, its docs skill, and
# the Codex target of Kyber-Squad.

set -Eeuo pipefail

REPO_ROOT="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
BIN_DIR="${KYBER_WEAVE_INSTALL_DIR:-$HOME/.local/bin}"
CLI="$BIN_DIR/kyber-weave"
PROFILE="$HOME/.bashrc"

mkdir -p "$BIN_DIR" "${XDG_CONFIG_HOME:-$HOME/.config}"
export PATH="$BIN_DIR:$HOME/.local/bin:$PATH"
export TERM=dumb NO_COLOR=1 COLUMNS=200 DEBIAN_FRONTEND=noninteractive

# Codex Cloud runs setup and agent commands in separate Bash sessions. Keep
# the CLI directory on PATH for the agent phase as well as this script.
touch "$PROFILE"
printf -v BIN_DIR_SHELL '%q' "$BIN_DIR"
PATH_LINE="export PATH=$BIN_DIR_SHELL:\$PATH"
if ! grep -Fqx -- "$PATH_LINE" "$PROFILE"; then
    printf '\n# Kyber-Weave installed by scripts/codex-cloud-setup.sh\n%s\n' \
        "$PATH_LINE" >> "$PROFILE"
fi

# The repository gates need the SDK pinned in global.json and sqlite3 for the
# read-only CodeGraph adapter and analysis persistence tests.
compatible_sdk_version() {
    command -v dotnet >/dev/null 2>&1 || return 1
    dotnet --list-sdks 2>/dev/null | awk '
        {
            split($1, version, ".")
            if (version[1] == 10 && version[2] == 0 && version[3] + 0 >= 100) {
                print $1
                found = 1
                exit
            }
        }
        END { if (!found) exit 1 }
    '
}

SDK_VERSION="$(compatible_sdk_version || true)"
if [[ -z "$SDK_VERSION" ]]; then
    if ! command -v apt-get >/dev/null 2>&1; then
        echo "error: this cloud image needs a compatible .NET 10 SDK; apt-get is unavailable" >&2
        exit 1
    fi
    if ! apt-get install -y dotnet-sdk-10.0; then
        apt-get update
        apt-get install -y dotnet-sdk-10.0
    fi
    SDK_VERSION="$(compatible_sdk_version || true)"
fi

if [[ -z "$SDK_VERSION" ]]; then
    echo "error: could not install a .NET 10.0.1xx-or-later SDK compatible with global.json" >&2
    exit 1
fi

if ! command -v sqlite3 >/dev/null 2>&1; then
    if ! command -v apt-get >/dev/null 2>&1; then
        echo "error: sqlite3 is required by the repository gates; apt-get is unavailable" >&2
        exit 1
    fi
    if ! apt-get install -y sqlite3; then
        apt-get update
        apt-get install -y sqlite3
    fi
fi

INSTALL_ARGS=(--install-dir "$BIN_DIR" --no-kyberdash)
if [[ -n "${KYBER_WEAVE_VERSION:-}" ]]; then
    INSTALL_ARGS+=(--version "$KYBER_WEAVE_VERSION")
elif [[ "${KYBER_WEAVE_PRERELEASE:-1}" == "1" ]]; then
    INSTALL_ARGS+=(--prerelease)
fi

echo "Installing Kyber-Weave CLI and MCP binaries..."
sh "$REPO_ROOT/scripts/install.sh" "${INSTALL_ARGS[@]}"

if ! command -v apm >/dev/null 2>&1; then
    echo "Installing APM for the Kyber-Weave docs skill..."
    curl -fsSL https://aka.ms/apm-unix | sh
    export PATH="$HOME/.local/bin:$PATH"
fi

if ! command -v apm >/dev/null 2>&1; then
    echo "error: APM installation completed but 'apm' is not on PATH" >&2
    exit 1
fi

echo "Installing the Kyber-Weave documentation scaffolding and Codex skill..."
if ! DOCS_OUTPUT="$("$CLI" docs init "$REPO_ROOT" --target codex 2>&1)"; then
    printf '%s\n' "$DOCS_OUTPUT" >&2
    exit 1
fi
printf '%s\n' "$DOCS_OUTPUT"
if [[ "$DOCS_OUTPUT" == *"skipped"* && "$DOCS_OUTPUT" == *"kyber-weave-docs skill"* ]]; then
    echo "error: docs init did not deploy the kyber-weave-docs skill" >&2
    exit 1
fi

# Keep Squad state and rendered agent files in the ephemeral cloud user's
# Codex home. A project-scope install would rewrite this repo's tracked
# deployment receipt and dirty the checkout on every cloud session.
echo "Installing or updating the Kyber-Squad Codex target..."
(
    cd "$HOME"
    if ! "$CLI" squad update --global --target codex --yes; then
        "$CLI" squad install --global --target codex --yes
    fi
)

echo "Kyber-Weave Codex Cloud setup complete."
echo "CLI: $CLI"
echo "Docs skill: APM Codex target in $REPO_ROOT"
echo "Squad: global Codex deployment under $HOME/.codex"
