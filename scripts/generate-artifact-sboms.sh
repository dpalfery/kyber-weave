#!/usr/bin/env bash
#
# generate-artifact-sboms.sh — one CycloneDX SBOM per published archive.
#
# Usage: scripts/generate-artifact-sboms.sh <assets-dir> <version>
#
# For each *.tar.gz / *.zip / *.exe in <assets-dir>, unpacks the archive into a
# throwaway directory, scans the extracted tree with the pinned Syft, normalises
# the document, validates it against the vendored CycloneDX 1.6 schema, and writes
# <archive-stem>.cdx.json next to the archive (see artifact_sbom_name in
# scripts/syft-bootstrap.sh). The aggregate SBOM keeps its versioned name and is
# built by scripts/generate-sbom.sh instead.
#
# ---------------------------------------------------------------------------
# WHY PINNED SYFT, SHARED FROM ONE SOURCE
# ---------------------------------------------------------------------------
# Same trust anchor as the aggregate generator: nuget.org is never a distribution
# channel here (docs/distribution.md), so the pipeline fetches Syft from a pinned
# Anchore release tarball verified by SHA-256. The pin lives in
# scripts/syft-bootstrap.sh, sourced by both generators — one place to bump it,
# so the two can never disagree on what "the SBOM tool" was.
#
# ---------------------------------------------------------------------------
# WHAT IS SCANNED
# ---------------------------------------------------------------------------
# Each archive is unpacked into a throwaway directory and the extracted tree is
# scanned (dir: source). The scan runs with the file cataloger enabled
# (SYFT_FILE_METADATA_SELECTION=all) so every shipped byte is listed as a file
# component, even when no lockfile or binary cataloger fires. A tiny archive with
# no packages still yields a non-empty, schema-valid document rather than an
# empty one a later step might publish.
#
# ---------------------------------------------------------------------------
# FILE CATALOGUE IS PROVENANCE, NOT RUNTIME COVERAGE
# ---------------------------------------------------------------------------
# The file list proves which bytes shipped. It does not prove which runtime
# dependencies those bytes contain: a single-file .NET publish folds its closure
# into the executable, and a Node SEA blob bundles the runtime and the dashboard,
# so neither leaves anything for a package cataloger to read. Those archives get
# a kyber-weave:sbom:not-measured marker saying so, loudly, rather than a quietly
# incomplete package list. A Node SEA SBOM therefore carries BOTH the file
# catalogue entry AND the not-measured marker (decision D4): the bytes are listed,
# and the document states that runtime coverage was not measurable.
#
# ---------------------------------------------------------------------------
# WHAT IS DELIBERATELY NOT REQUIRED
# ---------------------------------------------------------------------------
# Unlike the aggregate generator, per-artifact validation does NOT require
# npm/cargo/nuget ecosystems. An extracted archive rarely contains lockfiles, so
# demanding ecosystems would fail every archive that ships only binaries. The
# vendored schema, the non-empty component list, and the resolvable dependency
# graph are still enforced: a silent or truncated scan fails here, not at publish.
#

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# shellcheck source=syft-bootstrap.sh
source "${SCRIPT_DIR}/syft-bootstrap.sh"

usage() {
    cat >&2 <<EOF
Usage: $(basename "$0") <assets-dir> <version>

  <assets-dir>  directory holding the published archives (*.tar.gz/*.zip/*.exe)
  <version>     release version, e.g. 0.1.7
EOF
}

[[ $# -eq 2 ]] || { usage; die "expected <assets-dir> and <version>"; }
ASSETS_DIR="$1"
VERSION="$2"
[[ -n "$ASSETS_DIR" ]] || die "<assets-dir> must not be empty"
[[ -n "$VERSION" ]] || die "<version> must not be empty"
VERSION="${VERSION#v}"

command -v python3 >/dev/null 2>&1 || die "python3 is required (it normalises the generated SBOMs)"

[[ -d "$ASSETS_DIR" ]] || die "assets directory not found: ${ASSETS_DIR}"
ASSETS_DIR="$(cd "$ASSETS_DIR" && pwd)"

VALIDATOR="${REPO_ROOT}/scripts/validate-sbom.py"
[[ -f "$VALIDATOR" ]] || die "the SBOM validator is missing: ${VALIDATOR}"

PLATFORM="$(detect_platform)"
SYFT_BIN="$(resolve_syft "$PLATFORM")"

# One throwaway parent for every extraction; a single EXIT trap cleans them all
# even when a later archive fails after earlier ones succeeded.
WORK_PARENT="$(mktemp -d "${TMPDIR:-/tmp}/kw-artifact-sboms-XXXXXX")"
trap 'rm -rf -- "$WORK_PARENT"' EXIT

log "scanning archives in ${ASSETS_DIR} with Syft ${SYFT_VERSION}"

shopt -s nullglob

archives=("${ASSETS_DIR}"/*.tar.gz "${ASSETS_DIR}"/*.zip "${ASSETS_DIR}"/*.exe)
[[ "${#archives[@]}" -gt 0 ]] || die "no publishable archives (*.tar.gz/*.zip/*.exe) in ${ASSETS_DIR}"

# ---------------------------------------------------------------------------
# BOUNDED PARALLEL SCANS (plan D8)
# ---------------------------------------------------------------------------
# WHY parallel, and why bounded: Syft scans are independent per archive, so a
# sequential loop leaves cores idle while 20 archives scan one by one. Each
# scan is its own Syft process over its own throwaway extraction directory, so
# workers share nothing but the read-only Syft binary and the assets directory.
# WHY the ceiling: one Syft holds the extracted tree plus its catalogers in
# memory; the cap below (min(ncpu, 4), override via KW_ARTIFACT_SBOM_JOBS)
# keeps peak RSS bounded on ubuntu-latest runners and laptops alike — measured
# ceiling note: 4 parallel scans is the default maximum, higher values are
# opt-in per run, never per commit. Waves re-sync every $JOBS jobs so the pool
# stays portable to bash 3.2 (macOS has no `wait -n`).
# WHY deterministic output is preserved: each worker redirects its own stdout
# to ${WORK_PARENT}/status/<archive>.log, and the parent replays those logs in
# sorted filename order after every worker finishes. Per-archive SBOM bytes are
# untouched by scheduling (same normaliser, same validator, same file names),
# so only the completion order varies — never the artifacts.
STATUS_DIR="${WORK_PARENT}/status"
mkdir -p "$STATUS_DIR" || die "failed to create scan status directory"

JOBS="${KW_ARTIFACT_SBOM_JOBS:-}"
if [[ -z "$JOBS" ]]; then
    if command -v nproc >/dev/null 2>&1; then
        JOBS="$(nproc)"
    elif command -v sysctl >/dev/null 2>&1; then
        JOBS="$(sysctl -n hw.ncpu 2>/dev/null || echo 4)"
    else
        JOBS=4
    fi
fi
case "$JOBS" in
    ''|*[!0-9]*) JOBS=4 ;;
esac
[[ "$JOBS" -ge 1 ]] 2>/dev/null || JOBS=1
[[ "$JOBS" -le 4 ]] 2>/dev/null || JOBS=4
[[ "$JOBS" -le "${#archives[@]}" ]] 2>/dev/null || JOBS="${#archives[@]}"

process_archive() {
    local archive="$1"
    local archive_base sbom_name stem out_file extract_dir raw_file tar_list zip_list
    archive_base="$(basename "$archive")"
    sbom_name="$(artifact_sbom_name "$archive_base")"
    stem="${sbom_name%.cdx.json}"
    out_file="${ASSETS_DIR}/${sbom_name}"

    log "artifact: ${archive_base} -> ${sbom_name}"

    extract_dir="$(mktemp -d "${WORK_PARENT}/extract-XXXXXX")" \
        || die "failed to create extraction directory for ${archive_base}"

    case "$archive_base" in
        *.tar.gz)
            command -v tar >/dev/null 2>&1 || die "tar is required to unpack ${archive_base}"
            # WHY pre-list: tar extracts absolute paths and ../ entries outside
            # -C, and symlinks inside the tree would let a later scan follow
            # build-machine paths. Own-build archives make this low-likelihood,
            # but a traversal fixture must still fail closed here, not at publish.
            tar_list="$(tar -tzf "$archive")" \
                || die "failed to list ${archive_base}"
            if printf '%s\n' "$tar_list" | grep -Eq '(^/)|(^|/)\.\.(/|$)'; then
                die "refusing to unpack ${archive_base}: archive contains absolute or parent-directory entries"
            fi
            tar -xzf "$archive" -C "$extract_dir" \
                || die "failed to unpack ${archive_base}"
            if [[ -n "$(find "$extract_dir" -type l -print -quit)" ]]; then
                die "refusing to scan ${archive_base}: archive contains symlinks"
            fi
            ;;
        *.zip)
            command -v unzip >/dev/null 2>&1 || die "unzip is required to unpack ${archive_base}"
            # WHY -Z1: one filename per line, portable across Info-ZIP on Linux
            # and macOS, so the same absolute/.. check applies as for tar.
            zip_list="$(unzip -Z1 "$archive")" \
                || die "failed to list ${archive_base}"
            if printf '%s\n' "$zip_list" | grep -Eq '(^/)|(^|/)\.\.(/|$)'; then
                die "refusing to unpack ${archive_base}: archive contains absolute or parent-directory entries"
            fi
            unzip -q "$archive" -d "$extract_dir" \
                || die "failed to unpack ${archive_base}"
            if [[ -n "$(find "$extract_dir" -type l -print -quit)" ]]; then
                die "refusing to scan ${archive_base}: archive contains symlinks"
            fi
            ;;
        *.exe)
            # A Windows installer is opaque to tar/unzip: stage the file itself
            # so the file cataloger still records its bytes as provenance.
            cp "$archive" "${extract_dir}/" \
                || die "failed to stage ${archive_base}"
            ;;
    esac

    # BSD mktemp only replaces a trailing run of Xs, so the template must end
    # with them: a `.json` suffix after the Xs works on GNU but creates one
    # literal file on macOS, which parallel workers would then race over. The suffix
    # is cosmetic (Syft writes JSON by -o mapping, python reads it back), so
    # the unique path carries no extension.
    # WHY under WORK_PARENT: the EXIT trap cleans only WORK_PARENT, so a raw
    # file under /tmp would leak when the normaliser exits before its explicit
    # rm (set -e skips it). Inside WORK_PARENT the trap reclaims it.
    raw_file="$(mktemp "${WORK_PARENT}/kw-artifact-raw-XXXXXX")"

    # The file cataloger is the provenance net: with selection=all every shipped
    # byte becomes a file component, so even an archive with no lockfiles or
    # recognizable binaries yields a non-empty document.
    SYFT_FILE_METADATA_SELECTION=all "$SYFT_BIN" scan "dir:${extract_dir}" \
        --source-name "$stem" \
        --source-version "${VERSION}" \
        -o "cyclonedx-json@1.6=${raw_file}" \
        || { rm -f "$raw_file"; die "Syft scan failed for ${archive_base}"; }

    [[ -s "$raw_file" ]] || { rm -f "$raw_file"; die "Syft produced an empty SBOM for ${archive_base}"; }

    python3 - "$raw_file" "$out_file" "$extract_dir" "$stem" "$archive_base" \
              "$VERSION" "$SYFT_VERSION" <<'PY'
import hashlib
import json
import os
import sys

raw_path, out_path, extract_dir, stem, archive_base, version, syft_version = sys.argv[1:8]

errors = []

with open(raw_path, encoding="utf-8") as handle:
    bom = json.load(handle)


def fail(message):
    errors.append(message)


# --- the shape CycloneDX requires -------------------------------------------------
if bom.get("bomFormat") != "CycloneDX":
    fail("bomFormat is %r, expected 'CycloneDX'" % bom.get("bomFormat"))
if bom.get("specVersion") != "1.6":
    fail("specVersion is %r, expected '1.6'" % bom.get("specVersion"))
if not isinstance(bom.get("version"), int) or bom.get("version", 0) < 1:
    fail("version is %r, expected a positive integer" % bom.get("version"))

components = bom.get("components")
if not isinstance(components, list):
    fail("components is %r, expected a list" % (components,))
    components = []

# --- scrub the build machine's absolute paths out of the document ----------------
# Syft records file-type components under their absolute path, which would publish
# the build machine's directory layout into a released artifact. The extraction
# directory is throwaway, so its prefix is stripped to a relative path. Both the
# abspath and the realpath are tried: on macOS $TMPDIR is a symlink
# (/var -> /private/var), and Syft records the resolved path while the shell
# holds the unresolved one — matching only one of them would leak the prefix.
prefixes = {os.path.abspath(extract_dir) + os.sep, os.path.realpath(extract_dir) + os.sep}

def scrub(value):
    if not isinstance(value, str):
        return value
    for prefix in prefixes:
        if value.startswith(prefix):
            return value[len(prefix):]
    return value

for component in components:
    if not isinstance(component, dict):
        continue
    if "name" in component:
        component["name"] = scrub(component.get("name"))
    for prop in component.get("properties", []) or []:
        if not isinstance(prop, dict):
            continue
        if "value" in prop:
            prop["value"] = scrub(prop.get("value"))

# --- the root is the archive, not Syft's scan target ------------------------------
# Syft names the scan target after --source-name already, but the ref, purl and
# type are normalised here so every per-artifact SBOM roots identically:
# <archive-stem>@<version> as an application.
metadata = bom.setdefault("metadata", {})
root = metadata.setdefault("component", {})
old_root_ref = root.get("bom-ref")
root["type"] = "application"
root["name"] = stem
root["version"] = version
root["bom-ref"] = "%s@%s" % (stem, version)
root["purl"] = "pkg:generic/%s@%s" % (stem, version)

# --- per-artifact provenance markers ----------------------------------------------
# Every per-artifact SBOM states which archive it describes and that it is a
# per-artifact document. Archives whose runtime closure cannot be measured get
# an additional not-measured marker rather than a quietly incomplete package
# list:
#
#   * kyber-weave-* / kyber-weave-mcp-* are single-file .NET publishes: the
#     closure is folded into the executable, so no package cataloger can read it.
#   * kyberdash-* are Node SEA blobs: the runtime and dashboard are bundled, so
#     the bytes are listed as files AND the document states runtime coverage was
#     not measurable (both catalogue entry and marker, never one instead of the
#     other).
#   * kyberdash-tray-* are Tauri bundles/installers: opaque binaries, same story.
#
# Squad skill zips and any other file collection carry no marker: their file
# list IS their content, with no hidden runtime closure to disclaim.
def not_measured_reason(name):
    if name.startswith("kyber-weave-mcp-") or name.startswith("kyber-weave-"):
        return "dotnet-single-file"
    if name.startswith("kyberdash-tray-"):
        return "tauri-bundle"
    if name.startswith("kyberdash-"):
        return "node-sea"
    return None

metadata.setdefault("properties", [])
metadata["properties"] = [
    prop for prop in metadata["properties"]
    if not str(prop.get("name", "")).startswith("kyber-weave:sbom:")
]
metadata["properties"].extend([
    {"name": "kyber-weave:sbom:generator", "value": "anchore/syft@%s" % syft_version},
    {"name": "kyber-weave:sbom:spec", "value": "CycloneDX 1.6"},
    {"name": "kyber-weave:sbom:version", "value": version},
    {"name": "kyber-weave:sbom:artifact", "value": archive_base},
    {"name": "kyber-weave:sbom:kind", "value": "per-artifact"},
])
reason = not_measured_reason(stem)
if reason is not None:
    metadata["properties"].append(
        {"name": "kyber-weave:sbom:not-measured", "value": reason}
    )

# --- the document must never be empty -----------------------------------------------
# A per-artifact SBOM with no components would publish an attested claim that an
# archive contains nothing. When Syft catalogs nothing (a tiny archive with no
# lockfiles and no recognizable binaries still yields file components with
# selection=all, but an empty extraction or an opaque installer might not), a
# synthetic file component for the archive itself is added so the failure mode is
# "lists the archive" rather than "lists nothing". The hash is computed over the
# staged bytes so the provenance entry is verifiable.
if not components:
    archive_path = os.path.join(os.path.abspath(os.path.dirname(out_path)), archive_base)
    hashes = []
    try:
        digest = hashlib.sha256()
        with open(archive_path, "rb") as handle:
            for chunk in iter(lambda: handle.read(1024 * 1024), b""):
                digest.update(chunk)
        hashes = [{"alg": "SHA-256", "content": digest.hexdigest()}]
    except OSError as exc:
        # WHY fail closed: a hash-less synthetic would attest provenance it
        # cannot verify. An unreadable archive is a pipeline failure, not an
        # empty scan, so it must block publish rather than ship without hashes.
        fail("cannot hash archive %s: %s" % (archive_base, exc))
        hashes = []
    synthetic = {
        "bom-ref": "%s-archive" % stem,
        "type": "file",
        "name": archive_base,
    }
    if hashes:
        synthetic["hashes"] = hashes
    components = [synthetic]
    bom["components"] = components
    print("no components catalogued for %s; recording the archive itself as provenance" % archive_base)
else:
    bom["components"] = components

# --- collapse duplicate packages --------------------------------------------------
# Same rationale as the aggregate generator: Syft can emit one component per
# lockfile that names a package. File components carry no purl and are left in
# place; only purl-bearing packages collapse, with the dependency graph remapped
# onto the surviving bom-refs.
from collections import OrderedDict

raw_count = len(components)
canonical_by_purl = OrderedDict()
remap = {}
if old_root_ref:
    remap[old_root_ref] = root["bom-ref"]
survivors = []
for component in components:
    if not isinstance(component, dict):
        survivors.append(component)
        continue
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

grouped = OrderedDict()
for entry in bom.get("dependencies") or []:
    if not isinstance(entry, dict):
        continue
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
known_refs = {c.get("bom-ref") for c in components if isinstance(c, dict)}
known_refs.add(root.get("bom-ref"))
if old_root_ref:
    known_refs.add(old_root_ref)
seen_refs = set()
for entry in bom.get("dependencies", []) or []:
    if not isinstance(entry, dict):
        continue
    if entry.get("ref") not in known_refs:
        fail("dependency graph references an unknown component: %r" % entry.get("ref"))
    if entry.get("ref") in seen_refs:
        fail("dependency graph has more than one entry for %r" % entry.get("ref"))
    seen_refs.add(entry.get("ref"))
    for target in entry.get("dependsOn", []) or []:
        if target not in known_refs:
            fail("dependency graph references an unknown component: %r" % target)
        if target == entry.get("ref"):
            fail("dependency graph entry %r depends on itself" % target)

if not isinstance(bom.get("components"), list) or not bom.get("components"):
    fail("the SBOM has no components")

print("  %-40s %4d component(s)" % (archive_base, len(bom.get("components", []))))

# --- write it out ------------------------------------------------------------------
# Nothing is written until every check has passed. A failed run must not leave an
# SBOM on disk for a later step to pick up and publish.
if errors:
    # WHY remove stale out_path: a previous successful run may have left an
    # SBOM behind, and publishing it after this run failed would attest the
    # wrong bytes. The validator path already removes; the errors path must too.
    try:
        os.unlink(out_path)
    except FileNotFoundError:
        pass
    print("")
    for message in errors:
        print("::error::%s" % message)
    print("")
    print("SBOM not written: %s" % out_path)
    sys.exit(1)

with open(out_path, "w", encoding="utf-8") as handle:
    json.dump(bom, handle, indent=2, sort_keys=False)
    handle.write("\n")

print("SBOM written: %s" % out_path)
PY

    rm -f "$raw_file"

    # Schema validation is the last gate (no --require-ecosystems: an extracted
    # archive rarely contains lockfiles, so demanding ecosystems would fail
    # every binary-only archive). A rejected SBOM is removed so a later step
    # cannot publish it.
    if ! python3 "$VALIDATOR" "$out_file"; then
        rm -f "$out_file"
        die "the generated SBOM for ${archive_base} failed CycloneDX schema validation and was removed"
    fi
}

failed=0
batch_pids=()
processed=0
for archive in "${archives[@]}"; do
    [[ -f "$archive" ]] || continue
    processed=$((processed + 1))
    archive_base="$(basename "$archive")"
    process_archive "$archive" >"${STATUS_DIR}/${archive_base}.log" 2>&1 &
    batch_pids+=($!)
    if [[ "${#batch_pids[@]}" -ge "$JOBS" ]]; then
        for pid in "${batch_pids[@]}"; do
            wait "$pid" || failed=1
        done
        batch_pids=()
    fi
done
if [[ "${#batch_pids[@]}" -gt 0 ]]; then
    for pid in "${batch_pids[@]}"; do
        wait "$pid" || failed=1
    done
    batch_pids=()
fi

# Deterministic replay: workers ran in parallel, so completion order varied.
# The glob expands in sorted order, so the console record matches a sequential
# run even though the scans overlapped.
for status_log in "${STATUS_DIR}"/*.log; do
    [[ -f "$status_log" ]] || continue
    cat "$status_log"
done

# Fail closed: a single archive failure fails the run after every worker has
# finished, so no partial SBOM set is ever mistaken for a complete one. Failed
# workers already removed their own SBOM above.
[[ "$failed" -eq 0 ]] || die "per-artifact SBOM generation failed for one or more archives; no partial set is published"

# WHY count written, not glob total: non-regular matches are skipped above, so
# reporting the glob size would claim SBOMs that were never written. On the
# success path one SBOM was written per processed archive.
count="$processed"

log ""
log "per-artifact SBOMs: ${count} written to ${ASSETS_DIR}"
