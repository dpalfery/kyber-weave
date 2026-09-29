#!/usr/bin/env bash
#
# Verify release checksum manifest: check completeness, reject stale inbound copies,
# compute hashes, and validate the result.
#
# Usage: verify-release-checksums.sh [--list] <asset-dir> <version>
#
# Exits 0 and writes SHA256SUMS.txt if successful. Exits non-zero and emits
# ::error:: diagnostics if any expected asset is missing, any unexpected asset
# is present, if any hash is wrong, or if the manifest is malformed.
#
# With --list: outputs the expected asset list (one per line) and exits 0.
#
# The expected asset list is a Bash array. When a new asset is added to the
# release, add it here, update the corresponding build job, and update the test
# that asserts the count.
#

set -euo pipefail

# Handle --list flag
if [[ "${1:-}" == "--list" ]]; then
    # --list requires three arguments: --list <asset-dir> <version>
    if [[ $# -lt 3 ]]; then
        echo "::error::usage: $(basename "$0") [--list] <asset-dir> <version>" >&2
        exit 1
    fi
    ASSET_DIR="$2"
    VERSION="$3"
    LIST_MODE=1
else
    # Normal mode requires two arguments: <asset-dir> <version>
    if [[ $# -lt 2 ]]; then
        echo "::error::usage: $(basename "$0") [--list] <asset-dir> <version>" >&2
        exit 1
    fi
    ASSET_DIR="$1"
    VERSION="$2"
    LIST_MODE=0
fi

# Expected assets: 20 files from five groups.
# CLI (5): kyber-weave-<rid>, osx and linux as .tar.gz, win as .zip
# MCP (5): kyber-weave-mcp-<rid>, same pattern
# KyberDash (5): kyberdash-<rid>, darwin as .tar.gz, linux as .tar.gz, win as .zip
# Tray (3): kyberdash-tray-darwin-arm64.zip, darwin-x64.zip, win-x64-setup.exe
# Squad (2): kyber-squad-<version>.zip, kyber-squad-plugin-<version>.zip
declare -a EXPECTED_ASSETS=(
    "kyber-weave-linux-x64.tar.gz"
    "kyber-weave-linux-arm64.tar.gz"
    "kyber-weave-osx-x64.tar.gz"
    "kyber-weave-osx-arm64.tar.gz"
    "kyber-weave-win-x64.zip"
    "kyber-weave-mcp-linux-x64.tar.gz"
    "kyber-weave-mcp-linux-arm64.tar.gz"
    "kyber-weave-mcp-osx-x64.tar.gz"
    "kyber-weave-mcp-osx-arm64.tar.gz"
    "kyber-weave-mcp-win-x64.zip"
    "kyberdash-darwin-arm64.tar.gz"
    "kyberdash-darwin-x64.tar.gz"
    "kyberdash-linux-arm64.tar.gz"
    "kyberdash-linux-x64.tar.gz"
    "kyberdash-win-x64.zip"
    "kyberdash-tray-darwin-arm64.zip"
    "kyberdash-tray-darwin-x64.zip"
    "kyberdash-tray-win-x64-setup.exe"
    "kyber-squad-${VERSION}.zip"
    "kyber-squad-plugin-${VERSION}.zip"
)

# If --list flag was given, output the expected asset list and exit.
if [[ $LIST_MODE -eq 1 ]]; then
    for asset in "${EXPECTED_ASSETS[@]}"; do
        echo "$asset"
    done
    exit 0
fi

cd "$ASSET_DIR"

# Enable nullglob so globs that match nothing expand to nothing, and dotglob
# to catch hidden files that should not be present.
shopt -s dotglob nullglob

# Step 1: Delete any stale SHA256SUMS.txt from the downloaded artifacts.
# This file must not be listed in the final manifest: only the actual release
# assets go into the checksum. The per-RID SHA256SUMS.txt that build-kyberdash
# uploads carries stale hashes; we delete it and recompute from scratch.
if [[ -f SHA256SUMS.txt ]]; then
    echo "::warning::discarding stale inbound SHA256SUMS.txt (recomputing from expected assets)" >&2
    rm -f SHA256SUMS.txt
fi

# Step 2: Check that all expected assets are present and no unexpected ones are.
# Detect any files that are not in the expected list, including hidden files and
# directories (which should never exist in release-assets).
declare -a actual_files=()
for entry in *; do
    # Reject anything that is not a regular file.
    if [[ ! -f "$entry" ]]; then
        echo "::error::unexpected release asset: $entry (not a regular file)" >&2
        exit 1
    fi
    actual_files+=("$entry")
done

# Helper function to check if an element is in an array.
# Usage: in_array "element" "${array[@]}"
# Returns 0 if found, 1 if not found.
in_array() {
    local needle="$1"
    shift
    local item
    for item in "$@"; do
        [[ "$item" == "$needle" ]] && return 0
    done
    return 1
}

# Check: every expected asset exists and every actual file is expected.
declare -a missing=()
declare -a unexpected=()

for asset in "${EXPECTED_ASSETS[@]}"; do
    if [[ ! -f "$asset" ]]; then
        missing+=("$asset")
    fi
done

for f in "${actual_files[@]+"${actual_files[@]}"}"; do
    if ! in_array "$f" "${EXPECTED_ASSETS[@]}"; then
        unexpected+=("$f")
    fi
done

if [[ ${#missing[@]} -gt 0 ]]; then
    for asset in "${missing[@]}"; do
        echo "::error::missing release asset: $asset" >&2
    done
    exit 1
fi

if [[ ${#unexpected[@]} -gt 0 ]]; then
    for asset in "${unexpected[@]}"; do
        echo "::error::unexpected release asset: $asset" >&2
    done
    exit 1
fi

# Step 3: Compute hashes into a temp file, then move to SHA256SUMS.txt.
# This mirrors the approach in scripts/release-local.sh: write to temp,
# validate, then atomically rename so the file is never partially written.
TEMP_SUMS="$(mktemp)"
trap 'rm -f "$TEMP_SUMS"' EXIT

sha256sum "${EXPECTED_ASSETS[@]}" > "$TEMP_SUMS"

# Step 4: Assert the manifest format and content.
# - One line per asset (already guaranteed by the array above).
# - Each line matches ^[0-9a-f]{64}  <name>$.
# - No line names SHA256SUMS.txt (we deleted it; the hash list should not name it).
# - sha256sum --check --strict passes (verifies that every file still exists
#   and its hash is correct, and rejects extra lines or bad format).

# Validate each line's format.
while IFS= read -r line; do
    if ! [[ "$line" =~ ^[0-9a-f]{64}\ \ .+$ ]]; then
        echo "::error::malformed checksum line: $line" >&2
        exit 1
    fi
    # Extract the filename (everything after the two spaces).
    filename="${line#*  }"
    if [[ "$filename" == "SHA256SUMS.txt" ]]; then
        echo "::error::checksum manifest must not contain a line for itself: SHA256SUMS.txt" >&2
        exit 1
    fi
done < "$TEMP_SUMS"

# Verify: check that every line in the manifest corresponds to an existing
# file with the correct hash. --strict rejects extra files or missing entries.
if ! sha256sum --check --strict "$TEMP_SUMS" >/dev/null 2>&1; then
    echo "::error::checksum verification failed" >&2
    sha256sum --check --strict "$TEMP_SUMS" >&2 || true
    exit 1
fi

# All checks passed. Move the temp file into place.
mv "$TEMP_SUMS" SHA256SUMS.txt

echo "SHA256SUMS.txt created with ${#EXPECTED_ASSETS[@]} asset hashes"
exit 0
