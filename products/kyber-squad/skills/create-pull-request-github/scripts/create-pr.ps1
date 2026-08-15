param (
    [string]$SB = (git branch --show-current),
    [string]$TB,
    [string]$Owner,
    [string]$Repo
)

if ([string]::IsNullOrWhiteSpace($TB)) {
    Write-Error "Error: Target branch (TB) is required."
    exit 1
}

# Auto-detect owner and repo if not provided
if ([string]::IsNullOrWhiteSpace($Owner) -or [string]::IsNullOrWhiteSpace($Repo)) {
    $remoteUrl = git remote get-url origin 2>$null
    if ($remoteUrl -match 'github\.com[:/]([^/]+)/([^/]+)') {
        if ([string]::IsNullOrWhiteSpace($Owner)) { $Owner = $Matches[1] }
        if ([string]::IsNullOrWhiteSpace($Repo)) { $Repo = $Matches[2] -replace '\.git$', '' }
    }
}

if ([string]::IsNullOrWhiteSpace($Owner) -or [string]::IsNullOrWhiteSpace($Repo)) {
    Write-Error "Error: Owner and Repo could not be auto-detected and were not provided."
    exit 1
}

git fetch --prune --no-tags origin
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
    $defaultBranch = (gh repo view "$Owner/$Repo" --json defaultBranchRef --jq '.defaultBranchRef.name' 2>$null)
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

- Summarized implementation details...
"@

$descriptionFile = Join-Path $env:TEMP 'create-pr-description.md'
Set-Content -Path $descriptionFile -Value $description -Encoding utf8

$existing = gh pr list --repo "$Owner/$Repo" --head $SB --base $TB --state open --json number --jq '.[0].number // empty' 2>$null
if ($existing) {
    gh pr edit $existing --repo "$Owner/$Repo" --title $title --body-file $descriptionFile
}
else {
    gh pr create --repo "$Owner/$Repo" --head $SB --base $TB --title $title --body-file $descriptionFile
}

Remove-Item -Path $descriptionFile -ErrorAction SilentlyContinue
