#!/usr/bin/env bash
# Creates or updates one GitHub pull request with the GitHub CLI, then reads it back and fails
# if the title or body did not land. Mechanics only: the create-pull-request skill decides the
# target branch, title, and body; this script never derives them from the branch name.
#
# Usage:
#   bash github-create-pr.sh --base <branch> --title <text> --body-file <file>
#                            [--head <branch>] [--repo <owner/name>] [--draft]
#
# --head defaults to the current branch; --repo defaults to the github.com origin remote.
# --draft applies only when a new pull request is created; an existing one keeps its state.
set -euo pipefail

usage() {
  echo "Usage: $0 --base <branch> --title <text> --body-file <file> [--head <branch>] [--repo <owner/name>] [--draft]" >&2
  exit 2
}

HEAD_BRANCH=""
BASE_BRANCH=""
TITLE=""
BODY_FILE=""
REPO=""
DRAFT=false

while [ $# -gt 0 ]; do
  case "$1" in
    --head) [ $# -ge 2 ] || usage; HEAD_BRANCH="$2"; shift 2 ;;
    --base) [ $# -ge 2 ] || usage; BASE_BRANCH="$2"; shift 2 ;;
    --title) [ $# -ge 2 ] || usage; TITLE="$2"; shift 2 ;;
    --body-file) [ $# -ge 2 ] || usage; BODY_FILE="$2"; shift 2 ;;
    --repo) [ $# -ge 2 ] || usage; REPO="$2"; shift 2 ;;
    --draft) DRAFT=true; shift ;;
    -h|--help) usage ;;
    *) echo "Error: unknown argument '$1'." >&2; usage ;;
  esac
done

if [ -z "${BASE_BRANCH}" ] || [ -z "${TITLE}" ] || [ -z "${BODY_FILE}" ]; then
  echo "Error: --base, --title, and --body-file are required; the skill resolves them before calling this script." >&2
  usage
fi

if [ ! -f "${BODY_FILE}" ]; then
  echo "Error: body file '${BODY_FILE}' does not exist." >&2
  exit 1
fi

if [ -z "${HEAD_BRANCH}" ]; then
  HEAD_BRANCH="$(git branch --show-current)"
  if [ -z "${HEAD_BRANCH}" ]; then
    echo "Error: no current branch (detached HEAD); pass --head." >&2
    exit 1
  fi
fi

if [ -z "${REPO}" ]; then
  REMOTE_URL="$(git remote get-url origin 2>/dev/null || true)"
  if [[ "${REMOTE_URL}" =~ ^(git@github\.com:|https?://([^/@]+@)?github\.com/|ssh://git@github\.com/)([^/]+)/([^/]+)$ ]]; then
    REPO="${BASH_REMATCH[3]}/${BASH_REMATCH[4]%.git}"
  else
    echo "Error: origin '${REMOTE_URL}' is not a github.com remote; pass --repo <owner/name>." >&2
    exit 1
  fi
fi

find_open_pr() {
  gh pr list --repo "${REPO}" --head "${HEAD_BRANCH}" --base "${BASE_BRANCH}" --state open \
    --json number --jq '.[0].number // empty'
}

NUMBER="$(find_open_pr)"
if [ -n "${NUMBER}" ]; then
  gh pr edit "${NUMBER}" --repo "${REPO}" --title "${TITLE}" --body-file "${BODY_FILE}"
else
  CREATE_ARGS=(--repo "${REPO}" --head "${HEAD_BRANCH}" --base "${BASE_BRANCH}" --title "${TITLE}" --body-file "${BODY_FILE}")
  if [ "${DRAFT}" = true ]; then
    CREATE_ARGS+=(--draft)
  fi
  gh pr create "${CREATE_ARGS[@]}"
  NUMBER="$(find_open_pr)"
  if [ -z "${NUMBER}" ]; then
    echo "Error: the pull request from '${HEAD_BRANCH}' into '${BASE_BRANCH}' was not found after creation." >&2
    exit 1
  fi
fi

# GitHub may store line endings differently and trims trailing newlines, so compare without
# carriage returns; command substitution drops trailing newlines on both sides.
ACTUAL_TITLE="$(gh pr view "${NUMBER}" --repo "${REPO}" --json title --jq .title)"
ACTUAL_BODY="$(gh pr view "${NUMBER}" --repo "${REPO}" --json body --jq .body | tr -d '\r')"
EXPECTED_BODY="$(tr -d '\r' < "${BODY_FILE}")"

if [ "${ACTUAL_TITLE}" != "${TITLE}" ]; then
  echo "Error: pull request #${NUMBER} title is '${ACTUAL_TITLE}', expected '${TITLE}'." >&2
  exit 1
fi

if [ "${ACTUAL_BODY}" != "${EXPECTED_BODY}" ]; then
  echo "Error: pull request #${NUMBER} body does not match '${BODY_FILE}'." >&2
  exit 1
fi

gh pr view "${NUMBER}" --repo "${REPO}" --json url --jq .url
