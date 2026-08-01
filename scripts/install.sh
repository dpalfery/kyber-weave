#!/bin/sh
# Kyber-Weave installer.
#
#   curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh
#
# Downloads the self-contained `kyber-weave` and `kyber-weave-mcp` binaries for
# this platform from a GitHub Release and verifies them against the release's
# SHA256SUMS.txt. No .NET runtime, no sudo, no compiler required.
#
# Options (flags, or the matching env vars):
#   --version <v>       KYBER_WEAVE_VERSION      release to install (default: latest)
#   --install-dir <d>   KYBER_WEAVE_INSTALL_DIR  where to put binaries (default: ~/.local/bin)
#   --no-mcp            KYBER_WEAVE_NO_MCP=1     install only the CLI, skip the MCP server
#   --help
#
# When piping to sh, pass flags after `-s --`:
#   curl -fsSL <url> | sh -s -- --version 0.1.0 --install-dir /usr/local/bin

set -eu

OWNER="dpalfery"
REPO="kyber-weave"
RELEASE_BASE="https://github.com/${OWNER}/${REPO}/releases/download"
LATEST_API="https://api.github.com/repos/${OWNER}/${REPO}/releases/latest"

VERSION="${KYBER_WEAVE_VERSION:-}"
INSTALL_DIR="${KYBER_WEAVE_INSTALL_DIR:-}"
NO_MCP="${KYBER_WEAVE_NO_MCP:-}"
TMPDIR_KW=""

log() { printf 'kyber-weave: %s\n' "$1" >&2; }

die() {
    printf 'kyber-weave: error: %s\n' "$1" >&2
    exit 1
}

usage() {
    # Written out literally rather than read from "$0": under `curl … | sh`
    # there is no script file to read back.
    cat >&2 <<'EOF'
Kyber-Weave installer.

  curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh

Downloads the self-contained `kyber-weave` and `kyber-weave-mcp` binaries for
this platform from a GitHub Release and verifies them against the release's
SHA256SUMS.txt. No .NET runtime, no sudo, no compiler required.

Options (flags, or the matching env vars):
  --version <v>       KYBER_WEAVE_VERSION      release to install (default: latest)
  --install-dir <d>   KYBER_WEAVE_INSTALL_DIR  where to put binaries (default: ~/.local/bin)
  --no-mcp            KYBER_WEAVE_NO_MCP=1     install only the CLI, skip the MCP server
  --help

When piping to sh, pass flags after `-s --`:
  curl -fsSL <url> | sh -s -- --version 0.1.0 --install-dir /usr/local/bin
EOF
    exit 0
}

cleanup() {
    [ -n "$TMPDIR_KW" ] && [ -d "$TMPDIR_KW" ] && rm -rf "$TMPDIR_KW"
    return 0
}
trap cleanup EXIT INT TERM

while [ $# -gt 0 ]; do
    case "$1" in
        --version)      [ $# -ge 2 ] || die "--version needs a value"; VERSION="$2"; shift 2 ;;
        --version=*)    VERSION="${1#*=}"; shift ;;
        --install-dir)  [ $# -ge 2 ] || die "--install-dir needs a value"; INSTALL_DIR="$2"; shift 2 ;;
        --install-dir=*) INSTALL_DIR="${1#*=}"; shift ;;
        --no-mcp)       NO_MCP=1; shift ;;
        -h|--help)      usage ;;
        *)              die "unknown option: $1 (try --help)" ;;
    esac
done

# ---------------------------------------------------------------- prerequisites

if command -v curl >/dev/null 2>&1; then
    DOWNLOADER=curl
elif command -v wget >/dev/null 2>&1; then
    DOWNLOADER=wget
else
    die "need curl or wget on PATH"
fi

command -v tar >/dev/null 2>&1 || die "need tar on PATH"

if command -v sha256sum >/dev/null 2>&1; then
    SHA_CMD="sha256sum"
elif command -v shasum >/dev/null 2>&1; then
    SHA_CMD="shasum -a 256"
else
    die "need sha256sum or shasum on PATH to verify downloads"
fi

# ------------------------------------------------------------- platform → RID

os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
    Linux)  os_part="linux" ;;
    Darwin) os_part="osx" ;;
    MINGW*|MSYS*|CYGWIN*|Windows_NT)
        die "Windows is not supported by this script. Use: npm i -g @dpalfery/kyber-weave" ;;
    *)      die "unsupported OS: $os" ;;
esac

case "$arch" in
    x86_64|amd64)   arch_part="x64" ;;
    arm64|aarch64)  arch_part="arm64" ;;
    *)              die "unsupported architecture: $arch" ;;
esac

RID="${os_part}-${arch_part}"

# osx-x64 / osx-arm64 / linux-x64 / linux-arm64 are all published; guard anyway
# so a future uname combination fails loudly rather than 404-ing mid-download.
case "$RID" in
    linux-x64|linux-arm64|osx-x64|osx-arm64) ;;
    *) die "no Release asset for $RID (supported: linux-x64, linux-arm64, osx-x64, osx-arm64)" ;;
esac

# ------------------------------------------------------------------- download

# fetch <url> <dest-file>
fetch() {
    case "$1" in
        https://*) ;;
        *) die "refusing non-HTTPS URL: $1" ;;
    esac
    if [ "$DOWNLOADER" = curl ]; then
        # --proto '=https' / --proto-redir keeps redirects on HTTPS only.
        # Transport errors are silenced; callers report them with context.
        curl -fsSL --proto '=https' --proto-redir '=https' -o "$2" "$1" 2>/dev/null
    else
        wget -q --https-only -O "$2" "$1" 2>/dev/null
    fi
}

# fetch_stdout <url>
fetch_stdout() {
    case "$1" in
        https://*) ;;
        *) die "refusing non-HTTPS URL: $1" ;;
    esac
    if [ "$DOWNLOADER" = curl ]; then
        curl -fsSL --proto '=https' --proto-redir '=https' "$1" 2>/dev/null
    else
        wget -q --https-only -O - "$1" 2>/dev/null
    fi
}

resolve_latest_version() {
    # Parse "tag_name": "v0.1.0" without requiring jq.
    fetch_stdout "$LATEST_API" \
        | tr ',' '\n' \
        | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v\{0,1\}\([^"]*\)".*/\1/p' \
        | head -n 1
}

if [ -z "$VERSION" ]; then
    log "resolving latest release…"
    VERSION="$(resolve_latest_version || true)"
    [ -n "$VERSION" ] || die "could not resolve the latest release. Pass --version <x.y.z> explicitly."
fi

VERSION="${VERSION#v}"
TAG="v${VERSION}"

if [ -z "$INSTALL_DIR" ]; then
    [ -n "${HOME:-}" ] || die "HOME is unset; pass --install-dir <dir> (or set KYBER_WEAVE_INSTALL_DIR)"
    INSTALL_DIR="${HOME}/.local/bin"
fi

TMPDIR_KW="$(mktemp -d 2>/dev/null || mktemp -d -t kyber-weave)"

log "installing ${TAG} (${RID}) → ${INSTALL_DIR}"

SUMS="${TMPDIR_KW}/SHA256SUMS.txt"
fetch "${RELEASE_BASE}/${TAG}/SHA256SUMS.txt" "$SUMS" \
    || die "could not download SHA256SUMS.txt for ${TAG}. Does that release exist?"

# verify_and_extract <archive-name>
verify_and_extract() {
    archive="$1"
    url="${RELEASE_BASE}/${TAG}/${archive}"
    dest="${TMPDIR_KW}/${archive}"

    log "downloading ${archive}…"
    fetch "$url" "$dest" || die "download failed: ${url}"

    # Exact filename match against `sha256sum` output (`<hex>  <name>` or
    # `<hex> *<name>`). A regex over the name would let `.` match anything and
    # let one asset's line satisfy a different asset.
    expected="$(awk -v want="$archive" '
        {
            name = $2
            sub(/^\*/, "", name)
            sub(/^.*\//, "", name)
            if (name == want && length($1) == 64 && $1 ~ /^[0-9a-fA-F]+$/) {
                print tolower($1)
                exit
            }
        }' "$SUMS")"
    [ -n "$expected" ] || die "SHA256SUMS.txt has no entry for ${archive}; refusing to install an unverified asset"

    actual="$($SHA_CMD "$dest" | cut -d' ' -f1)"
    [ "$actual" = "$expected" ] \
        || die "SHA256 mismatch for ${archive}: expected ${expected}, got ${actual}"

    tar -xzf "$dest" -C "$TMPDIR_KW"
}

verify_and_extract "kyber-weave-${RID}.tar.gz"
[ -n "$NO_MCP" ] || verify_and_extract "kyber-weave-mcp-${RID}.tar.gz"

# -------------------------------------------------------------------- install

mkdir -p "$INSTALL_DIR" || die "cannot create ${INSTALL_DIR}"
[ -w "$INSTALL_DIR" ] || die "no write permission for ${INSTALL_DIR}. Re-run with --install-dir <dir>, or use sudo."

install_binary() {
    name="$1"
    src="${TMPDIR_KW}/${name}"
    [ -f "$src" ] || die "archive did not contain ${name}"
    chmod 755 "$src"
    # Copy to a temp name then rename, so replacing a running binary is atomic.
    cp "$src" "${INSTALL_DIR}/.${name}.new"
    mv -f "${INSTALL_DIR}/.${name}.new" "${INSTALL_DIR}/${name}"
}

install_binary "kyber-weave"
[ -n "$NO_MCP" ] || install_binary "kyber-weave-mcp"

# macOS quarantines files downloaded by some tools; clear it if xattr exists.
if [ "$os_part" = "osx" ] && command -v xattr >/dev/null 2>&1; then
    xattr -d com.apple.quarantine "${INSTALL_DIR}/kyber-weave" 2>/dev/null || true
    [ -n "$NO_MCP" ] || xattr -d com.apple.quarantine "${INSTALL_DIR}/kyber-weave-mcp" 2>/dev/null || true
fi

log "installed kyber-weave ${VERSION} → ${INSTALL_DIR}/kyber-weave"
[ -n "$NO_MCP" ] || log "installed kyber-weave-mcp ${VERSION} → ${INSTALL_DIR}/kyber-weave-mcp"

case ":${PATH}:" in
    *":${INSTALL_DIR}:"*) ;;
    *)
        log ""
        log "${INSTALL_DIR} is not on your PATH. Add this to your shell profile:"
        log "  export PATH=\"${INSTALL_DIR}:\$PATH\""
        ;;
esac
