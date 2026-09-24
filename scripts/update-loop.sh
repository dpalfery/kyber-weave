#!/usr/bin/env bash
# The local inner loop for `kyber-weave update` and `kyber-weave squad install`.
#
#   scripts/update-loop.sh
#
# Publishes the working tree as a stand-in Release, serves it from loopback, installs
# a "from" build into a throwaway directory, and drives a real self-update followed by
# a real Squad install against it. Everything runs against published single-file
# binaries, because that is the only shape in which either failure reproduces.
#
# The `--from` side matters more than it looks. A self-updater is always executed by
# the *old* binary, so a fix to the updater cannot be proven by the release that
# contains it — only by updating away from a build that predates it.
#
# There is a limit to how far back `--from` can reach: redirecting a build at all
# requires it to honour KYBER_WEAVE_RELEASE_ORIGIN, so any ref older than that support
# will ignore the local server and reach for real github.com. Such a run fails with a
# 404 on SHA256SUMS.txt rather than a misleading pass. Builds from the working tree are
# the default for exactly that reason; use `--from <ref>` once the ref you want to test
# from carries the override.
#
# The KyberDash cases then run against the same release: an installed kyberdash is
# replaced, `--no-kyberdash` and `--no-menubar` are honoured, a recorded tray makes the
# update run the new `kyberdash menubar --update`, and a release below the KyberDash
# floor, which carries no kyberdash archive, still updates the CLI and MCP. Last, the
# recovery cases make release-local.sh's KyberDash build fail on purpose, and check that
# it puts dash/package.json back and throws away a bad cached Node download.
#
# Options:
#   --from <source>   working (default) | installed | <git ref>
#   --to <version>    version to publish and update to (default: 99.0.0-local)
#   --rid <rid>       target RID (default: this machine's)
#   --skip-squad      run only the self-update half
#   --no-kyberdash    skip the KyberDash build and every case that needs it
#   --keep            leave the sandbox and release tree in place for inspection
#   --reuse           skip publishing if the release tree already has this version

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FROM="working"
TO_VERSION="99.0.0-local"
RID=""
SKIP_SQUAD=""
NO_KYBERDASH=""
KEEP=""
REUSE=""

# Below every KyberDash floor, as every release before 0.1.7-rc.9 was, so its release
# carries no kyberdash archive — the case the floor exists for.
FLOOR_VERSION="0.0.1-loopfrom"

die() { printf '\033[31mupdate-loop: error: %s\033[0m\n' "$1" >&2; exit 1; }
log() { printf '\033[36mupdate-loop:\033[0m %s\n' "$1" >&2; }
pass() { printf '\033[32m  PASS\033[0m %s\n' "$1" >&2; }
fail() { printf '\033[31m  FAIL\033[0m %s\n' "$1" >&2; FAILURES=$((FAILURES + 1)); }

FAILURES=0

while [ $# -gt 0 ]; do
    case "$1" in
        --from) [ $# -ge 2 ] || die "--from needs a value"; FROM="$2"; shift 2 ;;
        --to)   [ $# -ge 2 ] || die "--to needs a value"; TO_VERSION="${2#v}"; shift 2 ;;
        --rid)  [ $# -ge 2 ] || die "--rid needs a value"; RID="$2"; shift 2 ;;
        --skip-squad) SKIP_SQUAD=1; shift ;;
        --no-kyberdash) NO_KYBERDASH=1; shift ;;
        --keep) KEEP=1; shift ;;
        --reuse) REUSE=1; shift ;;
        -h|--help) sed -n '2,36p' "$0" >&2; exit 0 ;;
        *) die "unknown option: $1" ;;
    esac
done

command -v python3 >/dev/null 2>&1 || die "need python3 on PATH"
command -v dotnet >/dev/null 2>&1 || die "need dotnet on PATH"
command -v curl >/dev/null 2>&1 || die "need curl on PATH"

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

# The installer's own helpers, so the floor and the RID mapping asserted here are the
# ones install.sh uses rather than a copy of them. install.sh parses its arguments before
# library mode returns, so the helper call is set aside and the arguments cleared first.
installer() {
    (
        helper=("$@")
        set --
        # shellcheck source=scripts/install.sh
        KYBER_WEAVE_INSTALL_LIB=1 . "${REPO_ROOT}/scripts/install.sh"
        "${helper[@]}"
    )
}

KYBERDASH_RID="$(installer kyber_weave_kyberdash_rid "$RID")"
if [ -z "$NO_KYBERDASH" ] && ! installer kyber_weave_release_has_kyberdash "$TO_VERSION"; then
    die "--to ${TO_VERSION} is below the KyberDash floor, so its release would carry no kyberdash; pass a later --to, or --no-kyberdash"
fi

RELEASE_TREE="${REPO_ROOT}/.local-release"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/kw-loop-XXXXXX")"
BIN="${SANDBOX}/bin"
FROM_BIN="${SANDBOX}/from"
TO_BIN="${SANDBOX}/to"
SERVER_PID=""

cleanup() {
    [ -n "$SERVER_PID" ] && kill "$SERVER_PID" 2>/dev/null || true
    if [ -n "$KEEP" ]; then
        log "kept sandbox: ${SANDBOX}"
        log "kept release tree: ${RELEASE_TREE}"
    else
        rm -rf "$SANDBOX"
    fi
}
trap cleanup EXIT INT TERM

mkdir -p "$BIN" "$FROM_BIN" "$TO_BIN" "${SANDBOX}/home"

# ------------------------------------------------------------------ publish "to"

TO_TAG="v${TO_VERSION}"
KYBERDASH_ARCHIVE="kyberdash-${KYBERDASH_RID}.tar.gz"
if [ -n "$REUSE" ] && [ -f "${RELEASE_TREE}/${TO_TAG}/SHA256SUMS.txt" ] \
    && { [ -n "$NO_KYBERDASH" ] || [ -f "${RELEASE_TREE}/${TO_TAG}/${KYBERDASH_ARCHIVE}" ]; }; then
    log "reusing existing release tree for ${TO_TAG}"
else
    log "publishing ${TO_TAG} from the working tree"
    "${REPO_ROOT}/scripts/release-local.sh" \
        --version "$TO_VERSION" --out "$RELEASE_TREE" --rid "$RID" \
        ${SKIP_SQUAD:+--no-squad} ${NO_KYBERDASH:+--no-kyberdash}
fi

# ------------------------------------------------------------------ stage "from"

# The "from" binaries only need to exist and be runnable; how they arrived does not
# affect the failure under test, which is a running image replacing itself. Copying
# sidesteps install.sh, which is HTTPS-only and cannot read the local server.
stage_from_directory() {
    source_dir="$1"
    [ -x "${source_dir}/kyber-weave" ] || die "no kyber-weave in ${source_dir}"
    cp "${source_dir}/kyber-weave" "${BIN}/kyber-weave"
    if [ -x "${source_dir}/kyber-weave-mcp" ]; then
        cp "${source_dir}/kyber-weave-mcp" "${BIN}/kyber-weave-mcp"
    fi
    chmod 755 "${BIN}"/kyber-weave*
    xattr -d com.apple.quarantine "${BIN}"/kyber-weave* 2>/dev/null || true
}

stage_from_release_tree() {
    tag="$1"
    target="$2"
    for binary in kyber-weave kyber-weave-mcp; do
        archive="${RELEASE_TREE}/${tag}/${binary}-${RID}.tar.gz"
        [ -f "$archive" ] || die "missing ${archive}"
        tar -C "$target" -xzf "$archive"
    done
    chmod 755 "${target}"/kyber-weave*
    xattr -d com.apple.quarantine "${target}"/kyber-weave* 2>/dev/null || true
}

# The "from" release doubles as the below-floor release, so it is built without
# KyberDash whichever source it comes from.
case "$FROM" in
    installed)
        install_dir="${KYBER_WEAVE_INSTALL_DIR:-${HOME}/.local/bin}"
        log "staging 'from' binaries from ${install_dir}"
        log "note: this only works if the installed build honours KYBER_WEAVE_RELEASE_ORIGIN"
        stage_from_directory "$install_dir"
        log "publishing v${FLOOR_VERSION}, below the KyberDash floor, from the working tree"
        "${REPO_ROOT}/scripts/release-local.sh" \
            --version "$FLOOR_VERSION" --out "$RELEASE_TREE" --rid "$RID" --no-squad --no-kyberdash
        ;;
    working)
        log "staging 'from' binaries from the working tree (same code as 'to')"
        FROM_VERSION="$FLOOR_VERSION"
        "${REPO_ROOT}/scripts/release-local.sh" \
            --version "$FROM_VERSION" --out "$RELEASE_TREE" --rid "$RID" --no-squad --no-kyberdash
        stage_from_release_tree "v${FROM_VERSION}" "$BIN"
        ;;
    *)
        log "staging 'from' binaries built at ${FROM}"
        FROM_VERSION="$FLOOR_VERSION"
        "${REPO_ROOT}/scripts/release-local.sh" \
            --version "$FROM_VERSION" --out "$RELEASE_TREE" --rid "$RID" \
            --ref "$FROM" --no-squad --no-kyberdash
        stage_from_release_tree "v${FROM_VERSION}" "$BIN"
        ;;
esac

# Kept pristine: the self-update below replaces what is in $BIN, and every KyberDash
# case starts again from these.
cp -p "${BIN}"/kyber-weave* "$FROM_BIN/"
stage_from_release_tree "$TO_TAG" "$TO_BIN"

FROM_REPORTED="$("${BIN}/kyber-weave" --version 2>&1 || true)"
log "from: ${FROM_REPORTED}  ->  to: kyber-weave ${TO_VERSION}"

if [ "$FROM_REPORTED" = "kyber-weave ${TO_VERSION}" ]; then
    die "'from' and 'to' are both ${TO_VERSION}; update would short-circuit. Pass --to a different version."
fi

# ------------------------------------------------------------------- serve local

log "starting the loopback release server"
SERVER_STARTED_AT="$(date +%s)"
SERVER_OUT="${SANDBOX}/server.port"
SERVER_LOG="${SANDBOX}/server.log"
python3 "${REPO_ROOT}/scripts/local-release-server.py" --root "$RELEASE_TREE" --port 0 \
    > "$SERVER_OUT" 2> "$SERVER_LOG" &
SERVER_PID=$!

# Dumps everything needed to tell "still starting" from "crashed" apart. The first
# version of this printed only an empty log, which said neither.
server_diagnostics() {
    printf 'python3: %s\n' "$(command -v python3)" >&2
    python3 --version >&2 2>&1 || true
    if kill -0 "$SERVER_PID" 2>/dev/null; then
        printf 'server process %s is alive but never became ready\n' "$SERVER_PID" >&2
    else
        wait "$SERVER_PID" 2>/dev/null && server_status=0 || server_status=$?
        printf 'server process %s exited with status %s\n' "$SERVER_PID" "$server_status" >&2
    fi
    printf -- '--- stdout (%s bytes) ---\n' "$(wc -c < "$SERVER_OUT" | tr -d ' ')" >&2
    cat "$SERVER_OUT" >&2 2>/dev/null || true
    printf -- '--- stderr (%s bytes) ---\n' "$(wc -c < "$SERVER_LOG" | tr -d ' ')" >&2
    cat "$SERVER_LOG" >&2 2>/dev/null || true
    printf -- '--- release tree ---\n' >&2
    ls -la "$RELEASE_TREE" >&2 2>/dev/null || true
}

# A cold python3 on a loaded runner is slow to start — the first version of this waited
# five seconds and lost that race on macOS CI right after two dotnet publishes. Wait
# generously; the loop exits as soon as the port appears, so a warm start costs nothing.
PORT=""
WAITED=0
while [ "$WAITED" -lt "${SERVER_START_TIMEOUT:-60}" ]; do
    # Only accept a newline-terminated line. Reading mid-write would yield a truncated
    # port and send every later request somewhere unrelated. Command substitution strips
    # trailing newlines, so an empty result here means the last byte was one.
    if [ -s "$SERVER_OUT" ] && [ -z "$(tail -c 1 "$SERVER_OUT" 2>/dev/null)" ]; then
        CANDIDATE="$(head -n 1 "$SERVER_OUT" 2>/dev/null || true)"
        case "$CANDIDATE" in
            ''|*[!0-9]*) ;;
            *) PORT="$CANDIDATE" ;;
        esac
    fi
    [ -n "$PORT" ] && break
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
        server_diagnostics
        die "the release server exited before reporting a port"
    fi
    sleep 1
    WAITED=$((WAITED + 1))
done

if [ -z "$PORT" ]; then
    server_diagnostics
    die "the release server did not report a port within ${SERVER_START_TIMEOUT:-60}s"
fi

ORIGIN="http://127.0.0.1:${PORT}"

# Having a port is not the same as accepting connections. Probe until it answers, so a
# slow bind surfaces here rather than as a confusing download failure later.
READY=""
WAITED=0
while [ "$WAITED" -lt 30 ]; do
    if curl -fsS -o /dev/null --max-time 2 "${ORIGIN}/healthz" 2>/dev/null; then
        READY=1
        break
    fi
    sleep 1
    WAITED=$((WAITED + 1))
done

if [ -z "$READY" ]; then
    server_diagnostics
    die "the release server never answered ${ORIGIN}/healthz"
fi

export KYBER_WEAVE_RELEASE_ORIGIN="$ORIGIN"

# Readiness took ~36s on a macOS runner against a 60s cap, so the headroom is real but
# not generous. Reporting it means a drift toward the cap shows up as a rising number
# in a passing run, rather than as a flake with nothing to compare against.
SERVER_READY_SECONDS=$(( $(date +%s) - SERVER_STARTED_AT ))
log "serving ${RELEASE_TREE} at ${ORIGIN} (ready in ${SERVER_READY_SECONDS}s of ${SERVER_START_TIMEOUT:-60}s)"

# ------------------------------------------------------------------ self-update

# Every update runs under a HOME inside the sandbox. The updater reads the tray record
# from $HOME/.kyberdash, and on a machine with a real tray the loop must not drive it.
log "running: kyber-weave update ${TO_VERSION}"
set +e
UPDATE_OUT="$(HOME="${SANDBOX}/home" "${BIN}/kyber-weave" update "$TO_VERSION" 2>&1)"
UPDATE_CODE=$?
set -e
printf '%s\n' "$UPDATE_OUT" | sed 's/^/    /' >&2

if [ "$UPDATE_CODE" -eq 0 ]; then
    pass "update exited 0"
else
    fail "update exited ${UPDATE_CODE}"
    # The local server answers every path the updater asks for, so a 404 here means the
    # request never arrived — an older 'from' build ignoring the origin override.
    if printf '%s' "$UPDATE_OUT" | grep -q "Does that release exist"; then
        log "hint: the 'from' build appears to predate KYBER_WEAVE_RELEASE_ORIGIN and"
        log "      went to github.com instead. Use --from working, or a newer ref."
    fi
fi

CLI_AFTER="$("${BIN}/kyber-weave" --version 2>&1 || echo "<crashed>")"
if [ "$CLI_AFTER" = "kyber-weave ${TO_VERSION}" ]; then
    pass "CLI reports ${TO_VERSION} after update"
else
    fail "CLI reports '${CLI_AFTER}', expected 'kyber-weave ${TO_VERSION}'"
fi

MCP_AFTER="$("${BIN}/kyber-weave-mcp" --version 2>&1 || echo "<crashed>")"
if [ "$MCP_AFTER" = "kyber-weave-mcp ${TO_VERSION}" ]; then
    pass "MCP reports ${TO_VERSION} after update"
else
    fail "MCP reports '${MCP_AFTER}', expected 'kyber-weave-mcp ${TO_VERSION}'"
fi

# Update replaces what is installed; it never adds a kyberdash nobody installed.
if [ -e "${BIN}/kyberdash" ]; then
    fail "update created a kyberdash that was not installed"
else
    pass "an absent kyberdash stays absent"
fi

# A binary that survived the swap can still be missing assemblies it had not loaded
# when its own image was replaced. Exercising a command that touches unrelated code
# paths is what catches that, not --version.
set +e
DOCTOR_OUT="$("${BIN}/kyber-weave" squad doctor 2>&1)"
DOCTOR_CODE=$?
set -e
if printf '%s' "$DOCTOR_OUT" | grep -q "Could not load file or assembly"; then
    fail "post-update binary cannot load a bundled assembly"
    printf '%s\n' "$DOCTOR_OUT" | sed 's/^/    /' >&2
elif [ "$DOCTOR_CODE" -le 2 ]; then
    pass "post-update binary runs a real command"
else
    fail "squad doctor exited ${DOCTOR_CODE}"
    printf '%s\n' "$DOCTOR_OUT" | sed 's/^/    /' >&2
fi

# ---------------------------------------------------------------- squad install

if [ -z "$SKIP_SQUAD" ]; then
    PROJECT="${SANDBOX}/project"
    mkdir -p "$PROJECT"
    git -C "$PROJECT" init -q .

    log "running: kyber-weave squad install --target copilot"
    set +e
    SQUAD_OUT="$(cd "$PROJECT" && HOME="$SANDBOX" "${BIN}/kyber-weave" squad install --target copilot 2>&1)"
    SQUAD_CODE=$?
    set -e
    printf '%s\n' "$SQUAD_OUT" | sed 's/^/    /' >&2

    if [ "$SQUAD_CODE" -eq 0 ]; then
        pass "squad install exited 0"
    else
        fail "squad install exited ${SQUAD_CODE}"
    fi

    DEPLOYED="$(find "$PROJECT" -type f -not -path '*/.git/*' | wc -l | tr -d ' ')"
    if [ "$DEPLOYED" -gt 0 ]; then
        pass "squad install deployed ${DEPLOYED} files"
    else
        fail "squad install deployed no files"
    fi
fi

# -------------------------------------------------------------------- KyberDash

# Each case starts from a fresh copy of a staged build, with its own bin directory and
# HOME, and runs one real update against the same local release. The "older kyberdash"
# a case installs is a stub that reports a version no release carries, so a replaced
# binary and an untouched one can be told apart by asking each for its version.
STUB_KYBERDASH_VERSION="0.0.1-stub"
case "$(uname -s)" in
    Darwin) TRAY_PLATFORM=darwin ;;
    *) TRAY_PLATFORM=linux ;;
esac

begin_case() {
    CASE_NAME="$1"
    CASE_BIN="${SANDBOX}/cases/${CASE_NAME}/bin"
    CASE_HOME="${SANDBOX}/cases/${CASE_NAME}/home"
    mkdir -p "$CASE_BIN" "$CASE_HOME"
    cp -p "$2"/kyber-weave* "$CASE_BIN/"
}

install_stub_kyberdash() {
    printf '#!/bin/sh\necho %s\n' "$STUB_KYBERDASH_VERSION" > "${CASE_BIN}/kyberdash"
    chmod 755 "${CASE_BIN}/kyberdash"
}

# The record `kyberdash menubar` writes (dash/src/install/menubar.ts). Its paths point
# into the case directory, so no step of a tray update can reach outside the sandbox.
record_tray() {
    mkdir -p "${CASE_HOME}/.kyberdash"
    cat > "${CASE_HOME}/.kyberdash/tray.json" <<EOF
{
  "path": "${CASE_HOME}/Applications/KyberDash.app",
  "version": "${STUB_KYBERDASH_VERSION}",
  "installedAt": "2026-01-01T00:00:00.000Z",
  "platform": "${TRAY_PLATFORM}",
  "kyberdashPath": "${CASE_BIN}/kyberdash"
}
EOF
}

update_case() {
    log "${CASE_NAME}: kyber-weave update $*"
    set +e
    CASE_OUT="$(HOME="$CASE_HOME" "${CASE_BIN}/kyber-weave" update "$@" 2>&1)"
    CASE_CODE=$?
    set -e
    printf '%s\n' "$CASE_OUT" | sed 's/^/    /' >&2
}

expect_exit_zero() {
    if [ "$CASE_CODE" -eq 0 ]; then
        pass "${CASE_NAME}: update exited 0"
    else
        fail "${CASE_NAME}: update exited ${CASE_CODE}"
    fi
}

expect_logged() {
    if printf '%s' "$CASE_OUT" | grep -qF -- "$1"; then
        pass "${CASE_NAME}: $2"
    else
        fail "${CASE_NAME}: $2 (the output has no '$1')"
    fi
}

expect_reports() {
    reported="$("${CASE_BIN}/$1" --version 2>&1 || echo "<crashed>")"
    if [ "$reported" = "$2" ]; then
        pass "${CASE_NAME}: $3"
    else
        fail "${CASE_NAME}: $3 ($1 reports '${reported}', expected '$2')"
    fi
}

if [ -n "$NO_KYBERDASH" ]; then
    log "--no-kyberdash given; skipping the cases that need a kyberdash release asset"
else
    begin_case kyberdash-replaced "$FROM_BIN"
    install_stub_kyberdash
    update_case "$TO_VERSION"
    expect_exit_zero
    expect_reports kyberdash "$TO_VERSION" "an installed kyberdash is replaced and answers --version"
    expect_logged "no tray install recorded" "without a tray record, the tray is left alone"

    begin_case kyberdash-opt-out "$FROM_BIN"
    install_stub_kyberdash
    record_tray
    update_case "$TO_VERSION" --no-kyberdash
    expect_exit_zero
    expect_reports kyber-weave "kyber-weave ${TO_VERSION}" "--no-kyberdash still updates the CLI"
    expect_reports kyberdash "$STUB_KYBERDASH_VERSION" "--no-kyberdash leaves kyberdash alone"
    expect_logged "--no-kyberdash given; leaving the KyberDash tray unchanged" "--no-kyberdash leaves the tray alone"

    begin_case tray-opt-out "$FROM_BIN"
    install_stub_kyberdash
    record_tray
    update_case "$TO_VERSION" --no-menubar
    expect_exit_zero
    expect_reports kyberdash "$TO_VERSION" "--no-menubar still replaces kyberdash"
    expect_logged "--no-menubar given; leaving the KyberDash tray unchanged" "--no-menubar leaves the tray alone"

    # The tray step cannot succeed here: there is no tray to install on Linux, and on
    # macOS the release has no tray archive. What it must do is run the new kyberdash's
    # `menubar --update` and report a failure by name. The kyberdash's own error is the
    # evidence the subcommand ran: when the root `--version` swallowed the updater's
    # `--version <v>`, kyberdash printed its version, exited 0, and the update reported
    # a tray update that never happened.
    begin_case tray-delegated "$FROM_BIN"
    install_stub_kyberdash
    record_tray
    update_case "$TO_VERSION"
    if [ "$CASE_CODE" -ne 0 ]; then
        pass "${CASE_NAME}: a failed tray step fails the update"
    else
        fail "${CASE_NAME}: update exited 0 although the tray could not be updated"
    fi
    expect_logged "the KyberDash tray step failed" "the failure names the tray step"
    expect_logged "kyberdash menubar:" "the release's kyberdash ran menubar --update"
    expect_reports kyberdash "$TO_VERSION" "binaries stay committed when the tray step fails"
fi

# The regression test for a release with no kyberdash archive, which needs no KyberDash
# build: the updater must not ask for the archive, so the CLI and MCP still update.
begin_case kyberdash-floor "$TO_BIN"
install_stub_kyberdash
record_tray
update_case "$FLOOR_VERSION"
expect_exit_zero
expect_reports kyber-weave "kyber-weave ${FLOOR_VERSION}" "a release without kyberdash still updates the CLI"
expect_reports kyber-weave-mcp "kyber-weave-mcp ${FLOOR_VERSION}" "a release without kyberdash still updates the MCP"
expect_reports kyberdash "$STUB_KYBERDASH_VERSION" "kyberdash is left alone below the floor"
expect_logged "predates KyberDash" "the kyberdash skip is logged"
expect_logged "predates the KyberDash tray" "the tray skip is logged"

# ------------------------------------------------------- release-local.sh recovery

# The failure paths of release-local.sh's KyberDash build, which a passing publish never
# reaches. Both fail inside that build, before anything is published, so each costs
# seconds. Each writes to its own --out under the sandbox, never to the release tree.
sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{ print $1 }'
    else
        shasum -a 256 "$1" | awk '{ print $1 }'
    fi
}

release_local_case() {
    out="$1"
    shift
    log "${CASE_NAME}: release-local.sh --out ${out}"
    set +e
    CASE_OUT="$("$@" "${REPO_ROOT}/scripts/release-local.sh" \
        --version 0.0.0-recovery --out "$out" --rid "$RID" --no-squad 2>&1)"
    CASE_CODE=$?
    set -e
    printf '%s\n' "$CASE_OUT" | sed 's/^/    /' >&2
    if [ "$CASE_CODE" -ne 0 ]; then
        pass "${CASE_NAME}: the failed build fails release-local.sh"
    else
        fail "${CASE_NAME}: release-local.sh exited 0 although its build was made to fail"
    fi
}

if [ -z "$NO_KYBERDASH" ]; then
    # `npm version` has already stamped dash/package.json when tsup runs, so a tsup
    # failure is the case where the manifest is left dirty unless the exit trap restores
    # it. A stand-in npx that always fails forces exactly that. The Node download the
    # publish above cached is copied in, so the case needs no network.
    CASE_NAME="recovery-manifest"
    recovery="${SANDBOX}/recovery"
    mkdir -p "${recovery}/fake-bin" "${recovery}/manifest/.cache"
    printf '#!/bin/sh\necho "npx: failing on purpose for the recovery case" >&2\nexit 1\n' \
        > "${recovery}/fake-bin/npx"
    chmod 755 "${recovery}/fake-bin/npx"
    if [ -d "${RELEASE_TREE}/.cache" ]; then
        cp -R "${RELEASE_TREE}/.cache/." "${recovery}/manifest/.cache/"
    fi
    manifest_before="$(sha256_of "${REPO_ROOT}/dash/package.json") $(sha256_of "${REPO_ROOT}/dash/package-lock.json")"
    release_local_case "${recovery}/manifest" env PATH="${recovery}/fake-bin:${PATH}"
    expect_logged "failed: npx tsup" "the failing step is named"
    manifest_after="$(sha256_of "${REPO_ROOT}/dash/package.json") $(sha256_of "${REPO_ROOT}/dash/package-lock.json")"
    if [ "$manifest_after" = "$manifest_before" ]; then
        pass "${CASE_NAME}: dash/package.json and its lockfile are restored"
    else
        fail "${CASE_NAME}: dash/package.json or its lockfile was left changed"
    fi

    # A cached download that does not match its manifest must be thrown away, not
    # trusted by the next run. Both files are seeded, so this needs no network either.
    CASE_NAME="recovery-cache"
    node_version="$(tr -d '[:space:]' < "${REPO_ROOT}/dash/.nvmrc")"
    node_version="${node_version#v}"
    seeded="${recovery}/cache/.cache/node-v${node_version}"
    node_tarball="node-v${node_version}-${KYBERDASH_RID}.tar.xz"
    mkdir -p "$seeded"
    printf 'not a Node release\n' > "${seeded}/${node_tarball}"
    printf '%064d  %s\n' 0 "$node_tarball" > "${seeded}/SHASUMS256.txt"
    release_local_case "${recovery}/cache" env
    expect_logged "failed its checksum" "the checksum failure is named"
    if [ ! -e "${seeded}/${node_tarball}" ] && [ ! -e "${seeded}/SHASUMS256.txt" ]; then
        pass "${CASE_NAME}: the mismatched download and its manifest are removed"
    else
        fail "${CASE_NAME}: a mismatched download was left in the cache"
    fi
fi

# ----------------------------------------------------------------------- verdict

echo >&2
if [ "$FAILURES" -eq 0 ]; then
    printf '\033[32mupdate-loop: all checks passed\033[0m\n' >&2
    exit 0
fi

printf '\033[31mupdate-loop: %s check(s) failed\033[0m\n' "$FAILURES" >&2
exit 1
