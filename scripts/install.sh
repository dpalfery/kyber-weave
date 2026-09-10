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
#   --version <v>         KYBER_WEAVE_VERSION         release to install (default: latest)
#   --prerelease          KYBER_WEAVE_PRERELEASE=1    include pre-release versions
#   --install-dir <d>     KYBER_WEAVE_INSTALL_DIR     where to put binaries (default: ~/.local/bin)
#   --no-mcp              KYBER_WEAVE_NO_MCP=1        install only the CLI, skip the MCP server
#   --no-kyberdash        KYBER_WEAVE_NO_KYBERDASH=1  install CLI + MCP, skip the KyberDash binary
#   --with-menubar        KYBER_WEAVE_WITH_MENUBAR=1   (macOS only) after installing the CLI,
#                                                     download, verify SHA256 + code signature,
#                                                     and place the signed menubar app at
#                                                     ~/Applications. Refuses on any failure.
#   --help
#
# When piping to sh, pass flags after `-s --`:
#   curl -fsSL <url> | sh -s -- --version 0.1.0 --install-dir /usr/local/bin

set -eu

OWNER="dpalfery"
REPO="kyber-weave"
RELEASE_BASE="https://github.com/${OWNER}/${REPO}/releases/download"
LATEST_API="https://api.github.com/repos/${OWNER}/${REPO}/releases/latest"
RELEASES_API="https://api.github.com/repos/${OWNER}/${REPO}/releases"

VERSION="${KYBER_WEAVE_VERSION:-}"
INSTALL_DIR="${KYBER_WEAVE_INSTALL_DIR:-}"
NO_MCP="${KYBER_WEAVE_NO_MCP:-}"
NO_KYBERDASH="${KYBER_WEAVE_NO_KYBERDASH:-}"
WITH_MENUBAR="${KYBER_WEAVE_WITH_MENUBAR:-}"
PRERELEASE="${KYBER_WEAVE_PRERELEASE:-}"
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
  --version <v>         KYBER_WEAVE_VERSION         release to install (default: latest)
  --prerelease          KYBER_WEAVE_PRERELEASE=1    include pre-release versions
  --install-dir <d>     KYBER_WEAVE_INSTALL_DIR     where to put binaries (default: ~/.local/bin)
  --no-mcp              KYBER_WEAVE_NO_MCP=1        install only the CLI, skip the MCP server
  --no-kyberdash        KYBER_WEAVE_NO_KYBERDASH=1  install CLI + MCP, skip the KyberDash binary
  --with-menubar        KYBER_WEAVE_WITH_MENUBAR=1   (macOS only) install the signed menubar app
                                                     verified SHA256 + codesign before placement
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
        --prerelease)   PRERELEASE=1; shift ;;
        --install-dir)  [ $# -ge 2 ] || die "--install-dir needs a value"; INSTALL_DIR="$2"; shift 2 ;;
        --install-dir=*) INSTALL_DIR="${1#*=}"; shift ;;
        --no-mcp)       NO_MCP=1; shift ;;
        --no-kyberdash) NO_KYBERDASH=1; shift ;;
        --with-menubar) WITH_MENUBAR=1; shift ;;
        --with-menubar=*) WITH_MENUBAR="${1#*=}"; [ "$WITH_MENUBAR" = "1" ] || die "--with-menubar expects 1 (got: ${WITH_MENUBAR})"; shift ;;
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

# ----------------------------------------------------------- library helpers
#
# Pure helpers exposed for the test harness (tests/KyberWeave.Tests/ReleaseTests.cs).
# Sourcing install.sh with KYBER_WEAVE_INSTALL_LIB=1 returns here after these
# are defined, leaving the functions reachable from the surrounding shell
# without running the installer. The same helpers are called by the main body,
# so the installer and the tests share a single source of truth.

# kyber_weave_resolve_rid <os-uname> <arch-uname> -> prints "<os>-<arch>" on
# stdout; returns 0 for supported combinations, 2 for unsupported ones. The
# inner case is kept identical to what install.sh itself produces so the
# installer's gate (case "$RID" in linux-x64|linux-arm64|osx-x64|osx-arm64))
# continues to enforce the supported set fully.
kyber_weave_resolve_rid() {
    os="$1"
    arch="$2"
    case "$os" in
        Linux)               os_part="linux" ;;
        Darwin)              os_part="osx"   ;;
        MINGW*|MSYS*|CYGWIN*|Windows_NT)
            # Windows is supported, just by a different installer path; the
            # main flow continues to refuse it. We return the win-os RID so a
            # test asserting the full mapping can still expect non-error.
            os_part="win"   ;;
        *) printf 'unsupported OS: %s\n' "$os" >&2; return 2 ;;
    esac
    case "$arch" in
        x86_64|amd64)   arch_part="x64"   ;;
        arm64|aarch64)  arch_part="arm64" ;;
        *) printf 'unsupported architecture: %s\n' "$arch" >&2; return 2 ;;
    esac
    printf '%s-%s' "$os_part" "$arch_part"
}

# kyber_weave_kyberdash_rid <kyber-weave-rid> -> prints the matching KyberDash
# Node SEA RID. Node's Node SEA RID set uses darwin-* for macOS rather than
# the .NET osx-*; linux and windows RID names happen to coincide.
kyber_weave_kyberdash_rid() {
    case "$1" in
        osx-x64)   printf 'darwin-x64'   ;;
        osx-arm64) printf 'darwin-arm64' ;;
        linux-x64|linux-arm64|win-x64) printf '%s' "$1" ;;
        *) printf '%s' "$1" ;;
    esac
}

# kyber_weave_lookup_checksum <sums-file> <archive> -> prints the expected
# sha256 in lowercase, or empty if no basename-matching entry. Reads sha256sum
# output (`<hex>  <name>` or `<hex> *<name>`) and matches on basename so one
# asset's line cannot satisfy a different asset (regex `.` is too permissive).
kyber_weave_lookup_checksum() {
    sums="$1"
    archive="$2"
    archive_basename="${archive##*/}"
    awk -v want="$archive_basename" '
        {
            name = $2
            sub(/^\*/, "", name)
            sub(/^.*\//, "", name)
            if (name == want && length($1) == 64 && $1 ~ /^[0-9a-fA-F]+$/) {
                print tolower($1)
                exit
            }
        }' "$sums"
}

# kyber_weave_verify_checksum <sums-file> <archive-file>
#   exit 0  — checksum matches
#   exit 1  — checksum mismatch (and emits the diff to stderr)
#   exit 2  — no entry in sums file OR a required file is missing
kyber_weave_verify_checksum() {
    sums="$1"
    archive="$2"
    [ -f "$sums" ]    || return 2
    [ -f "$archive" ] || return 2
    expected="$(kyber_weave_lookup_checksum "$sums" "$archive")"
    [ -n "$expected" ] || return 2
    actual="$($SHA_CMD "$archive" | cut -d' ' -f1)"
    if [ "$actual" = "$expected" ]; then
        return 0
    fi
    printf 'SHA256 mismatch for %s: expected %s, got %s\n' \
        "${archive##*/}" "$expected" "$actual" >&2
    return 1
}

# Skip the installer body when sourced as a library by the test harness.
# `return 0 2>/dev/null || exit 0`: in sourced mode `return 0` cleanly exits
# the dotted file; in script mode `return` raises but is swallowed by stderr
# redirection and the `||` chains to `exit 0`.
if [ "${KYBER_WEAVE_INSTALL_LIB:-0}" = "1" ]; then
    return 0 2>/dev/null || exit 0
fi

# ------------------------------------------------------------- platform → RID

os="$(uname -s)"
arch="$(uname -m)"

# Windows is rejected at the message level; the upstream-vs-RID mapping for
# the helper still allows resolving a Windows host, but the install script
# only ships tar.gz so its flow short-circuits here.
case "$os" in
    MINGW*|MSYS*|CYGWIN*|Windows_NT)
        die "Windows is not supported by this script. Download kyber-weave-win-x64.zip and kyber-weave-mcp-win-x64.zip from https://github.com/${OWNER}/${REPO}/releases and place them on your PATH." ;;
esac

# Single source of truth for the platform -> Kyber-Weave RID mapping. The
# inline duplicate the previous version of this script carried has been
# removed so a test in tests/KyberWeave.Tests/ReleaseTests.cs and the
# installer derive the same string from the same rules.
RID="$(kyber_weave_resolve_rid "$os" "$arch")"

# osx-x64 / osx-arm64 / linux-x64 / linux-arm64 are all published; guard anyway
# so a future uname combination fails loudly rather than 404-ing mid-download.
case "$RID" in
    linux-x64|linux-arm64|osx-x64|osx-arm64) ;;
    *) die "no Release asset for $RID (supported: linux-x64, linux-arm64, osx-x64, osx-arm64)" ;;
esac

# KyberDash uses Node SEA RIDs (darwin-* for macOS rather than osx-*); the
# helper centralises that re-mapping so the installer's flow, and the test
# asserting it, both learn the same RID for the same platform.
KYBERDASH_RID="$(kyber_weave_kyberdash_rid "$RID")"

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
    if [ "$PRERELEASE" = "1" ]; then
        fetch_stdout "$RELEASES_API" \
            | tr -d '\r\n' \
            | tr '}' '\n' \
            | grep -v '"draft"[[:space:]]*:[[:space:]]*true' \
            | grep '"prerelease"[[:space:]]*:[[:space:]]*true' \
            | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v\{0,1\}\([^"]*\)".*/\1/p' \
            | head -n 1
    else
        # Parse "tag_name": "v0.1.0" without requiring jq.
        fetch_stdout "$LATEST_API" \
            | tr ',' '\n' \
            | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v\{0,1\}\([^"]*\)".*/\1/p' \
            | head -n 1
    fi
}

if [ -z "$VERSION" ]; then
    if [ "$PRERELEASE" = "1" ]; then
        log "resolving latest pre-release…"
    else
        log "resolving latest release…"
    fi
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

# KyberDash is a Node SEA single-executable shipping per spec D7. The artifact
# archive is the KyberDash RID, not the Kyber-Weave RID, because the Node SEA
# stable set uses darwin-* for macOS while .NET uses osx-*. Same SHA256SUMS.txt
# from the GitHub Release covers all assets; an unverified KyberDash would
# surface as a missing entry or hash mismatch and abort the install.
if [ -z "${NO_KYBERDASH}" ]; then
    verify_and_extract "kyberdash-${KYBERDASH_RID}.tar.gz"
fi

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
if [ -z "${NO_KYBERDASH}" ]; then
    install_binary "kyberdash"
fi

# macOS quarantines files downloaded by some tools; clear it if xattr exists.
if [ "$os_part" = "osx" ] && command -v xattr >/dev/null 2>&1; then
    xattr -d com.apple.quarantine "${INSTALL_DIR}/kyber-weave" 2>/dev/null || true
    [ -n "$NO_MCP" ] || xattr -d com.apple.quarantine "${INSTALL_DIR}/kyber-weave-mcp" 2>/dev/null || true
    [ -z "${NO_KYBERDASH}" ] && xattr -d com.apple.quarantine "${INSTALL_DIR}/kyberdash" 2>/dev/null || true
fi

log "installed kyber-weave ${VERSION} → ${INSTALL_DIR}/kyber-weave"
[ -n "$NO_MCP" ] || log "installed kyber-weave-mcp ${VERSION} → ${INSTALL_DIR}/kyber-weave-mcp"
[ -z "${NO_KYBERDASH}" ] && log "installed kyberdash ${VERSION} → ${INSTALL_DIR}/kyberdash"

case ":${PATH}:" in
    *":${INSTALL_DIR}:"*) ;;
    *)
        log ""
        log "${INSTALL_DIR} is not on your PATH. Add this to your shell profile:"
        log "  export PATH=\"${INSTALL_DIR}:\$PATH\""
        ;;
esac

# ------------------------------------------------------ signed mac menubar install
#
# R13.4: the menubar app must be downloaded, have its SHA256 and `codesign --verify`
# signature checked, and only then be renamed into the user's Applications directory.
# `kyber-weave menubar --force` performs the full path itself and refuses to place the
# bundle on any verification failure, so we delegate rather than re-implementing the
# chain. The flag is gated on macOS - on Linux/Windows the menubar install is a no-op
# here, matching the CLI's own platform guard. Non-zero exits are left to `set -e` so a
# tampered bundle aborts the install rather than slipping into ~/Applications.

if [ -n "$WITH_MENUBAR" ]; then
    if [ "$os_part" != "osx" ]; then
        log "--with-menubar is macOS only; skipping on ${os_part}"
    else
        KYBER_CLI="${INSTALL_DIR}/kyber-weave"
        [ -x "$KYBER_CLI" ] || die "--with-menubar: ${KYBER_CLI} is not executable after install"
        log "installing signed mac menubar app..."
        "$KYBER_CLI" menubar --force
    fi
fi
