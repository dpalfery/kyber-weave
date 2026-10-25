#!/usr/bin/env bash
#
# syft-bootstrap.sh — pinned-Syft bootstrap shared by the SBOM generators.
#
# Sourced, not executed: both scripts/generate-sbom.sh (the aggregate SBOM)
# and scripts/generate-artifact-sboms.sh (one SBOM per published archive)
# source this file so there is exactly one place to bump the Syft pin.
#
# ---------------------------------------------------------------------------
# WHY SYFT, AND WHY IT IS PINNED BY SHA-256
# ---------------------------------------------------------------------------
# The obvious .NET answer is `dotnet tool install CycloneDX` and let it emit the SBOM.
# That is not available here: nuget.org is never used as a distribution channel in this
# repository (docs/distribution.md), so the release pipeline must not acquire a build-time
# tool from it. Syft is instead fetched from a pinned Anchore GitHub release tarball and
# verified against a SHA-256 recorded in this file. That checksum is the trust anchor:
# a mismatch aborts the run rather than producing an SBOM from an unverified binary.
#
# One pin source: bump SYFT_VERSION and the four hashes together, and only together.
# Duplicating the pin across generators would let them drift apart, so both source it
# here. ADR 0042 already records the Syft pin as a maintenance obligation and warns
# that a stale pin is a loud failure.
#
# ---------------------------------------------------------------------------
# WHY THE MAPPING LIVES HERE
# ---------------------------------------------------------------------------
# artifact_sbom_name maps a published archive to its per-artifact SBOM name as
# <archive-stem>.cdx.json (strip .tar.gz / .zip / .exe, append .cdx.json).
# The generator and scripts/verify-release-checksums.sh share this one function
# so the two lists cannot drift apart: a rename on one side without the other
# would publish an SBOM the manifest does not carry, or vice versa.
#

# Guard against double-sourcing under `set -u`.
if [[ -n "${KYBER_WEAVE_SYFT_BOOTSTRAP_LOADED:-}" ]]; then
    return 0 2>/dev/null || exit 0
fi
KYBER_WEAVE_SYFT_BOOTSTRAP_LOADED=1

# ---------------------------------------------------------------------------
# Pinned Syft. The hashes are the sha256 column of
#   https://github.com/anchore/syft/releases/download/v1.55.0/syft_1.55.0_checksums.txt
# Bump SYFT_VERSION and the four hashes together, and only together.
# ---------------------------------------------------------------------------
SYFT_VERSION="1.55.0"
SYFT_BASE_URL="https://github.com/anchore/syft/releases/download/v${SYFT_VERSION}"
SYFT_SHA256_linux_amd64="bb65249542db5fedeeb4d3f9b018b75e7264dbfbbb10486f044581a92f90490d"
SYFT_SHA256_linux_arm64="929bd163db2d7cd2255d3bcb8967208079813d7b46b0c952f7b794d8e2c7d304"
SYFT_SHA256_darwin_amd64="e2e5831cbabaec9a36fe65c34755c1113982c842a685edce3ce0b54a0ded8003"
SYFT_SHA256_darwin_arm64="1217c258bd5eb91ef3189cf22a82e02f9cac07e4ce934350f428e93aff674625"

# Minimal diagnostics. The callers define these too with identical bodies;
# defining them here makes this file usable on its own when sourced first.
# They are only defined when missing so a caller with richer helpers keeps them.
if ! declare -F log >/dev/null 2>&1; then
    log()  { printf '%s\n' "$*"; }
fi
if ! declare -F warn >/dev/null 2>&1; then
    warn() { printf '::warning::%s\n' "$*" >&2; }
fi
if ! declare -F die >/dev/null 2>&1; then
    die()  { printf '::error::%s\n' "$*" >&2; exit 1; }
fi
# Progress messages that are emitted from inside a command substitution go to
# stderr: stdout there is the function's return value, not a message.
if ! declare -F progress >/dev/null 2>&1; then
    progress() { printf '%s\n' "$*" >&2; }
fi

# ---------------------------------------------------------------------------
# Per-artifact SBOM name mapping (issue #417, decision D1).
#
#   artifact_sbom_name <asset> -> <archive-stem>.cdx.json
#
# Strips exactly one archive suffix (.tar.gz, .zip, .exe) and appends
# .cdx.json, e.g. kyber-weave-linux-x64.tar.gz -> kyber-weave-linux-x64.cdx.json,
# kyberdash-tray-win-x64-setup.exe -> kyberdash-tray-win-x64-setup.cdx.json,
# kyber-squad-0.1.0.zip -> kyber-squad-0.1.0.cdx.json. The aggregate SBOM keeps
# its versioned name (kyber-weave-sbom-<version>.cdx.json) and never goes
# through this function. Basename first so callers may pass a path.
# ---------------------------------------------------------------------------
artifact_sbom_name() {
    local asset="$1"
    asset="$(basename "$asset")"
    case "$asset" in
        *.tar.gz) printf '%s.cdx.json\n' "${asset%.tar.gz}" ;;
        *.zip)    printf '%s.cdx.json\n' "${asset%.zip}" ;;
        *.exe)    printf '%s.cdx.json\n' "${asset%.exe}" ;;
        *)        printf '%s.cdx.json\n' "${asset%.*}" ;;
    esac
}

# ---------------------------------------------------------------------------
# Syft resolution: PATH, then cache, then download-and-verify. Fail closed.
# ---------------------------------------------------------------------------
detect_platform() {
    local os arch
    os="$(uname -s)"
    arch="$(uname -m)"
    case "$os" in
        Linux)  os="linux" ;;
        Darwin) os="darwin" ;;
        *)      die "unsupported OS for the Syft bootstrap: $os (linux and darwin are supported)" ;;
    esac
    case "$arch" in
        x86_64|amd64)  arch="amd64" ;;
        arm64|aarch64) arch="arm64" ;;
        *)             die "unsupported architecture for the Syft bootstrap: $arch" ;;
    esac
    printf '%s_%s\n' "$os" "$arch"
}

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print $1}'
    else
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

CACHE_ROOT="${KYBER_WEAVE_CACHE_DIR:-${XDG_CACHE_HOME:-${HOME}/.cache}/kyber-weave}"

syft_version_of() {
    "$1" version -o json 2>/dev/null \
        | python3 -c 'import json,sys; print(json.load(sys.stdin).get("version",""))' 2>/dev/null || true
}

resolve_syft() {
    local platform="$1" cache_dir="${CACHE_ROOT}/syft/${SYFT_VERSION}" candidate

    # An explicit override wins, but only if it is the pinned version. An unpinned
    # Syft would make the SBOM's provenance unprovable, which is the one thing
    # this bootstrap exists to guarantee.
    if [[ -n "${KYBER_WEAVE_SYFT:-}" ]]; then
        candidate="$KYBER_WEAVE_SYFT"
        [[ -x "$candidate" ]] || die "KYBER_WEAVE_SYFT is set but not executable: $candidate"
        [[ "$(syft_version_of "$candidate")" == "$SYFT_VERSION" ]] \
            || die "KYBER_WEAVE_SYFT is version $(syft_version_of "$candidate"), expected the pinned $SYFT_VERSION"
        printf '%s\n' "$candidate"
        return 0
    fi

    # A Syft already on PATH is used only when it is exactly the pinned version.
    if command -v syft >/dev/null 2>&1; then
        candidate="$(command -v syft)"
        if [[ "$(syft_version_of "$candidate")" == "$SYFT_VERSION" ]]; then
            progress "using Syft ${SYFT_VERSION} from PATH: ${candidate}"
            printf '%s\n' "$candidate"
            return 0
        fi
    fi

    # Cached download from a previous run. The cached *tarball* is re-verified on
    # every run rather than trusted because it is cached: a CI cache restored from
    # elsewhere is exactly the place a tampered artifact would arrive. The extracted
    # binary is only used once the tarball it came from has been checked again.
    local var_name="SYFT_SHA256_${platform}"
    local expected="${!var_name:-}"
    [[ -n "$expected" ]] || die "no pinned SHA-256 for platform ${platform}"

    local tarball="syft_${SYFT_VERSION}_${platform}.tar.gz"
    local cached_tarball="${cache_dir}/${tarball}"

    if [[ -f "$cached_tarball" ]]; then
        local cached_actual
        cached_actual="$(sha256_of "$cached_tarball")"
        if [[ "$cached_actual" == "$expected" ]]; then
            progress "using cached Syft ${SYFT_VERSION} (SHA-256 re-verified): ${cache_dir}/syft"
            if [[ ! -x "${cache_dir}/syft" ]]; then
                mkdir -p "$cache_dir"
                tar -xzf "$cached_tarball" -C "$cache_dir" syft
                chmod +x "${cache_dir}/syft"
            fi
            [[ -x "${cache_dir}/syft" ]] || die "the cached Syft tarball did not contain a 'syft' binary"
            printf '%s\n' "${cache_dir}/syft"
            return 0
        fi
        warn "cached ${tarball} failed its SHA-256 check (expected ${expected}, got ${cached_actual}); re-downloading"
        rm -f "$cached_tarball"
    fi

    # Download the pinned tarball and verify it before it is allowed to run.
    command -v curl >/dev/null 2>&1 || die "curl is required to download Syft"
    command -v tar  >/dev/null 2>&1 || die "tar is required to unpack Syft"

    local url="${SYFT_BASE_URL}/${tarball}"
    local work
    work="$(mktemp -d "${TMPDIR:-/tmp}/kw-syft-XXXXXX")"
    # An EXIT trap, not RETURN: `die` exits the shell and RETURN only fires when a
    # function returns, so a failed download or a hash mismatch would leak the
    # directory exactly on the failure path. This function runs in a command
    # substitution, so the trap is scoped to the subshell that runs it.
    # shellcheck disable=SC2064  # expand work now, not at trap time
    trap "rm -rf '${work}'" EXIT

    progress "downloading ${url}"
    curl -fsSL --retry 3 --retry-delay 2 -o "${work}/${tarball}" "$url" \
        || die "failed to download ${url}"

    local actual
    actual="$(sha256_of "${work}/${tarball}")"
    if [[ "$actual" != "$expected" ]]; then
        die "SHA-256 mismatch for ${tarball}: expected ${expected}, got ${actual}. Refusing to run an unverified Syft."
    fi
    progress "SHA-256 verified for ${tarball}"

    mkdir -p "$cache_dir"
    # Keep the verified tarball, not just the extracted binary, so that the next
    # run can re-verify what it is about to execute.
    cp "${work}/${tarball}" "${cached_tarball}"
    tar -xzf "${work}/${tarball}" -C "$cache_dir" syft
    chmod +x "${cache_dir}/syft"
    [[ -x "${cache_dir}/syft" ]] || die "the Syft tarball did not contain a 'syft' binary"

    printf '%s\n' "${cache_dir}/syft"
}
