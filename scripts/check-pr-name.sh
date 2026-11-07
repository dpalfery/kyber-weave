#!/usr/bin/env bash
# check-pr-name.sh - hold a pull request's branch and title to the shape that names its harness.
#
# A pull request's branch and title are what a maintainer reads first, and they are the only
# record of which harness produced the change: the diff says nothing about whether a `claude`
# run or a `commandcode` run wrote it. This check enforces the shape agreed for that (decision
# D42), so the harness is readable in both places. CI runs it as its own job (`pr-naming` in
# .github/workflows/ci.yml), and .github/workflows/pr-naming-recheck.yml re-runs that job when
# a title is edited.
#
# Usage:
#   scripts/check-pr-name.sh --title <title> --branch <branch> [--author <login>]
#
# Shapes:
#   branch  [<owner>.<agent>.]<harness>/<type>/<slug>
#           commandcode/fix/arbiter-global-block-identity
#           dave.test-dev.commandcode/fix/arbiter-global-block-identity
#   title   <type>(<scope>): <summary> [<harness>]
#           fix(arbiter): qualify global block identity by target [commandcode]
#
# <type> is a conventional-commit type, <slug> is lowercase hyphen-separated words, <scope> is
# one word, and <harness> is a catalogue token or `manual` for work named after no harness.
# Everything matches case-sensitively: a capitalised token is a different token, and the
# catalogue's are lowercase.
#
# A pull request authored by dependabot[bot] is exempt, because GitHub generates its branch and
# title and no human can conform them. The exemption follows the author, not the branch prefix,
# so a human who names a branch `dependabot/...` is still judged.
#
# Exit codes:
#   0  the branch and title conform, or the author is Dependabot
#   1  one or both do not conform; stderr has one line per offending part, quoting it
#   2  usage error: a flag is unknown, has no value, or is missing
#
# The script reads argv only: no stdin, no network, and it starts no process.

set -euo pipefail

readonly usage='usage: check-pr-name.sh --title <title> --branch <branch> [--author <login>]'

# The harness tokens, in the order SquadTargetCatalog.All holds them: one line per
# SquadTargetCatalog.GetToken in src/KyberWeave.Core/Squad/Deployment/SquadTarget.cs, then
# `manual` for a pull request named after no harness. A shell script cannot read the C#
# catalogue, so the thirteen are copied here, and the naming check's own tests build their
# accept rows from SquadTargetCatalog and so fail until this line follows a catalogue change.
readonly harness_tokens='codex cursor claude copilot opencode kilo antigravity warp factory pi zcode devin commandcode manual'

# The conventional-commit types the <type> segment accepts, the same set the commit history uses.
readonly conventional_types='build chore ci docs feat fix perf refactor revert style test'

# The branch shape with no type segment, no harness or no slug is the shape of a branch pushed
# by hand, so the message has to state the shape rather than only reject.
readonly branch_shape='[<owner>.<agent>.]<harness>/<type>/<slug>'
readonly title_shape='<type>(<scope>): <summary> [<harness>]'

rejected=0

# reject <message> - record one offending part, on stderr so a caller's stdout stays parseable.
reject() {
  printf 'check-pr-name: %s\n' "$1" >&2
  rejected=1
}

die_usage() {
  printf 'check-pr-name: %s\n%s\n' "$1" "${usage}" >&2
  exit 2
}

# in_list <value> <space-separated-list> - word splitting the list is the point: every word in
# it is one token, and no token contains a space. IFS is set here rather than inherited, because
# a caller that splits on '/' or '.' for its own parsing would otherwise split this list the
# same way and match nothing.
in_list() {
  local IFS=$' \t\n'
  local item
  for item in $2; do
    if [ "${item}" = "$1" ]; then
      return 0
    fi
  done
  return 1
}

# check_branch - the branch must name its harness, a conventional type and a readable slug.
check_branch() {
  local IFS='/'
  local -a segments
  read -r -a segments <<<"${branch}"

  if [ "${#segments[@]}" -ne 3 ]; then
    reject "the branch '${branch}' has ${#segments[@]} slash-separated segment(s) where the shape is '${branch_shape}', for example 'commandcode/fix/arbiter-global-block-identity'"
    return 0
  fi

  local head="${segments[0]}" type="${segments[1]}" slug="${segments[2]}"
  # The harness is the last dotted component of the first segment: '<harness>' alone, or
  # '<owner>.<agent>.<harness>' with the optional prefix in front of it.
  local tag="${head##*.}"

  if [ "${head}" != "${tag}" ]; then
    local -a prefix
    IFS='.' read -r -a prefix <<<"${head}"
    if [ "${#prefix[@]}" -ne 3 ]; then
      reject "the branch '${branch}' carries '${head}' before the first separator; the prefix is '<owner>.<agent>.' in front of the harness token, so the first segment is '<harness>' or '<owner>.<agent>.<harness>'"
      return 0
    fi
    if [[ ! "${prefix[0]}" =~ ^[A-Za-z0-9][A-Za-z0-9-]*$ || ! "${prefix[1]}" =~ ^[A-Za-z0-9][A-Za-z0-9-]*$ ]]; then
      reject "the branch '${branch}' has an owner or agent component in '${head}' that is not a login name (letters, digits and hyphens, starting with a letter or digit)"
      return 0
    fi
  fi

  if ! in_list "${tag}" "${harness_tokens}"; then
    reject "the branch '${branch}' names the harness '${tag}', which is not a catalogue token; use one of: ${harness_tokens}"
  fi

  if ! in_list "${type}" "${conventional_types}"; then
    reject "the branch '${branch}' has the type '${type}', which is not a conventional-commit type; use one of: ${conventional_types}"
  fi

  if [[ ! "${slug}" =~ ^[a-z0-9]+(-[a-z0-9]+)*$ ]]; then
    reject "the branch '${branch}' has the slug '${slug}', which is not lowercase words separated by single hyphens, for example 'arbiter-global-block-identity'"
  fi

  return 0
}

# check_title - the title must carry the harness token in square brackets at the end, after a
# conventional-commit subject.
check_title() {
  local shape='^([a-z]+)\(([^()]+)\): (.+) \[([a-z0-9]+)\]$'

  if [[ ! "${title}" =~ ${shape} ]]; then
    reject "the title '${title}' does not match '${title_shape}', for example 'fix(arbiter): qualify global block identity by target [commandcode]'"
    return 0
  fi

  local type="${BASH_REMATCH[1]}" scope="${BASH_REMATCH[2]}"
  local summary="${BASH_REMATCH[3]}" tag="${BASH_REMATCH[4]}"

  # A second bracket group would let a title carry two harnesses, and the trailing one would be
  # the only one read. The summary therefore excludes both brackets, which the shape above
  # cannot express on its own.
  if [[ "${summary}" == *'['* || "${summary}" == *']'* ]]; then
    reject "the title '${title}' carries more than one square-bracket group; the harness token belongs in square brackets at the end, once"
    return 0
  fi

  if ! in_list "${type}" "${conventional_types}"; then
    reject "the title '${title}' has the type '${type}', which is not a conventional-commit type; use one of: ${conventional_types}"
  fi

  if [[ ! "${scope}" =~ ^[^[:space:][:cntrl:]]+$ ]]; then
    reject "the title '${title}' has the scope '${scope}', which is not one word; write 'fix(arbiter): ...', not 'fix(my scope): ...'"
  fi

  if ! in_list "${tag}" "${harness_tokens}"; then
    reject "the title '${title}' names the harness '${tag}', which is not a catalogue token; use one of: ${harness_tokens}"
  fi

  return 0
}

title=''
branch=''
author=''
have_title=0
have_branch=0

while [ "$#" -gt 0 ]; do
  case "$1" in
    --title)
      [ "$#" -ge 2 ] || die_usage '--title needs a value'
      title="$2"
      have_title=1
      shift 2
      ;;
    --branch)
      [ "$#" -ge 2 ] || die_usage '--branch needs a value'
      branch="$2"
      have_branch=1
      shift 2
      ;;
    --author)
      [ "$#" -ge 2 ] || die_usage '--author needs a value'
      author="$2"
      shift 2
      ;;
    -h | --help)
      printf '%s\n' "${usage}"
      exit 0
      ;;
    *)
      die_usage "unknown argument '$1'"
      ;;
  esac
done

[ "${have_title}" -eq 1 ] || die_usage '--title is required'
[ "${have_branch}" -eq 1 ] || die_usage '--branch is required'

# GitHub's Dependabot login, compared without case because GitHub logins are case-insensitive.
# An absent author is not Dependabot: a run that passes no author is judged like any other.
if [ "$(printf '%s' "${author}" | tr '[:upper:]' '[:lower:]')" = 'dependabot[bot]' ]; then
  printf "check-pr-name: '%s' is Dependabot, whose branch and title GitHub generates; exempt.\n" "${author}"
  exit 0
fi

check_branch
check_title

if [ "${rejected}" -ne 0 ]; then
  exit 1
fi

printf 'check-pr-name: the branch and title conform to the naming rule.\n'
