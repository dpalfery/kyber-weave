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
  REMOTE_URL=$(git remote get-url origin || echo "")
  if [[ $REMOTE_URL =~ github\.com[:/]([^/]+)/([^.]+)(\.git)? ]]; then
    [ -z "${OWNER}" ] && OWNER="${BASH_REMATCH[1]}"
    [ -z "${REPO}" ] && REPO="${BASH_REMATCH[2]}"
  fi
fi

if [ -z "${OWNER}" ] || [ -z "${REPO}" ]; then
  echo "Error: Owner and Repo could not be auto-detected and were not provided."
  exit 1
fi

git fetch --prune --no-tags origin

COMMITS=$(git log --no-merges --pretty=format:"- [%h] %s (%an)" origin/${TB}..${SB})
FILES=$(git diff --name-only origin/${TB}..${SB} | sed -n '1,20p')
SUMMARY=$(git log --no-merges --pretty=format:"%s" -n1 origin/${TB}..${SB})

# Clean title from branch name
BRANCH_STEM="${SB##*/}"
TITLE=$(echo "${BRANCH_STEM}" | sed -E 's/[-_/]+/ /g' | awk '{for(i=1;i<=NF;i++)sub(/./,toupper(substr($i,1,1)),$i)}1')

DESCRIPTION_FILE=$(mktemp)
cat > "${DESCRIPTION_FILE}" <<EOF
## Summary

${SUMMARY}

## Type of Change

- [ ] Bug fix (non-breaking change which fixes an issue)
- [ ] New feature (non-breaking change which adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Refactor (code change that neither fixes a bug nor adds a feature)

## Proposed Changes

${COMMITS}

### Files Changed:
${FILES}
EOF

# Check for existing PR
EXISTING=$(gh pr list --repo "${OWNER}/${REPO}" --head "${SB}" --base "${TB}" --state open --json number --jq '.[0].number // empty')
if [ -n "${EXISTING}" ]; then
  gh pr edit "${EXISTING}" --repo "${OWNER}/${REPO}" --title "${TITLE}" --body-file "${DESCRIPTION_FILE}"
else
  gh pr create --repo "${OWNER}/${REPO}" --head "${SB}" --base "${TB}" --title "${TITLE}" --body-file "${DESCRIPTION_FILE}"
fi

rm -f "${DESCRIPTION_FILE}"
