---
name: create-pull-request-github
description: "Use when asked to submit or create a Pull Request or PR on GitHub"
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

> **Note:** This skill is named `create-pull-request-github`. If you are looking for the original `create-pull-request` skill, this file is the GitHub-specific replacement.

# Create Pull Request (GitHub)

## Requirements

- GitHub MCP server is the preferred path when available.
  - Configured in `.kilo/kilo.json` as server name `github`, using `ghcr.io/github/github-mcp-server`.
  - **Read-only mode caveat:** The current MCP configuration sets `GITHUB_READ_ONLY=1`, which disables write operations (create/update PR). If write MCP tools are unavailable, fall back to the `gh` CLI.
- `gh` CLI (`gh`) installed and authenticated (`gh auth login`).
- Local git repo checked out and up-to-date; remote branch pushed.

Note: The agent will auto-detect `owner` and `repo` from the current git remote configuration. Only provide these as explicit inputs if you want to override the detected values. `sourceBranch` defaults to the current branch. `targetBranch` must be provided explicitly or resolved from conclusive evidence about the current branch's parent branch; do not assume the repository default branch.

## Inputs

- `sourceBranch` (string, optional) — the branch to open the PR from. Defaults to the current branch if not specified.
- `targetBranch` (string, optional) — the target branch to merge into. If not specified, the skill must resolve the parent branch from conclusive evidence or ask the user to choose one.
- `title` (string, optional) — override for the PR title. If omitted, the skill will generate a clean title from the branch name.
- `extraNotes` (string, optional) — freeform notes to append to the PR description.
- `owner` (string, optional) — override for repository owner (user or organization). Auto-detected from git remote if not provided.
- `repo` (string, optional) — override for repository name. Auto-detected from git remote if not provided.

## Outputs

- A markdown-formatted PR description and either the MCP operation result or the CLI invocation that was executed.

## Behavior / Implementation Guidance

### 1. Title generation

- If `title` not provided, generate it from `sourceBranch` by stripping prefixes (`feature/`, `bugfix/`, `hotfix/`, `task/`), splitting on `/`, `-`, and `_`, and title-casing non-numeric tokens.
- Preserve leading numeric issue IDs from the branch name. Example: `feature/42-audit-error-service-build-template` -> `42 Audit Error Service Build Template`.
- Do not use fragile regex replacements that can drop the first character of the branch stem.

### 2. Resolve the target or parent branch before building the PR

- If `targetBranch` was provided by the user, use it.
- Otherwise, determine the parent branch from conclusive evidence only.
- Conclusive evidence may include:
  - the user explicitly naming the parent branch earlier in the conversation
  - a validated team or repo rule already documented for the current branch type
  - a clearly established branch relationship from local git state, remote tracking configuration, or an existing PR pattern for the same branch
- Never assume the repository default branch is the PR target without conclusive evidence that it is also the parent branch of the current branch.
- If the parent branch cannot be determined conclusively, list the long-lived branches for the repository and ask the user to choose one or provide a different target branch.
- Long-lived branches typically include branches such as `main`, `master`, `dev`, `develop`, and active `release/*` branches, but the actual list should come from the repository.
- Use `gh api` or `git branch -r` to list long-lived branches when parent branch is unknown.

### 3. Gather context for description

- Use git to collect commits and changes between `targetBranch` and `sourceBranch` ONLY to inform the "Summary" and "Proposed Changes" sections.
- Do NOT include raw lists of commits or file paths in the final PR description, as these are natively provided by the GitHub UI.
- Command for context: `git log --no-merges --pretty=format:"%s" origin/${targetBranch}..${sourceBranch}`

### 4. Build the description (Markdown sections)

- **Summary / TL;DR**: A high-level overview (1–3 sentences) of the change.
  - **Goal**: Explain the "Why" more than the "How." (e.g., "Implements a caching layer for the Denver Airport parking API to reduce latency and stay within rate limits.")
- **Type of Change**: A checklist to help the reviewer set their mindset:
  - [ ] Bug fix (non-breaking change which fixes an issue)
  - [ ] New feature (non-breaking change which adds functionality)
  - [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
  - [ ] Refactor (code change that neither fixes a bug nor adds a feature)
- **Related Issue / Ticket**: A direct link to the tracking item (GitHub Issue, Jira, etc.).
  - Prefer deriving the ticket from the branch name when possible.
  - If the branch contains a leading numeric GitHub issue ID, format it as `Closes #42`.
  - If the branch contains an alphanumeric issue key such as `ABC-123`, preserve that format.
- **Proposed Changes**: A bulleted list of the technical implementation details summarized from commit messages and file changes. Keep it short but complete.
- **How Has This Been Tested?**: Documentation of verification steps:
  - **Unit Tests**: Mention new test coverage or updated suites.
  - **Manual Verification**: List the steps taken.
  - **Environments**: Specify if it was tested in a Dev container, a local VNet-integrated environment, etc.
- **Notes**: Include `extraNotes` if provided.

### 5. Prefer GitHub MCP first

- First check whether the GitHub MCP tools are available and healthy by performing a lightweight read such as getting the current user profile or listing repositories.
- If the GitHub MCP server is available, healthy, and NOT in read-only mode, use it as the first choice for PR operations.
- Use MCP to:
  - list existing PRs for the same source and target branch (`list_pull_requests`)
  - validate an existing PR by reading it back (`pull_request_read`)
- Note: In this repo the GitHub MCP server is configured read-only (`GITHUB_READ_ONLY=1`), so PR create/update must use the `gh` CLI fallback.
- MCP tool names:
  - `list_pull_requests` — list PRs with optional filters (base, head, state)
  - `pull_request_read` — get PR details, diff, status, files, commits, reviews, comments

### 6. If GitHub MCP is not available, not installed, or unhealthy

- Fall back to the GitHub CLI (`gh`) flow below.
- If the MCP server is installed but fails, check that `GITHUB_PERSONAL_ACCESS_TOKEN` is set and the server is not in read-only mode.

### 7. Create or update the PR via GitHub CLI fallback

- Write the final markdown description to a temporary UTF-8 file first.
- If `targetBranch` is not provided and the parent branch is not conclusively known, list long-lived branches first and ask the user to choose one before proceeding.
- Check for an existing PR before creating:
  - `gh pr list --repo "${owner}/${repo}" --head "${sourceBranch}" --base "${targetBranch}" --state open --json number`
- If a PR already exists, update it:
  - `gh pr edit <prNumber> --repo "${owner}/${repo}" --title "${title}" --body-file "${descriptionFile}"`
- If no PR exists, create one:
  - `gh pr create --repo "${owner}/${repo}" --head "${sourceBranch}" --base "${targetBranch}" --title "${title}" --body-file "${descriptionFile}"`
- In PowerShell, build `descriptionMarkdown` with a here-string, save it to `descriptionFile`, and pass `--body-file` to the CLI. Do not pass a multi-line description directly as a native command argument, and do not pass a string containing literal `\n` escape sequences.
- After creation or update, the skill will validate that the PR's title and description match the intended values. Use `gh pr view <prNumber> --repo "${owner}/${repo}" --json title,body` for this validation. If validation fails, retry the update or report an error.

### 8. Safety & validation

- Ensure `sourceBranch` exists locally or remotely; fail fast if not found.
- Ensure `targetBranch` is explicit or resolved from conclusive evidence; otherwise ask the user before creating the PR.
- Do not create a test PR in the target repository unless the user asked for one or the workflow explicitly requires an MCP health check.
- Escape/encode the markdown description for the shell environment used.
- Do not include secrets or credentials from the environment in the PR description.

## Example script (POSIX / Bash)

```bash
# fetch
git fetch --prune --no-tags origin
SB=feature/my-change
TB=main # replace with the explicitly chosen or conclusively resolved parent branch
OWNER="my-org" # or auto-detect from git remote
REPO="my-repo" # or auto-detect from git remote

SUMMARY=$(git log --no-merges --pretty=format:"%s" -n1 origin/${TB}..${SB})
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
cat > "${DESCRIPTION_FILE}" <<EOF
## Summary

${SUMMARY}

## Type of Change

- [ ] Bug fix (non-breaking change which fixes an issue)
- [ ] New feature (non-breaking change which adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Refactor (code change that neither fixes a bug nor adds a feature)${RELATED_ISSUE}

## Proposed Changes

Summarized implementation detail here...
EOF

# Check for existing PR
EXISTING=$(gh pr list --repo "${OWNER}/${REPO}" --head "${SB}" --base "${TB}" --state open --json number --jq '.[0].number // empty')
if [ -n "${EXISTING}" ]; then
  gh pr edit "${EXISTING}" --repo "${OWNER}/${REPO}" --title "${TITLE}" --body-file "${DESCRIPTION_FILE}"
else
  gh pr create --repo "${OWNER}/${REPO}" --head "${SB}" --base "${TB}" --title "${TITLE}" --body-file "${DESCRIPTION_FILE}"
fi
```

## Example PowerShell snippet (Windows)

```powershell
git fetch --prune --no-tags origin
$SB = 'feature/my-change'
$TB = 'main' # replace with the explicitly chosen or conclusively resolved parent branch
$owner = 'my-org' # or auto-detect from git remote
$repo = 'my-repo' # or auto-detect from git remote
$summary = git log --no-merges --pretty=format:"%s" -n 1 "origin/$TB..$SB"
$branchStem = $SB -replace '^(feature/|bugfix/|hotfix/|chore/|task/)', ''
$titleParts = ($branchStem -split '[-_/]+' | Where-Object { $_ }) | ForEach-Object {
  if ($_ -match '^\d+$') { $_ }
  else { [System.Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($_.ToLowerInvariant()) }
}
$title = [string]::Join(' ', $titleParts)
if ($branchStem -match '^(\d+)(?:[-_/]|$)') { $ticket = "#$($Matches[1])" }
elseif ($branchStem -match '([A-Z]+-\d+)') { $ticket = $Matches[1] }
else { $ticket = '' }

$relatedIssueSection = if ($ticket) {
    $defaultBranch = (gh repo view "$owner/$repo" --json defaultBranchRef --jq '.defaultBranchRef.name' 2>$null)
    if (-not $defaultBranch) { $defaultBranch = 'main' }
    $directive = if ($TB -eq $defaultBranch -and $ticket -match '^#\d+$') { "Closes $ticket" } else { "Relates to $ticket" }
@"

## Related Issue / Ticket

$directive
"@
}
else {
  ""
}

$description = @"
## Summary

$summary

## Type of Change

- [ ] Bug fix (non-breaking change which fixes an issue)
- [ ] New feature (non-breaking change which adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Refactor (code change that neither fixes a bug nor adds a feature)$relatedIssueSection

## Proposed Changes

Summarized implementation detail here...
"@
$descriptionFile = Join-Path $env:TEMP 'create-pr-description.md'
Set-Content -Path $descriptionFile -Value $description -Encoding utf8

$existing = gh pr list --repo "$owner/$repo" --head $SB --base $TB --state open --json number --jq '.[0].number // empty' 2>$null
if ($existing) {
  gh pr edit $existing --repo "$owner/$repo" --title $title --body-file $descriptionFile
}
else {
  gh pr create --repo "$owner/$repo" --head $SB --base $TB --title $title --body-file $descriptionFile
}
```

## Example prompts to try with this skill

- "Create a PR from `feature/auth-fix` into `main` for repo `my-org/my-repo`."
- "Open a PR from my branch `42/update-readme` — include commit messages and files changed."
- "Use GitHub MCP to open a PR from my current branch, and only fall back to gh CLI if MCP is unavailable."

## Ambiguities & follow-ups (ask user)

- If the parent branch cannot be conclusively determined, which long-lived branch should be the PR target?
- Should the PR be created as a draft?
- Are there specific reviewers or teams to request reviews from?

## Next steps / suggested automation

- Maintain and validate the existing cross-platform helper scripts (`scripts/create-pr.sh` and `scripts/create-pr.ps1`) that implement the command flow above and are callable by the skill.
- Optionally add parsing for issue IDs in branch names and auto-link those issues in the PR (e.g., `Closes #42`).

## Notes for implementers

- Keep the description markdown concise; long diffs are better left to the commit/diff view in the PR UI.
- Prefer to run the git commands against `origin` to avoid surprise local-only changes.
- Never assume the repository default branch is the PR target unless you have conclusive evidence it is the parent branch of the current branch.
- If the parent branch is unclear, list the repository's long-lived branches and ask the user to choose or provide the target branch before creating the PR.
- Prefer GitHub MCP as the first choice when it is available and healthy.
- If GitHub MCP is in read-only mode (`GITHUB_READ_ONLY=1`), fall back to `gh` CLI for write operations (create/update).
- If GitHub MCP is unavailable, proceed with GitHub CLI.
- For PowerShell, prefer here-strings for multi-line PR descriptions, write them to a UTF-8 temp file, and pass `--body-file` to `gh pr create` or `gh pr edit`.
- Auto-detect `owner` and `repo` from the git remote URL (e.g., `git remote get-url origin` returns `git@github.com:owner/repo.git` or `https://github.com/owner/repo.git`).
- The GitHub MCP server in this repo uses Docker (`ghcr.io/github/github-mcp-server`) with `GITHUB_PERSONAL_ACCESS_TOKEN` from environment and `GITHUB_READ_ONLY=1`.
