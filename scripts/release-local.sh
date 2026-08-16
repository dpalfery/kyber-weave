#!/usr/bin/env bash
# Builds the Release artifacts for one RID into a local release tree.
#
#   scripts/release-local.sh --version 99.0.0-local --out .local-release
#
# Produces `<out>/v<version>/` holding the same asset names the Release workflow
# attaches, so `scripts/local-release-server.py` can serve it as a stand-in release:
#
#   kyber-weave-<rid>.tar.gz    kyber-weave-mcp-<rid>.tar.gz
#   kyber-squad-<version>.zip   kyber-squad-plugin-<version>.zip
#   SHA256SUMS.txt
#
# The publish flags are kept identical to .github/workflows/release.yml. Single-file
# packaging is the whole point: the self-update failure this exists to catch only
# reproduces against a real single-file host, never under `dotnet run`.
#
# Options:
#   --version <v>   version stamped into the binaries and asset names (required)
#   --out <dir>     release tree root (default: .local-release)
#   --rid <rid>     target RID (default: this machine's)
#   --ref <ref>     build from a git worktree at <ref> instead of the working tree
#   --no-squad      skip the Squad archives (self-update only)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIGURATION=Release
VERSION=""
OUT="${REPO_ROOT}/.local-release"
RID=""
REF=""
NO_SQUAD=""

die() { printf 'release-local: error: %s\n' "$1" >&2; exit 1; }
log() { printf 'release-local: %s\n' "$1" >&2; }

while [ $# -gt 0 ]; do
    case "$1" in
        --version) [ $# -ge 2 ] || die "--version needs a value"; VERSION="$2"; shift 2 ;;
        --out)     [ $# -ge 2 ] || die "--out needs a value"; OUT="$2"; shift 2 ;;
        --rid)     [ $# -ge 2 ] || die "--rid needs a value"; RID="$2"; shift 2 ;;
        --ref)     [ $# -ge 2 ] || die "--ref needs a value"; REF="$2"; shift 2 ;;
        --no-squad) NO_SQUAD=1; shift ;;
        -h|--help) sed -n '2,26p' "$0" >&2; exit 0 ;;
        *) die "unknown option: $1" ;;
    esac
done

[ -n "$VERSION" ] || die "--version is required"
VERSION="${VERSION#v}"

if [ -z "$RID" ]; then
    case "$(uname -s)" in
        Darwin) os_part=osx ;;
        Linux)  os_part=linux ;;
        *) die "unsupported OS $(uname -s); pass --rid" ;;
    esac
    case "$(uname -m)" in
        arm64|aarch64) arch_part=arm64 ;;
        x86_64|amd64)  arch_part=x64 ;;
        *) die "unsupported architecture $(uname -m); pass --rid" ;;
    esac
    RID="${os_part}-${arch_part}"
fi

# A worktree keeps the build off the working tree's files, so a harness can publish
# an older ref while uncommitted edits stay untouched.
WORKTREE=""
cleanup() {
    if [ -n "$WORKTREE" ] && [ -d "$WORKTREE" ]; then
        git -C "$REPO_ROOT" worktree remove --force "$WORKTREE" >/dev/null 2>&1 || true
    fi
    # Defined later; guard so an early failure does not trip `set -u`.
    if [ "$(type -t restore_locks 2>/dev/null || true)" = "function" ]; then
        restore_locks
    fi
}
trap cleanup EXIT INT TERM

SOURCE_ROOT="$REPO_ROOT"
if [ -n "$REF" ]; then
    WORKTREE="$(mktemp -d "${TMPDIR:-/tmp}/kw-worktree-XXXXXX")"
    rm -rf "$WORKTREE"
    log "checking out ${REF} into a worktree"
    git -C "$REPO_ROOT" worktree add --detach --quiet "$WORKTREE" "$REF" \
        || die "could not create a worktree at ${REF}"
    SOURCE_ROOT="$WORKTREE"
fi

TAG="v${VERSION}"
DEST="${OUT}/${TAG}"
mkdir -p "$DEST"
rm -f "${DEST}"/*

publish() {
    project="$1"
    binary="$2"
    stage="$3"

    log "publishing ${binary} ${VERSION} (${RID})"
    dotnet publish "${SOURCE_ROOT}/${project}" \
        -c "$CONFIGURATION" \
        -r "$RID" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=None \
        -p:DebugSymbols=false \
        -p:Version="$VERSION" \
        -o "$stage" \
        --nologo -v quiet >/dev/null

    tar -C "$stage" -czf "${DEST}/${binary}-${RID}.tar.gz" "$binary"
}

STAGING="$(mktemp -d "${TMPDIR:-/tmp}/kw-publish-XXXXXX")"

# A RID-specific restore rewrites packages.lock.json (adding an ILLink entry and a
# net10.0/<rid> section). CI never notices because its checkout is thrown away, but
# locally that is an unrelated edit appearing in every `git status` after a loop run.
# Snapshot the tracked lock files and put them back once publishing is done.
LOCK_BACKUP="$(mktemp -d "${TMPDIR:-/tmp}/kw-locks-XXXXXX")"
LOCK_FILES=""
if [ -z "$REF" ]; then
    LOCK_FILES="$(git -C "$REPO_ROOT" ls-files '*packages.lock.json' 2>/dev/null || true)"
    for lock in $LOCK_FILES; do
        mkdir -p "${LOCK_BACKUP}/$(dirname "$lock")"
        cp "${REPO_ROOT}/${lock}" "${LOCK_BACKUP}/${lock}"
    done
fi

restore_locks() {
    for lock in $LOCK_FILES; do
        [ -f "${LOCK_BACKUP}/${lock}" ] && cp "${LOCK_BACKUP}/${lock}" "${REPO_ROOT}/${lock}"
    done
    rm -rf "$LOCK_BACKUP"
}

publish "src/KyberWeave.Cli/KyberWeave.Cli.csproj" "kyber-weave" "${STAGING}/cli"
publish "src/KyberWeave.Mcp/KyberWeave.Mcp.csproj" "kyber-weave-mcp" "${STAGING}/mcp"

if [ -z "$NO_SQUAD" ]; then
    log "packing Squad ${VERSION}"
    dotnet build "${SOURCE_ROOT}/src/KyberWeave.Cli/KyberWeave.Cli.csproj" \
        -c "$CONFIGURATION" -p:Version="$VERSION" --nologo -v quiet >/dev/null
    dotnet run --project "${SOURCE_ROOT}/src/KyberWeave.Cli" --no-build -c "$CONFIGURATION" -- \
        squad pack --format all --out "${STAGING}/squad" --version "$VERSION" >/dev/null
    cp "${STAGING}/squad/kyber-squad-${VERSION}.zip" "$DEST/"
    cp "${STAGING}/squad/kyber-squad-plugin-${VERSION}.zip" "$DEST/"
fi

rm -rf "$STAGING"
restore_locks

# The `<hex>  <name>` spacing is a parsing contract, not cosmetics: install.sh matches on
# it and GitHubSquadReleaseSource rejects any row that is not exactly 64 hex + two spaces
# + the asset name. sha256sum and `shasum -a 256` agree on that format.
if command -v sha256sum >/dev/null 2>&1; then
    SHA_CMD="sha256sum"
else
    SHA_CMD="shasum -a 256"
fi

# Hash into a temp file: redirecting straight to SHA256SUMS.txt inside the same directory
# creates it before the glob expands, so the manifest would list itself.
(
    cd "$DEST"
    # Bare `*` (not `./*`) keeps the names unprefixed, which is the form both parsers want.
    # shellcheck disable=SC2086
    $SHA_CMD * > "${TMPDIR:-/tmp}/kw-sums.$$"
    mv "${TMPDIR:-/tmp}/kw-sums.$$" SHA256SUMS.txt
)

log "release tree ready at ${DEST}"
ls -1 "$DEST" >&2
