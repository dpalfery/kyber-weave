#!/usr/bin/env bash
# Computes the next Kyber-Weave release version from existing v* tags.
#
#   scripts/next-release-version.sh           # next RC (default)
#   scripts/next-release-version.sh --prod    # next stable
#   scripts/next-release-version.sh --notes-base --for <released-tag>
#   scripts/next-release-version.sh --self-test
#
# Prints `VERSION=` and `TAG=` on stdout for `eval`. Diagnostics go to stderr.
# --notes-base --for <tag> prints only `PREVIOUS_TAG=<tag>` (empty when there is no
# base): the tag the release notes for <tag> compare against. <tag> is the tag being
# released (the TAG this script printed), so the base is always lower than it. A
# stable <tag> uses the highest lower stable tag (spanning every RC); an RC <tag>
# uses the highest lower stable or RC tag. The major-version guard is not applied
# to those lower candidates, so v1.0.0-rc.2 compares with v1.0.0-rc.1, while a
# mistyped higher-major tag is above <tag> and can never be the base.
#
# Dispatch never asks for a version. Unchecked "make prod release" increments
# only the RC number on the current candidate line (0.1.7-rc.9 → 0.1.7-rc.10;
# 0.1.6 with no RC → 0.1.7-rc.1). Checked, it promotes that line to stable
# (0.1.7-rc.9 → 0.1.7) or, when the highest tag is already stable, bumps patch
# (0.1.7 → 0.1.8). Minor and major cuts stay a tag push.
#
# Comparison is SemVer 2.0.0 precedence — the same awk rules as
# kyber_weave_semver_compare in scripts/install.sh. Ordinal string order would
# sort rc.10 below rc.9. Tags whose major is above the highest stable major are
# ignored so a mistype like v1.0.6-rc.6 cannot steal the next number. -dev.*
# and other non-rc prereleases are ignored.

set -euo pipefail

die() { printf 'next-release-version: error: %s\n' "$1" >&2; exit 1; }
log() { printf 'next-release-version: %s\n' "$1" >&2; }

# Must stay in lockstep with kyber_weave_semver_compare in scripts/install.sh.
semver_compare() {
    awk -v a="$1" -v b="$2" '
        function cmpnum(x, y) { return (x < y) ? -1 : ((x > y) ? 1 : 0) }
        function core(v) {
            sub(/^[vV]/, "", v)
            sub(/\+.*$/, "", v)
            sub(/-.*$/, "", v)
            return v
        }
        function pre(v) {
            sub(/^[vV]/, "", v)
            sub(/\+.*$/, "", v)
            if (v !~ /-/) return ""
            sub(/^[^-]*-/, "", v)
            return v
        }
        function cmpcore(x, y,   xa, ya, nx, ny, n, i, xv, yv) {
            nx = split(x, xa, ".")
            ny = split(y, ya, ".")
            n = (nx > ny) ? nx : ny
            for (i = 1; i <= n; i++) {
                xv = (i <= nx) ? xa[i] + 0 : 0
                yv = (i <= ny) ? ya[i] + 0 : 0
                if (xv != yv) return cmpnum(xv, yv)
            }
            return 0
        }
        function cmppre(x, y,   xa, ya, nx, ny, n, i, xv, yv, xn, yn) {
            if (x == "" && y == "") return 0
            if (x == "") return 1
            if (y == "") return -1
            nx = split(x, xa, ".")
            ny = split(y, ya, ".")
            n = (nx < ny) ? nx : ny
            for (i = 1; i <= n; i++) {
                xv = xa[i]
                yv = ya[i]
                xn = (xv ~ /^[0-9]+$/)
                yn = (yv ~ /^[0-9]+$/)
                if (xn && yn) {
                    if (xv + 0 != yv + 0) return cmpnum(xv + 0, yv + 0)
                } else if (xn) {
                    return -1
                } else if (yn) {
                    return 1
                } else if (xv != yv) {
                    return (xv < yv) ? -1 : 1
                }
            }
            if (nx != ny) return cmpnum(nx, ny)
            return 0
        }
        BEGIN {
            r = cmpcore(core(a), core(b))
            if (r == 0) r = cmppre(pre(a), pre(b))
            print r
        }'
}

normalize() {
    raw="${1#v}"
    raw="${raw#V}"
    printf '%s\n' "$raw"
}

is_stable() {
    case "$1" in
        *.*) [ "$1" = "${1%%-*}" ] || return 1
            case "$1" in
                [0-9]*.[0-9]*.[0-9]*) return 0 ;;
                *) return 1 ;;
            esac
            ;;
        *) return 1 ;;
    esac
}

is_rc() {
    case "$1" in
        [0-9]*.[0-9]*.[0-9]*-rc.[0-9]*) return 0 ;;
        *) return 1 ;;
    esac
}

major_of() {
    printf '%s\n' "${1%%.*}"
}

bump_patch() {
    core="$1"
    maj="${core%%.*}"
    rest="${core#*.}"
    min="${rest%%.*}"
    pat="${rest#*.}"
    printf '%s.%s.%s\n' "$maj" "$min" "$((pat + 1))"
}

collect_tags() {
    if [ "$#" -gt 0 ]; then
        printf '%s\n' "$@"
        return
    fi
    # Origin only. The shared object store can carry local desktop tags
    # (v0.9.*) that must not choose the next Kyber-Weave number.
    if git remote get-url origin >/dev/null 2>&1
    then
        git ls-remote --tags origin 'refs/tags/v*' \
            | awk '{print $2}' \
            | sed 's|^refs/tags/||' \
            | grep -v '\^{}$' || true
        return
    fi
    git tag -l 'v*'
}

choose_next() {
    prod="$1"
    shift

    allowed=""
    max_stable=""
    while IFS= read -r tag
    do
        [ -n "$tag" ] || continue
        ver="$(normalize "$tag")"
        if is_stable "$ver"
        then
            if [ -z "$max_stable" ] || [ "$(semver_compare "$ver" "$max_stable")" -gt 0 ]
            then
                max_stable="$ver"
            fi
        elif ! is_rc "$ver"
        then
            continue
        fi
        allowed="${allowed}${ver}"$'\n'
    done <<EOF
$(collect_tags "$@")
EOF

    filtered=""
    while IFS= read -r ver
    do
        [ -n "$ver" ] || continue
        if [ -n "$max_stable" ] && [ "$(major_of "$ver")" -gt "$(major_of "$max_stable")" ]
        then
            log "ignoring ${ver}: major is above stable ${max_stable}"
            continue
        fi
        filtered="${filtered}${ver}"$'\n'
    done <<EOF
${allowed}
EOF

    highest=""
    while IFS= read -r ver
    do
        [ -n "$ver" ] || continue
        if [ -z "$highest" ] || [ "$(semver_compare "$ver" "$highest")" -gt 0 ]
        then
            highest="$ver"
        fi
    done <<EOF
${filtered}
EOF

    [ -n "$highest" ] || die "no stable or -rc.N tags found to increment"

    if [ "$prod" = 1 ]
    then
        if is_rc "$highest"
        then
            next="${highest%-rc.*}"
        else
            next="$(bump_patch "$highest")"
        fi
    else
        if is_rc "$highest"
        then
            n="${highest##*-rc.}"
            core="${highest%-rc.*}"
            next="${core}-rc.$((n + 1))"
        else
            next="$(bump_patch "$highest")-rc.1"
        fi
    fi

    printf 'VERSION=%s\n' "$next"
    printf 'TAG=v%s\n' "$next"
    log "highest=${highest} prod=${prod} -> ${next}"
}

# Prints PREVIOUS_TAG=<tag> for the release tag $1, drawn from the tags in $2.. (or
# origin / local tags when none are given).
notes_base_for() {
    target="$(normalize "$1")"
    shift
    if is_stable "$target"
    then
        want_stable=1
    elif is_rc "$target"
    then
        want_stable=0
    else
        die "--for '${target}' is not a stable or -rc.N version"
    fi

    base=""
    while IFS= read -r tag
    do
        [ -n "$tag" ] || continue
        ver="$(normalize "$tag")"
        if is_stable "$ver"
        then
            :
        elif [ "$want_stable" = 0 ] && is_rc "$ver"
        then
            :
        else
            continue
        fi
        [ "$(semver_compare "$ver" "$target")" -lt 0 ] || continue
        if [ -z "$base" ] || [ "$(semver_compare "$ver" "$base")" -gt 0 ]
        then
            base="$ver"
        fi
    done <<EOF
$(collect_tags "$@")
EOF

    if [ -n "$base" ]
    then
        printf 'PREVIOUS_TAG=v%s\n' "$base"
    else
        printf 'PREVIOUS_TAG=\n'
    fi
    log "notes-base for=${target} -> ${base:-none}"
}

expect_next() {
    label="$1"
    prod="$2"
    want="$3"
    shift 3
    got="$(choose_next "$prod" "$@" | awk -F= '/^VERSION=/{print $2}')"
    [ "$got" = "$want" ] || die "${label}: expected ${want}, got ${got}"
}

expect_base() {
    label="$1"
    target="$2"
    want="$3"
    shift 3
    got="$(notes_base_for "$target" "$@" | awk -F= '/^PREVIOUS_TAG=/{print $2}')"
    [ "$got" = "$want" ] || die "${label}: expected base '${want}', got '${got}'"
}

self_test() {
    expect_next "rc after rc.9" 0 "0.1.7-rc.10" v0.1.6 v0.1.7-rc.9
    expect_next "prod promotes rc" 1 "0.1.7" v0.1.6 v0.1.7-rc.9
    expect_next "rc after stable" 0 "0.1.7-rc.1" v0.1.6
    expect_next "prod after stable" 1 "0.1.7" v0.1.6
    expect_next "rc after promoted" 0 "0.1.8-rc.1" v0.1.6 v0.1.7-rc.9 v0.1.7
    expect_next "rc.10 beats rc.9" 0 "0.1.7-rc.11" v0.1.7-rc.9 v0.1.7-rc.10
    expect_next "ignores mistyped major" 0 "0.1.7-rc.10" v0.1.6 v0.1.7-rc.9 v1.0.6-rc.6
    expect_next "ignores -dev" 0 "0.1.7-rc.10" v0.1.6 v0.1.7-rc.9 v0.1.8-dev.1
    expect_base "rc after stable" v0.1.7-rc.1 "v0.1.6" v0.1.6
    expect_base "later rc" v0.1.7-rc.3 "v0.1.7-rc.2" v0.1.6 v0.1.7-rc.1 v0.1.7-rc.2
    expect_base "stable spans rcs" v0.1.7 "v0.1.6" v0.1.6 v0.1.7-rc.1 v0.1.7-rc.3
    expect_base "stable without prior stable" v0.1.0 "" v0.1.0-rc.1
    expect_base "ignores -dev" v0.1.7-rc.1 "v0.1.6" v0.1.6 v0.1.7-dev.3
    expect_base "widened major" v1.0.0-rc.2 "v1.0.0-rc.1" v0.9.5 v1.0.0-rc.1
    expect_base "mistyped major is never the base" v0.1.7-rc.10 "v0.1.7-rc.9" v0.1.6 v0.1.7-rc.9 v1.0.6-rc.6
    expect_base "never its own tag" v0.1.7-rc.9 "v0.1.6" v0.1.6 v0.1.7-rc.9
    log "self-test passed"
}

PROD=0
NOTES_BASE=0
FOR_TAG=""
MODE=run
while [ $# -gt 0 ]; do
    case "$1" in
        --prod) PROD=1; shift ;;
        --notes-base) NOTES_BASE=1; shift ;;
        --for)
            [ $# -ge 2 ] && [ -n "$2" ] || die "--for needs the tag being released"
            FOR_TAG="$2"; shift 2 ;;
        --self-test) MODE=self-test; shift ;;
        -h|--help) sed -n '2,29p' "$0" >&2; exit 0 ;;
        *) die "unknown option: $1" ;;
    esac
done

if [ "$MODE" = "self-test" ]
then
    self_test
    exit 0
fi

if [ "$NOTES_BASE" = 1 ]
then
    [ -n "$FOR_TAG" ] || die "--notes-base needs --for <released-tag>"
    notes_base_for "$FOR_TAG"
    exit 0
fi
[ -z "$FOR_TAG" ] || die "--for is only valid with --notes-base"

choose_next "$PROD"
