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
#   kyberdash-<node-rid>.tar.gz SHA256SUMS.txt
#
# The publish flags are kept identical to .github/workflows/release.yml. Single-file
# packaging is the whole point: the self-update failure this exists to catch only
# reproduces against a real single-file host, never under `dotnet run`.
#
# The KyberDash archive is the Node single-executable the `build-kyberdash` job makes,
# built the same way for this machine's RID. It needs node and npm on PATH and downloads
# a prebuilt Node from nodejs.org, cached under <out>/.cache. The local server ignores
# that directory.
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
    if [ "$(type -t restore_dash_manifest 2>/dev/null || true)" = "function" ]; then
        restore_dash_manifest
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

# release.yml's `build-kyberdash` job is the authority for these steps. ReleaseTests pins
# the fuse, the postject package, and dash/.nvmrc to that job, so a change there that is
# not made here fails a test rather than a release. Three things differ, and all are local
# concerns. The SEA blob is written by the downloaded Node rather than the runner's, so the
# host's own Node version does not matter. The Node archive is checked against nodejs.org's
# SHASUMS256.txt, because it is cached between runs. And dash/package.json is put back
# after `npm version` stamps it.
NODE_SEA_FUSE="NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2"
POSTJECT_PACKAGE="postject@1.0.0-alpha.6"

DASH_MANIFEST_BACKUP=""
restore_dash_manifest() {
    [ -n "$DASH_MANIFEST_BACKUP" ] || return 0
    cp "${DASH_MANIFEST_BACKUP}/package.json" "${SOURCE_ROOT}/dash/package.json"
    cp "${DASH_MANIFEST_BACKUP}/package-lock.json" "${SOURCE_ROOT}/dash/package-lock.json"
    rm -rf "$DASH_MANIFEST_BACKUP"
    DASH_MANIFEST_BACKUP=""
}

# Runs a noisy step with its output held back, and shows it only if the step fails.
quietly() {
    step_log="${STAGING}/step.log"
    if ! "$@" > "$step_log" 2>&1; then
        cat "$step_log" >&2
        die "failed: $*"
    fi
}

# Downloads into the cache under a temporary name, so an interrupted transfer never
# leaves a truncated file where the next run would trust it. Retried, because CI runs this
# on every pull request, and one dropped connection to nodejs.org should not fail the
# merge gate.
fetch_cached() {
    url="$1"
    target="$2"
    [ -f "$target" ] && return 0
    curl -fsSL --retry 3 --retry-delay 2 --retry-connrefused -o "${target}.part" "$url" \
        || die "could not download ${url}"
    mv "${target}.part" "$target"
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
    [ -f "${dash}/.nvmrc" ] || die "${dash}/.nvmrc is missing, so there is no Node version to build with"

    node_version="$(tr -d '[:space:]' < "${dash}/.nvmrc")"
    node_version="${node_version#v}"
    log "building kyberdash ${VERSION} (${kyberdash_rid}, Node ${node_version})"

    cache="${OUT}/.cache/node-v${node_version}"
    mkdir -p "$cache" "${stage}/node" "${stage}/out"
    tarball="node-v${node_version}-${node_platform}-${node_arch}.tar.xz"
    base="https://nodejs.org/dist/v${node_version}"
    fetch_cached "${base}/SHASUMS256.txt" "${cache}/SHASUMS256.txt"
    fetch_cached "${base}/${tarball}" "${cache}/${tarball}"

    # Neither cached file is trusted after a failure, so both are removed either way. The
    # messages differ because the fixes do: a mismatch is a bad download that a rerun
    # replaces, and a missing entry means the name asked for does not exist.
    expected="$(awk -v name="$tarball" '$2 == name { print $1 }' "${cache}/SHASUMS256.txt")"
    actual="$($SHA_CMD "${cache}/${tarball}" | awk '{ print $1 }')"
    if [ -z "$expected" ]; then
        rm -f "${cache}/${tarball}" "${cache}/SHASUMS256.txt"
        die "nodejs.org's SHASUMS256.txt for v${node_version} lists no ${tarball}; check the version in dash/.nvmrc"
    fi
    if [ "$expected" != "$actual" ]; then
        rm -f "${cache}/${tarball}" "${cache}/SHASUMS256.txt"
        die "${tarball} failed its checksum (expected ${expected}, got ${actual}); the cached copy was removed, so rerun"
    fi

    tar -xJf "${cache}/${tarball}" -C "${stage}/node" --strip-components=1
    node_bin="${stage}/node/bin/node"
    node_error="$("$node_bin" --version 2>&1)" \
        || die "the Node for ${kyberdash_rid} does not run on this machine (${node_error}); pass --no-kyberdash"

    # `npm version` rewrites the manifest and lockfile, which a worktree build throws
    # away with the worktree and a working-tree build must put back.
    if [ -z "$REF" ]; then
        DASH_MANIFEST_BACKUP="$(mktemp -d "${TMPDIR:-/tmp}/kw-dash-manifest-XXXXXX")"
        cp "${dash}/package.json" "${dash}/package-lock.json" "$DASH_MANIFEST_BACKUP/"
    fi
    (
        cd "$dash"
        quietly npm ci --no-audit --no-fund
        quietly npm version "$VERSION" --no-git-tag-version --allow-same-version
        quietly npx tsup --config tsup.sea.config.ts
    )
    [ -f "${dash}/dist-sea/main.js" ] || die "tsup did not emit dist-sea/main.js"

    # The main script is the CommonJS shim. The ESM bundle and the stamped package.json
    # ride along as assets; tsup.sea.config.ts and src/sea-shim.cjs say why.
    cat > "${stage}/sea-config.json" <<EOF
{
  "main": "${dash}/src/sea-shim.cjs",
  "output": "${stage}/sea-prep.blob",
  "useSnapshot": false,
  "disableExperimentalSEAWarning": true,
  "assets": {
    "main.js": "${dash}/dist-sea/main.js",
    "package.json": "${dash}/package.json"
  }
}
EOF
    quietly "$node_bin" --experimental-sea-config "${stage}/sea-config.json"
    restore_dash_manifest

    binary="${stage}/out/kyberdash"
    cp "$node_bin" "$binary"
    chmod 755 "$binary"
    postject_args=(--sentinel-fuse "$NODE_SEA_FUSE")
    if [ "$node_platform" = "darwin" ]; then
        postject_args+=(--macho-segment-name NODE_SEA)
    fi
    quietly npm exec --yes --package="$POSTJECT_PACKAGE" -- \
        postject "$binary" NODE_SEA_BLOB "${stage}/sea-prep.blob" "${postject_args[@]}"

    # postject invalidates the embedded signature; macOS will not run the binary until it
    # is re-signed.
    if [ "$node_platform" = "darwin" ]; then
        command -v codesign >/dev/null 2>&1 || die "a darwin kyberdash must be ad-hoc signed; build it on macOS"
        codesign --sign - --force "$binary"
        codesign --verify --verbose "$binary" || true
        xattr -c "$binary" 2>/dev/null || true
    fi

    # A corrupt blob or a failed inject still yields a Node binary, which answers
    # --version with Node's version rather than ours.
    reported="$("$binary" --version 2>&1)" || die "kyberdash --version failed: ${reported}"
    [ "$reported" = "$VERSION" ] || die "kyberdash reports '${reported}', expected '${VERSION}'"

    cp "${dash}/THIRD_PARTY_NOTICES.md" "${stage}/out/THIRD_PARTY_NOTICES.md"
    tar -C "${stage}/out" -czf "${DEST}/kyberdash-${kyberdash_rid}.tar.gz" kyberdash THIRD_PARTY_NOTICES.md
}

if [ -z "$NO_KYBERDASH" ]; then
    build_kyberdash
fi

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
