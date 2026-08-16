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
# Options:
#   --from <source>   working (default) | installed | <git ref>
#   --to <version>    version to publish and update to (default: 99.0.0-local)
#   --rid <rid>       target RID (default: this machine's)
#   --skip-squad      run only the self-update half
#   --keep            leave the sandbox and release tree in place for inspection
#   --reuse           skip publishing if the release tree already has this version

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FROM="working"
TO_VERSION="99.0.0-local"
RID=""
SKIP_SQUAD=""
KEEP=""
REUSE=""

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
        --keep) KEEP=1; shift ;;
        --reuse) REUSE=1; shift ;;
        -h|--help) sed -n '2,23p' "$0" >&2; exit 0 ;;
        *) die "unknown option: $1" ;;
    esac
done

command -v python3 >/dev/null 2>&1 || die "need python3 on PATH"
command -v dotnet >/dev/null 2>&1 || die "need dotnet on PATH"

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

RELEASE_TREE="${REPO_ROOT}/.local-release"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/kw-loop-XXXXXX")"
BIN="${SANDBOX}/bin"
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

mkdir -p "$BIN"

# ------------------------------------------------------------------ publish "to"

TO_TAG="v${TO_VERSION}"
if [ -n "$REUSE" ] && [ -f "${RELEASE_TREE}/${TO_TAG}/SHA256SUMS.txt" ]; then
    log "reusing existing release tree for ${TO_TAG}"
else
    log "publishing ${TO_TAG} from the working tree"
    "${REPO_ROOT}/scripts/release-local.sh" \
        --version "$TO_VERSION" --out "$RELEASE_TREE" --rid "$RID" \
        ${SKIP_SQUAD:+--no-squad}
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
    for binary in kyber-weave kyber-weave-mcp; do
        archive="${RELEASE_TREE}/${tag}/${binary}-${RID}.tar.gz"
        [ -f "$archive" ] || die "missing ${archive}"
        tar -C "$BIN" -xzf "$archive"
    done
    chmod 755 "${BIN}"/kyber-weave*
    xattr -d com.apple.quarantine "${BIN}"/kyber-weave* 2>/dev/null || true
}

case "$FROM" in
    installed)
        install_dir="${KYBER_WEAVE_INSTALL_DIR:-${HOME}/.local/bin}"
        log "staging 'from' binaries from ${install_dir}"
        log "note: this only works if the installed build honours KYBER_WEAVE_RELEASE_ORIGIN"
        stage_from_directory "$install_dir"
        ;;
    working)
        log "staging 'from' binaries from the working tree (same code as 'to')"
        FROM_VERSION="0.0.1-loopfrom"
        "${REPO_ROOT}/scripts/release-local.sh" \
            --version "$FROM_VERSION" --out "$RELEASE_TREE" --rid "$RID" --no-squad
        stage_from_release_tree "v${FROM_VERSION}"
        ;;
    *)
        log "staging 'from' binaries built at ${FROM}"
        FROM_VERSION="0.0.1-loopfrom"
        "${REPO_ROOT}/scripts/release-local.sh" \
            --version "$FROM_VERSION" --out "$RELEASE_TREE" --rid "$RID" \
            --ref "$FROM" --no-squad
        stage_from_release_tree "v${FROM_VERSION}"
        ;;
esac

FROM_REPORTED="$("${BIN}/kyber-weave" --version 2>&1 || true)"
log "from: ${FROM_REPORTED}  ->  to: kyber-weave ${TO_VERSION}"

if [ "$FROM_REPORTED" = "kyber-weave ${TO_VERSION}" ]; then
    die "'from' and 'to' are both ${TO_VERSION}; update would short-circuit. Pass --to a different version."
fi

# ------------------------------------------------------------------- serve local

log "starting the loopback release server"
SERVER_OUT="${SANDBOX}/server.port"
python3 "${REPO_ROOT}/scripts/local-release-server.py" --root "$RELEASE_TREE" --port 0 \
    > "$SERVER_OUT" 2> "${SANDBOX}/server.log" &
SERVER_PID=$!

PORT=""
for _ in $(seq 1 50); do
    PORT="$(head -n 1 "$SERVER_OUT" 2>/dev/null || true)"
    [ -n "$PORT" ] && break
    sleep 0.1
done
[ -n "$PORT" ] || { cat "${SANDBOX}/server.log" >&2; die "the release server did not start"; }

ORIGIN="http://127.0.0.1:${PORT}"
export KYBER_WEAVE_RELEASE_ORIGIN="$ORIGIN"
log "serving ${RELEASE_TREE} at ${ORIGIN}"

# ------------------------------------------------------------------ self-update

log "running: kyber-weave update ${TO_VERSION}"
set +e
UPDATE_OUT="$("${BIN}/kyber-weave" update "$TO_VERSION" 2>&1)"
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

# ----------------------------------------------------------------------- verdict

echo >&2
if [ "$FAILURES" -eq 0 ]; then
    printf '\033[32mupdate-loop: all checks passed\033[0m\n' >&2
    exit 0
fi

printf '\033[31mupdate-loop: %s check(s) failed\033[0m\n' "$FAILURES" >&2
exit 1
