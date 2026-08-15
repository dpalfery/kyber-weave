#!/usr/bin/env bash
set -euo pipefail

SB="${1:-$(git branch --show-current)}"
TB="${2:-}"
OWNER="${3:-}"
REPO="${4:-}"

if [ -z "${TB}" ]; then
  echo "Error: Target branch (TB) is required as the second argument."
  exit 1
fi

# Auto-detect owner and repo if not provided
if [ -z "${OWNER}" ] || [ -z "${REPO}" ]; then
  REMOTE_URL=$(git remote get-url origin 2>/dev/null || echo "")
  if [[ $REMOTE_URL =~ github\.com[:/]([^/]+)/([^/]+) ]]; then
    [ -z "${OWNER}" ] && OWNER="${BASH_REMATCH[1]}"
    [ -z "${REPO}" ] && REPO="${BASH_REMATCH[2]%.git}"
  fi
fi

if [ -z "${OWNER}" ] || [ -z "${REPO}" ]; then
  echo "Error: Owner and Repo could not be auto-detected and were not provided."
  exit 1
fi

git fetch --prune --no-tags origin

SUMMARY=$(git log --no-merges --pretty=format:"%s" -n1 origin/${TB}..${SB})

# Clean title from branch name
if [[ "${SB}" =~ ^(feature|bugfix|hotfix|chore|task)/(.*)$ ]]; then
  BRANCH_STEM="${BASH_REMATCH[2]}"
else
  BRANCH_STEM="${SB}"
fi
TITLE=$(echo "${BRANCH_STEM}" | sed -E 's/[-_/]+/ /g' | awk '{for(i=1;i<=NF;i++)sub(/./,toupper(substr($i,1,1)),$i)}1')

TICKET=""
if [[ "${BRANCH_STEM}" =~ ^([0-9]+)([-_/]|$) ]]; then
  TICKET="#${BASH_REMATCH[1]}"
elif [[ "${BRANCH_STEM}" =~ ([A-Za-z]+-[0-9]+) ]]; then
  TICKET="${BASH_REMATCH[1]}"
fi

RELATED_ISSUE=""
if [ -n "${TICKET}" ]; then
  DEFAULT_BRANCH=$(gh repo view "${OWNER}/${REPO}" --json defaultBranchRef --jq '.defaultBranchRef.name' 2>/dev/null || echo "main")
  if [ "${TB}" = "${DEFAULT_BRANCH}" ] && [[ "${TICKET}" =~ ^#[0-9]+$ ]]; then
    DIRECTIVE="Closes ${TICKET}"
  else
    DIRECTIVE="Relates to ${TICKET}"
  fi
  RELATED_ISSUE=$(cat <<EOF

## Related Issue / Ticket

${DIRECTIVE}
EOF
)
fi

DESCRIPTION_FILE=$(mktemp)
trap 'rm -f "${DESCRIPTION_FILE}"' EXIT

cat > "${DESCRIPTION_FILE}" <<EOF
## Summary

${SUMMARY}

## Type of Change

- [ ] Bug fix (non-breaking change which fixes an issue)
- [ ] New feature (non-breaking change which adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Refactor (code change that neither fixes a bug nor adds a feature)${RELATED_ISSUE}

## Proposed Changes

- Summarized implementation details...
EOF

# Check for existing PR
EXISTING=$(gh pr list --repo "${OWNER}/${REPO}" --head "${SB}" --base "${TB}" --state open --json number --jq '.[0].number // empty')
if [ -n "${EXISTING}" ]; then
  gh pr edit "${EXISTING}" --repo "${OWNER}/${REPO}" --title "${TITLE}" --body-file "${DESCRIPTION_FILE}"
else
  gh pr create --repo "${OWNER}/${REPO}" --head "${SB}" --base "${TB}" --title "${TITLE}" --body-file "${DESCRIPTION_FILE}"
fi
