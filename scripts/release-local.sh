#!/usr/bin/env bash
# Builds the Release artifacts for one RID into a local release tree.
#
#   scripts/release-local.sh --version 99.0.0-local --out .local-release
#
# Produces `<out>/v<version>/` holding the same asset names the Release workflow
# attaches, so `scripts/local-release-server.py` can serve it as a stand-in release:
#
#   kyber-weave-<rid>.tar.gz    kyber-weave-mcp-<rid>.tar.gz
#   kyber-weave-arbiter-<rid>.tar.gz
#   kyber-squad-<version>.zip   kyber-squad-plugin-<version>.zip
#   kyberdash-<node-rid>.tar.gz kyber-weave-sbom-<version>.cdx.json SHA256SUMS.txt
#   <archive-stem>.cdx.json     (one per-artifact SBOM per archive above)
#
# The publish flags are kept identical to .github/workflows/release.yml. Single-file
# packaging is the whole point: the self-update failure this exists to catch only
# reproduces against a real single-file host, never under `dotnet run`.
#
# The KyberDash archive packages the single-file .NET binaries of
# `dash/server/KyberDash.{Cli,Api,Mcp}` with the React SPA packed into
# `dash/dist-sea/web.json` and embedded at publish time, matching the `build-kyberdash`
# job. It needs node and npm on PATH.
#
# Options:
#   --version <v>   version stamped into the binaries and asset names (required)
#   --out <dir>     release tree root (default: .local-release)
#   --rid <rid>     target RID (default: this machine's)
#   --ref <ref>     build from a git worktree at <ref> instead of the working tree
#   --no-squad      skip the Squad archives (self-update only)
#   --no-kyberdash  skip the KyberDash archive, as every release before 0.1.7-rc.9 did

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIGURATION=Release
VERSION=""
OUT="${REPO_ROOT}/.local-release"
RID=""
REF=""
NO_SQUAD=""
NO_KYBERDASH=""

die() { printf 'release-local: error: %s\n' "$1" >&2; exit 1; }
log() { printf 'release-local: %s\n' "$1" >&2; }

while [ $# -gt 0 ]; do
    case "$1" in
        --version) [ $# -ge 2 ] || die "--version needs a value"; VERSION="$2"; shift 2 ;;
        --out)     [ $# -ge 2 ] || die "--out needs a value"; OUT="$2"; shift 2 ;;
        --rid)     [ $# -ge 2 ] || die "--rid needs a value"; RID="$2"; shift 2 ;;
        --ref)     [ $# -ge 2 ] || die "--ref needs a value"; REF="$2"; shift 2 ;;
        --no-squad) NO_SQUAD=1; shift ;;
        --no-kyberdash) NO_KYBERDASH=1; shift ;;
        -h|--help) sed -n '2,28p' "$0" >&2; exit 0 ;;
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

# The `<hex>  <name>` spacing is a parsing contract, not cosmetics: install.sh matches on
# it and GitHubSquadReleaseSource rejects any row that is not exactly 64 hex + two spaces
# + the asset name. sha256sum and `shasum -a 256` agree on that format.
if command -v sha256sum >/dev/null 2>&1; then
    SHA_CMD="sha256sum"
else
    SHA_CMD="shasum -a 256"
fi

# ------------------------------------------------------------------- KyberDash

# release.yml's `build-kyberdash` job is the authority for these steps. ReleaseTests
# pins the single-file publish flags, web dashboard pack, codesign, and smoke scripts
# to that job, so a change there that is not made here fails a test rather than a release.

# Runs a noisy step with its output held back, and shows it only if the step fails.
quietly() {
    step_log="${STAGING}/step.log"
    if ! "$@" > "$step_log" 2>&1; then
        cat "$step_log" >&2
        die "failed: $*"
    fi
}

build_kyberdash() {
    stage="${STAGING}/kyberdash"
    dash="${SOURCE_ROOT}/dash"

    case "$RID" in
        osx-arm64)   node_platform=darwin; node_arch=arm64 ;;
        osx-x64)     node_platform=darwin; node_arch=x64 ;;
        linux-arm64) node_platform=linux;  node_arch=arm64 ;;
        linux-x64)   node_platform=linux;  node_arch=x64 ;;
        *) die "no local KyberDash build for ${RID}; pass --no-kyberdash" ;;
    esac
    kyberdash_rid="${node_platform}-${node_arch}"
    command -v npm >/dev/null 2>&1 || die "building kyberdash needs npm on PATH; pass --no-kyberdash to skip it"
    command -v node >/dev/null 2>&1 || die "building kyberdash needs node on PATH; pass --no-kyberdash to skip it"
    log "building kyberdash ${VERSION} (${kyberdash_rid})"

    mkdir -p "${stage}/out"

    (
        cd "$dash"
        quietly npm --prefix web ci --no-audit --no-fund
        quietly npm --prefix web run build
        quietly node scripts/pack-sea-web.mjs dist/dash dist-sea/web.json
    )
    [ -f "${dash}/dist-sea/web.json" ] || die "pack-sea-web did not emit dist-sea/web.json"

    for proj in \
        "${dash}/server/KyberDash.Cli/KyberDash.Cli.csproj" \
        "${dash}/server/KyberDash.Api/KyberDash.Api.csproj" \
        "${dash}/server/KyberDash.Mcp/KyberDash.Mcp.csproj"; do
        dotnet publish "$proj" \
            -c "$CONFIGURATION" \
            -r "$RID" \
            --self-contained true \
            -p:PublishSingleFile=true \
            -p:IncludeNativeLibrariesForSelfExtract=true \
            -p:DebugType=None \
            -p:DebugSymbols=false \
            -p:Version="$VERSION" \
            -o "${stage}/out" \
            --nologo -v quiet >/dev/null
    done

    binary="${stage}/out/kyberdash"
    api_binary="${stage}/out/kyberdash-api"
    mcp_binary="${stage}/out/kyberdash-mcp"
    for bin in "$binary" "$api_binary" "$mcp_binary"; do
        [ -f "$bin" ] || die "kyberdash binary missing at ${bin}"
        chmod 755 "$bin"
        if [ "$node_platform" = "darwin" ]; then
            command -v codesign >/dev/null 2>&1 || die "a darwin kyberdash must be ad-hoc signed; build it on macOS"
            codesign --sign - --force "$bin"
            codesign --verify --verbose "$bin" || true
            xattr -c "$bin" 2>/dev/null || true
        fi
    done

    reported="$("$binary" --version 2>&1)" || die "kyberdash --version failed: ${reported}"
    [ "$reported" = "$VERSION" ] || die "kyberdash reports '${reported}', expected '${VERSION}'"

    # Verify the embedded dashboard serves the SPA, not the "not built" page.
    quietly node "${dash}/scripts/sea-web-smoke.mjs" "$binary"

    # Verify the MCP command speaks JSON-RPC over stdio and reaches the loopback fixture.
    quietly node "${dash}/scripts/sea-mcp-smoke.mjs" "$binary"

    cp "${dash}/THIRD_PARTY_NOTICES.md" "${stage}/out/THIRD_PARTY_NOTICES.md"
    tar -C "${stage}/out" -czf "${DEST}/kyberdash-${kyberdash_rid}.tar.gz" kyberdash kyberdash-api kyberdash-mcp THIRD_PARTY_NOTICES.md
}

if [ -z "$NO_KYBERDASH" ]; then
    build_kyberdash
fi

publish "src/KyberWeave.Cli/KyberWeave.Cli.csproj" "kyber-weave" "${STAGING}/cli"
publish "src/KyberWeave.Mcp/KyberWeave.Mcp.csproj" "kyber-weave-mcp" "${STAGING}/mcp"
publish "src/KyberWeave.Arbiter/KyberWeave.Arbiter.csproj" "kyber-weave-arbiter" "${STAGING}/arbiter"

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

# The SBOM is a release asset: release.yml attaches it to the GitHub Release and
# scripts/verify-release-checksums.sh expects it in the asset list, so the local
# tree has to carry one too — otherwise the local tree and the published release
# do not describe the same set of files.
#
# It is generated from SOURCE_ROOT, so a --ref build describes the code being
# released rather than whatever is in the working tree.
log "generating the SBOM for ${VERSION}"
bash "${SOURCE_ROOT}/scripts/generate-sbom.sh" "$DEST" "$VERSION"

# Per-artifact SBOMs (issue #417): one <archive-stem>.cdx.json per published
# archive in DEST, via the shared pinned-Syft generator. Runs after the
# aggregate SBOM and before the manifest below, so `$SHA_CMD *` covers both
# the aggregate and the per-artifact SBOMs. Single-file .NET
# not-measured markers stay in the generator; nothing is special-cased here.
log "generating per-artifact SBOMs for ${VERSION}"
bash "${SOURCE_ROOT}/scripts/generate-artifact-sboms.sh" "$DEST" "$VERSION"

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
