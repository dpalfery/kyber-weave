#!/usr/bin/env bash
#
# generate-sbom.sh — build the aggregate CycloneDX SBOM for a Kyber-Weave release.
#
# Usage: scripts/generate-sbom.sh [--no-dedupe] [--allow-partial] <out-dir> <version>
#
# Writes <out-dir>/kyber-weave-sbom-<version>.cdx.json: one CycloneDX JSON document
# (bomFormat "CycloneDX", specVersion 1.6) describing every third-party component the
# released tree depends on, across the three ecosystems the repository ships.
#
# ---------------------------------------------------------------------------
# WHY SYFT, AND WHY IT IS PINNED BY SHA-256
# ---------------------------------------------------------------------------
# The obvious .NET answer is `dotnet tool install CycloneDX` and let it emit the SBOM.
# That is not available here: nuget.org is never used as a distribution channel in this
# repository (docs/distribution.md), so the release pipeline must not acquire a build-time
# tool from it. Syft is instead fetched from a pinned Anchore GitHub release tarball and
# verified against a SHA-256 recorded in this script. That checksum is the trust anchor:
# a mismatch aborts the run rather than producing an SBOM from an unverified binary.
#
# ---------------------------------------------------------------------------
# WHAT IS SCANNED
# ---------------------------------------------------------------------------
# One Syft pass over the repository root, plus — when the .NET SDK is present — a
# throwaway framework-dependent publish staged inside the tree so that *.deps.json
# exists for the .NET cataloger to read. The staged publish is deliberately NOT
# single-file and NOT self-contained: a single-file publish folds the dependency
# closure into the executable and emits no readable deps.json, and a self-contained
# one adds the runtime packs as components of their own.
#
#   npm     dash/package-lock.json, dash/web/package-lock.json,
#           dash/tray/ui/package-lock.json, npm/package-lock.json
#   cargo   dash/tray/src-tauri/Cargo.lock
#   nuget   src/KyberWeave.Cli/packages.lock.json, src/KyberWeave.Core/packages.lock.json,
#           and — when the SDK is available — the staged *.deps.json of
#           KyberWeave.Cli and KyberWeave.Mcp
#
# TWO THINGS ABOUT THE .NET CATALOGER THAT ARE NOT OBVIOUS
#
# Syft's default .NET cataloger for deps.json is dotnet-deps-binary-cataloger. The
# older dotnet-deps-cataloger is deprecated, is NOT enabled by default, and will be
# removed in Syft v2 — so this script does not select it. The replacement reads
# **/*.deps.json directly and does not need the DLLs to exist on disk, which is why
# only the deps.json files are copied into the scanned tree.
#
# That cataloger does require the deps.json to CLAIM a DLL: its default
# dotnet.dep-packages-must-claim-dll setting drops any package that has no
# runtime/resource/compile/native asset section, directly or through a child. A
# real `dotnet publish` emits those sections, so the staged file qualifies — but a
# hand-written or trimmed deps.json would catalog as nothing at all, silently.
#
# ---------------------------------------------------------------------------
# WHAT IS DELIBERATELY NOT SCANNED
# ---------------------------------------------------------------------------
# .github/ is excluded. Syft's GitHub Actions cataloger would otherwise list CI actions
# (actions/checkout@v7 and friends) as components of the release; they are build-time
# tooling, not shipped code, and their presence in a published SBOM is misleading.
#
# node_modules/, obj/, bin/ and target/ are excluded for the same reason Syft excludes
# them by default in part: they are build output, so what they contain depends on who
# ran the build. The lockfiles in the tree are the authoritative inputs.
#
# ---------------------------------------------------------------------------
# DEGRADATION, STATED RATHER THAN HIDDEN
# ---------------------------------------------------------------------------
# Without a .NET SDK the publish step is skipped with a warning and the SBOM falls back
# to the checked-in packages.lock.json files. That fallback under-reports .NET:
# src/KyberWeave.Mcp has no packages.lock.json, so its NuGet dependencies
# (ModelContextProtocol, ModelContextProtocol.Core, Microsoft.Extensions.Hosting and
# their transitives) are absent from the SBOM.
#
# That fallback is a LOCAL-DEVELOPMENT path, not release-grade coverage. The release
# and CI jobs always have the .NET SDK, so what is published is the full closure — on
# this machine the staged publish recovers 32 NuGet packages the lock files miss.
# The script says so loudly rather than emitting a quietly incomplete document, and
# the nuget ecosystem check drops to a warning for the same reason.
#
# The npm and cargo ecosystems do not degrade: Syft parses package-lock.json and
# Cargo.lock directly, so neither needs its toolchain installed to be cataloged.
#

set -euo pipefail

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

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

DEDUPE=1
ALLOW_PARTIAL=0

log()  { printf '%s\n' "$*"; }
warn() { printf '::warning::%s\n' "$*" >&2; }
die()  { printf '::error::%s\n' "$*" >&2; exit 1; }

# Progress messages that are emitted from inside a command substitution go to
# stderr: stdout there is the function's return value, not a message.
progress() { printf '%s\n' "$*" >&2; }

usage() {
    cat >&2 <<EOF
Usage: $(basename "$0") [--no-dedupe] [--allow-partial] <out-dir> <version>

  <out-dir>   directory to write kyber-weave-sbom-<version>.cdx.json into (created if absent)
  <version>   release version, e.g. 0.1.7

  --no-dedupe     keep Syft's raw component list, including one entry per lockfile
                  that names the same package (default: collapse to one per purl)
  --allow-partial downgrade every ecosystem check from an error to a warning
EOF
}

# ---------------------------------------------------------------------------
# Arguments
# ---------------------------------------------------------------------------
while [[ $# -gt 0 ]]; do
    case "$1" in
        --no-dedupe)    DEDUPE=0; shift ;;
        --allow-partial) ALLOW_PARTIAL=1; shift ;;
        -h|--help)      usage; exit 0 ;;
        --)             shift; break ;;
        -*)             usage; die "unknown option: $1" ;;
        *)              break ;;
    esac
done

[[ $# -eq 2 ]] || { usage; die "expected <out-dir> and <version>"; }
OUT_DIR="$1"
VERSION="$2"
[[ -n "$OUT_DIR" ]] || die "<out-dir> must not be empty"
[[ -n "$VERSION" ]] || die "<version> must not be empty"
VERSION="${VERSION#v}"

command -v python3 >/dev/null 2>&1 || die "python3 is required (it validates the generated SBOM)"

mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"
OUT_FILE="${OUT_DIR}/kyber-weave-sbom-${VERSION}.cdx.json"

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
    # this script exists to guarantee.
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
    # shellcheck disable=SC2064  # expand work now, not at trap time
    trap "rm -rf '${work}'" RETURN

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

PLATFORM="$(detect_platform)"
SYFT_BIN="$(resolve_syft "$PLATFORM")"

# ---------------------------------------------------------------------------
# .NET staging.
#
# The intended CI behaviour is a throwaway, framework-dependent, non-single-file
# publish of KyberWeave.Cli and KyberWeave.Mcp into a directory inside the tree, so
# that Syft finds *.deps.json and can catalog the full NuGet closure — including
# src/KyberWeave.Mcp, which has no packages.lock.json and is therefore invisible to
# a lockfile-only scan.
#
# Without the SDK the step is skipped, loudly, and the SBOM falls back to the
# checked-in packages.lock.json files.
# ---------------------------------------------------------------------------
STAGING_DIR=""
PUBLISH_DIR=""
DOTNET_COVERAGE="lockfile-only"

# First-party assemblies and projects. These are what this repository builds, not
# what it depends on, so they must not appear in the component list: the root
# component already represents the product. The deps.json roots are appended to
# this list once the publish has told us what they are called.
FIRST_PARTY_NAMES="kyberweave.core kyberweave.cli kyberweave.mcp"
cleanup() {
    if [[ -n "$STAGING_DIR" && -d "$STAGING_DIR" ]]; then
        rm -rf "$STAGING_DIR"
    fi
    if [[ -n "$PUBLISH_DIR" && -d "$PUBLISH_DIR" ]]; then
        rm -rf "$PUBLISH_DIR"
    fi
}
trap cleanup EXIT

stage_dotnet() {
    if ! command -v dotnet >/dev/null 2>&1; then
        warn "no .NET SDK on PATH: skipping the staged publish."
        warn "The SBOM falls back to the checked-in packages.lock.json files."
        warn "src/KyberWeave.Mcp has NO packages.lock.json, so its NuGet dependencies"
        warn "(ModelContextProtocol, Microsoft.Extensions.Hosting and their transitives)"
        warn "are MISSING from this SBOM. .NET coverage here is partial."
        return 0
    fi

    # The publish happens OUTSIDE the scanned tree, and only the *.deps.json files
    # are copied into it. That is deliberate on two counts:
    #
    #   * A publish directory is full of PE binaries. Syft's .NET binary cataloger
    #     turns every DLL it cannot pair with a claimed runtime path into a
    #     "dotnet binary" component, which would add a bogus NuGet package per
    #     shipped DLL — including one for our own assemblies. The deps.json alone
    #     carries the whole dependency closure and is all that is wanted.
    #   * A deps.json on its own is catalogued correctly: Syft's default
    #     dotnet-deps-binary-cataloger reads **/*.deps.json directly and does not
    #     require the DLLs to be present on disk.
    PUBLISH_DIR="$(mktemp -d "${TMPDIR:-/tmp}/kw-sbom-publish-XXXXXX")"
    STAGING_DIR="${REPO_ROOT}/.sbom-staging"
    rm -rf "$STAGING_DIR"
    mkdir -p "$STAGING_DIR"

    # A restore can rewrite the tracked lock files (release-local.sh documents this
    # for RID-specific restores). Snapshot and restore them so a SBOM run leaves the
    # working tree exactly as it found it — and so the scan sees the committed
    # lock files, not a rewritten variant.
    local lock_backup lock_files
    lock_backup="$(mktemp -d "${TMPDIR:-/tmp}/kw-sbom-locks-XXXXXX")"
    lock_files="$(git -C "$REPO_ROOT" ls-files '*packages.lock.json' 2>/dev/null || true)"
    local lock
    for lock in $lock_files; do
        mkdir -p "${lock_backup}/$(dirname "$lock")"
        cp "${REPO_ROOT}/${lock}" "${lock_backup}/${lock}"
    done
    restore_locks() {
        for lock in $lock_files; do
            [[ -f "${lock_backup}/${lock}" ]] && cp "${lock_backup}/${lock}" "${REPO_ROOT}/${lock}"
        done
        rm -rf "$lock_backup"
    }

    local project
    for project in KyberWeave.Cli KyberWeave.Mcp; do
        log "staging ${project} for .NET cataloging (framework-dependent, non-single-file)"
        # Framework-dependent and non-single-file on purpose: a single-file publish
        # folds the dependency closure into the executable and emits no readable
        # deps.json, and a self-contained one adds the runtime packs as packages.
        dotnet publish "${REPO_ROOT}/src/${project}/${project}.csproj" \
            -c Release \
            --self-contained false \
            -p:PublishSingleFile=false \
            -p:DebugType=None \
            -p:DebugSymbols=false \
            -p:Version="${VERSION}" \
            -o "${PUBLISH_DIR}/${project}" \
            --nologo -v quiet \
            || { restore_locks; die "dotnet publish failed for ${project}"; }
    done

    restore_locks

    local deps
    for project in KyberWeave.Cli KyberWeave.Mcp; do
        mkdir -p "${STAGING_DIR}/${project}"
        find "${PUBLISH_DIR}/${project}" -maxdepth 1 -name '*.deps.json' -exec cp {} "${STAGING_DIR}/${project}/" \;
    done
    deps="$(find "$STAGING_DIR" -name '*.deps.json' | wc -l | tr -d ' ')"
    if [[ "$deps" -eq 0 ]]; then
        die "the staged publish produced no *.deps.json; Syft would have nothing to catalog"
    fi

    # The root project of a deps.json is emitted by Syft as a component of its own:
    # the deps.json is named after the assembly, and the assembly is the root
    # package. Those are the products themselves, not third-party dependencies, so
    # their names are collected here and dropped from the component list.
    local root
    for root in "${PUBLISH_DIR}"/*/*.deps.json; do
        FIRST_PARTY_NAMES="${FIRST_PARTY_NAMES} $(basename "$root" .deps.json)"
    done

    log "staged ${deps} deps.json file(s) under ${STAGING_DIR}"
    DOTNET_COVERAGE="full"
}

stage_dotnet

# ---------------------------------------------------------------------------
# Scan
# ---------------------------------------------------------------------------
RAW_FILE="$(mktemp "${TMPDIR:-/tmp}/kw-sbom-raw-XXXXXX.json")"
# shellcheck disable=SC2064  # expand now, not at trap time
trap "rm -f '${RAW_FILE}'; cleanup" EXIT

log "scanning ${REPO_ROOT} with Syft ${SYFT_VERSION}"

# Excludes are relative to the scan root. The './' and '**/' prefixes matter: a
# bare path fragment is not a glob and silently excludes nothing (or, worse,
# everything). Each one below was checked against Syft's matcher.
EXCLUDES=(
    "./.git"
    "./.github"
    "./.codegraph"
    "./artifacts"
    "./.local-release"
    "**/node_modules/**"
    "**/obj/**"
    "**/bin/**"
    "**/target/**"
    "**/dist/**"
)

# If the output directory lives inside the scanned tree, keep a previous run's
# SBOM out of this run's scan.
case "$OUT_DIR/" in
    "${REPO_ROOT}/"*) EXCLUDES+=("./${OUT_DIR#"${REPO_ROOT}/"}") ;;
esac

SYFT_ARGS=()
for pattern in "${EXCLUDES[@]}"; do
    SYFT_ARGS+=(--exclude "$pattern")
done

"$SYFT_BIN" scan "dir:${REPO_ROOT}" \
    "${SYFT_ARGS[@]}" \
    --source-name "kyber-weave" \
    --source-version "${VERSION}" \
    -o "cyclonedx-json@1.6=${RAW_FILE}" \
    || die "Syft scan failed"

[[ -s "$RAW_FILE" ]] || die "Syft produced an empty SBOM"

# ---------------------------------------------------------------------------
# Normalise, validate, and summarise. One Python pass so the normalisation and the
# validation see the same document.
# ---------------------------------------------------------------------------
python3 - "${RAW_FILE}" "${OUT_FILE}" "${REPO_ROOT}" "${VERSION}" "${DEDUPE}" \
         "${DOTNET_COVERAGE}" "${ALLOW_PARTIAL}" "${SYFT_VERSION}" "${FIRST_PARTY_NAMES}" <<'PY'
import json
import os
import sys
from collections import Counter, OrderedDict

raw_path, out_path, repo_root, version, dedupe, coverage, allow_partial, syft_version, first_party = sys.argv[1:10]
dedupe = dedupe == "1"
allow_partial = allow_partial == "1"

errors = []
warnings = []

with open(raw_path, encoding="utf-8") as handle:
    bom = json.load(handle)


def fail(message):
    errors.append(message)


def warn(message):
    warnings.append(message)


# --- the shape CycloneDX requires -------------------------------------------------
if bom.get("bomFormat") != "CycloneDX":
    fail("bomFormat is %r, expected 'CycloneDX'" % bom.get("bomFormat"))
if bom.get("specVersion") != "1.6":
    fail("specVersion is %r, expected '1.6'" % bom.get("specVersion"))
if not isinstance(bom.get("version"), int) or bom.get("version", 0) < 1:
    fail("version is %r, expected a positive integer" % bom.get("version"))

components = bom.get("components")
if not isinstance(components, list) or not components:
    fail("the SBOM has no components")
    components = components if isinstance(components, list) else []

# --- scrub the build machine's absolute paths out of the document ----------------
# Syft records file-type components under their absolute path, which would publish
# the build machine's directory layout into a released artifact.
prefix = os.path.abspath(repo_root) + os.sep
for component in components:
    name = component.get("name")
    if isinstance(name, str) and name.startswith(prefix):
        component["name"] = name[len(prefix):]
    for prop in component.get("properties", []) or []:
        value = prop.get("value")
        if isinstance(value, str) and value.startswith(prefix):
            prop["value"] = value[len(prefix):]

metadata = bom.setdefault("metadata", {})
root = metadata.setdefault("component", {})
# It is replaced with a stable, version-scoped ref; the old value is kept so the
# dependency graph can be remapped if Syft ever referenced it.
old_root_ref = root.get("bom-ref")
root["type"] = "application"
root["name"] = "kyber-weave"
root["version"] = version
root["bom-ref"] = "kyber-weave@%s" % version
root["purl"] = "pkg:generic/kyber-weave@%s" % version

metadata.setdefault("properties", [])
metadata["properties"] = [
    prop for prop in metadata["properties"]
    if not str(prop.get("name", "")).startswith("kyber-weave:sbom:")
]
metadata["properties"].extend([
    {"name": "kyber-weave:sbom:generator", "value": "anchore/syft@%s" % syft_version},
    {"name": "kyber-weave:sbom:spec", "value": "CycloneDX 1.6"},
    {"name": "kyber-weave:sbom:version", "value": version},
    {"name": "kyber-weave:sbom:dotnet-coverage", "value": coverage},
    {"name": "kyber-weave:sbom:deduplicated", "value": "true" if dedupe else "false"},
])

# --- drop first-party components ---------------------------------------------------
# Two shapes of first-party component reach the component list, and neither is a
# third-party dependency:
#
#   * A packages.lock.json records a project reference ("type": "Project") as if it
#     were a package, and Syft's packages-lock cataloger emits it as a NuGet
#     component with no resolved version.
#   * The root project of a deps.json is the assembly the deps.json is named
#     after, and Syft's deps cataloger emits it as a component too.
#
# Both are this repository's own code. The root component already represents the
# product, so listing them again would claim the release depends on itself. The
# names are supplied by the caller: the project names are known here, and the
# deps.json roots are discovered from what the publish produced.
first_party_names = {name.strip().lower() for name in first_party.split() if name.strip()}
first_party = []
for component in components:
    purl = component.get("purl") or ""
    if not purl.startswith("pkg:nuget/"):
        continue
    name = (component.get("name") or "").lower()
    if name in first_party_names:
        first_party.append(component)
    elif "@" not in purl:
        # A NuGet component with no version at all is a project reference from a
        # packages.lock.json, which never carries a resolved version.
        first_party.append(component)
if first_party:
    dropped_refs = {component.get("bom-ref") for component in first_party}
    components = [c for c in components if id(c) not in {id(f) for f in first_party}]
    bom["components"] = components
    # Prune the dropped components out of the dependency graph too, so the graph
    # does not end up pointing at something that is no longer in the document.
    pruned = []
    for entry in bom.get("dependencies") or []:
        if entry.get("ref") in dropped_refs:
            continue
        entry["dependsOn"] = [t for t in (entry.get("dependsOn") or []) if t not in dropped_refs]
        pruned.append(entry)
    bom["dependencies"] = pruned
    print("dropped %d first-party component(s) presented as NuGet packages: %s"
          % (len(first_party), ", ".join(sorted(c.get("name", "?") for c in first_party))))

# --- collapse duplicate packages --------------------------------------------------
# Syft emits one component per lock file that names a package, so a package reachable
# from two lock files appears twice under two bom-refs. For a published SBOM that is
# noise: a consumer counting components should count packages. Collapsing is done
# here rather than in Syft because Syft has no such option, and the dependency graph
# has to be remapped onto the surviving bom-refs.
raw_count = len(components)
if dedupe:
    canonical_by_purl = OrderedDict()
    remap = {}
    if old_root_ref:
        remap[old_root_ref] = root["bom-ref"]
    survivors = []
    for component in components:
        purl = component.get("purl")
        ref = component.get("bom-ref")
        if not purl or ref is None:
            survivors.append(component)
            continue
        if purl not in canonical_by_purl:
            canonical_by_purl[purl] = component
            survivors.append(component)
            continue
        kept = canonical_by_purl[purl]
        remap[ref] = kept.get("bom-ref")
        # Keep the evidence of every lock file that names this package.
        locations = [prop for prop in kept.get("properties", []) or []
                     if str(prop.get("name", "")).startswith("syft:location:")]
        index = len(locations)
        for prop in component.get("properties", []) or []:
            if not str(prop.get("name", "")).startswith("syft:location:"):
                continue
            index += 1
            locations.append({
                "name": "syft:location:%d:%s" % (index, prop.get("name", "").rsplit(":", 1)[-1]),
                "value": prop.get("value"),
            })
        if locations:
            kept["properties"] = [prop for prop in (kept.get("properties", []) or [])
                                   if not str(prop.get("name", "")).startswith("syft:location:")] + locations
    components = survivors
    bom["components"] = components

    # Remapping can make two dependency entries land on the same ref: a package
    # named by two lock files collapses to one component, and both original
    # entries then point at it. CycloneDX requires the dependencies array to hold
    # unique entries, so entries that share a ref are merged into one, taking the
    # union of what they depended on.
    grouped = OrderedDict()
    for entry in bom.get("dependencies") or []:
        new_ref = remap.get(entry.get("ref"), entry.get("ref"))
        bucket = grouped.setdefault(new_ref, [])
        for target in entry.get("dependsOn", []) or []:
            mapped = remap.get(target, target)
            if mapped != new_ref and mapped not in bucket:
                bucket.append(mapped)
    rewritten = []
    for new_ref, bucket in grouped.items():
        item = {"ref": new_ref}
        if bucket:
            item["dependsOn"] = bucket
        rewritten.append(item)
    bom["dependencies"] = rewritten

# --- the dependency graph must still resolve --------------------------------------
known_refs = {component.get("bom-ref") for component in components}
known_refs.add(root.get("bom-ref"))
if old_root_ref:
    known_refs.add(old_root_ref)
seen_refs = set()
for entry in bom.get("dependencies", []) or []:
    if entry.get("ref") not in known_refs:
        fail("dependency graph references an unknown component: %r" % entry.get("ref"))
    # CycloneDX requires the dependencies array to hold unique entries.
    if entry.get("ref") in seen_refs:
        fail("dependency graph has more than one entry for %r" % entry.get("ref"))
    seen_refs.add(entry.get("ref"))
    for target in entry.get("dependsOn", []) or []:
        if target not in known_refs:
            fail("dependency graph references an unknown component: %r" % target)
        if target == entry.get("ref"):
            fail("dependency graph entry %r depends on itself" % target)

# --- ecosystem coverage ------------------------------------------------------------
def purl_type(component):
    purl = component.get("purl")
    if not purl:
        return None
    return purl.split("/", 1)[0]


counts = Counter(purl_type(component) for component in components)
by_purl = Counter(component.get("purl") for component in components if component.get("purl"))

ecosystems = [
    ("npm", "pkg:npm", True,
     "package-lock.json (dash/, dash/web/, dash/tray/ui/, npm/)"),
    ("cargo", "pkg:cargo", True,
     "dash/tray/src-tauri/Cargo.lock"),
    ("nuget", "pkg:nuget", coverage == "full",
     "packages.lock.json + staged *.deps.json" if coverage == "full"
     else "packages.lock.json only (no .NET SDK available)"),
]

for name, prefix_key, required, source in ecosystems:
    found = counts.get(prefix_key, 0)
    if found == 0:
        message = "no %s components found (source: %s)" % (name, source)
        if required and not allow_partial:
            fail(message)
        else:
            warn(message)
    else:
        log_line = "  %-6s %4d component(s)  <- %s" % (name, found, source)
        if not required:
            log_line += "  [partial coverage: warning only]"
        print(log_line)

if counts.get(None, 0):
    print("  %-6s %4d component(s)  <- no purl (lock files themselves, and crates Syft "
          "cannot name)" % ("(none)", counts.get(None, 0)))

print("")
if dedupe:
    print("components: %d as catalogued, %d after collapsing duplicate packages"
          % (raw_count, len(components)))
else:
    print("components: %d as catalogued (duplicate packages left in place)" % len(components))
for key, count in sorted(counts.items(), key=lambda item: (-item[1], str(item[0]))):
    print("  %-10s %d" % (key or "(no purl)", count))
if by_purl:
    print("distinct package purls: %d" % len(by_purl))

if warnings:
    print("")
    for message in warnings:
        print("::warning::%s" % message)

# --- write it out ------------------------------------------------------------------
# Nothing is written until every check has passed. A failed run must not leave an
# SBOM on disk for a later step to pick up and publish.
if errors:
    print("")
    for message in errors:
        print("::error::%s" % message)
    print("")
    print("SBOM not written: %s" % out_path)
    sys.exit(1)

with open(out_path, "w", encoding="utf-8") as handle:
    json.dump(bom, handle, indent=2, sort_keys=False)
    handle.write("\n")

print("")
print("SBOM written: %s" % out_path)
PY

# ---------------------------------------------------------------------------
# Schema validation — the last gate.
#
# The Python pass above checks the shape of the document this script produced. That
# is not the same as checking it against the CycloneDX schema, and the difference
# is not academic: during development, a document that satisfied every hand-written
# check was still rejected by the schema (the schema requires the `dependencies`
# array to hold unique entries). So the generated file is validated against the
# vendored CycloneDX 1.6 schema before it is accepted.
#
# A failure here removes the file: a rejected SBOM must not be left on disk for a
# later step to publish.
# ---------------------------------------------------------------------------
VALIDATOR="${REPO_ROOT}/scripts/validate-sbom.py"
[[ -f "$VALIDATOR" ]] || die "the SBOM validator is missing: ${VALIDATOR}"

VALIDATOR_ARGS=(--require-ecosystems npm,cargo,nuget)
if [[ "$DOTNET_COVERAGE" != "full" ]]; then
    # Without the .NET SDK the nuget coverage is knowingly partial, so a missing
    # nuget ecosystem is a warning rather than an error. npm and cargo are not
    # affected: Syft reads those lock files directly, so their absence means the
    # scan did not do what it was asked to do.
    VALIDATOR_ARGS+=(--optional-ecosystems nuget)
fi

if ! python3 "$VALIDATOR" "${OUT_FILE}" "${VALIDATOR_ARGS[@]}"; then
    rm -f "${OUT_FILE}"
    die "the generated SBOM failed CycloneDX schema validation and was removed"
fi

log ""
log "SBOM: ${OUT_FILE}"
