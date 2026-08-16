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
    if ([string]::IsNullOrWhiteSpace($remoteUrl)) {
        Write-Error "Error: Could not determine git remote URL for origin."
        exit 1
    }
    if ($remoteUrl -match '^(?:git@github\.com:|https?://(?:[^/@]+@)?github\.com/|ssh://git@github\.com/)([^/]+)/([^/]+)$') {
        if ([string]::IsNullOrWhiteSpace($Owner)) { $Owner = $Matches[1] }
        if ([string]::IsNullOrWhiteSpace($Repo)) { $Repo = $Matches[2] -replace '\.git$', '' }
    }
    else {
        Write-Error "Error: Remote origin host must be github.com."
        exit 1
    }
}

if ([string]::IsNullOrWhiteSpace($Owner) -or [string]::IsNullOrWhiteSpace($Repo)) {
    Write-Error "Error: Owner and Repo could not be auto-detected and were not provided."
    exit 1
}

git fetch --prune --no-tags origin
$summary = git log --no-merges --pretty=format:"%s" -n 1 "origin/$TB..$SB" 2>$null
if ([string]::IsNullOrWhiteSpace($summary)) {
    $summary = "Summarize changes here..."
}

$branchStem = $SB -replace '^(feature|bugfix|hotfix|chore|task)/', ''
$titleParts = ($branchStem -split '[-_/]+' | Where-Object { $_ }) | ForEach-Object {
    if ($_ -match '^\d+$') { $_ }
    else { [System.Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($_.ToLowerInvariant()) }
}
$title = [string]::Join(' ', $titleParts)

if ($branchStem -match '^(\d+)(?:[-_/]|$)') {
    $ticket = "#$($Matches[1])"
}
elseif ($branchStem -match '([A-Za-z]+-\d+)') {
    $ticket = $Matches[1]
}
else {
    $ticket = ''
}

$relatedIssueSection = if ($ticket) {
    $defaultBranch = (gh repo view "$Owner/$Repo" --json defaultBranchRef -q .defaultBranchRef.name 2>$null)
    if ([string]::IsNullOrWhiteSpace($defaultBranch)) { $defaultBranch = 'main' }
    $directive = if ($TB -eq $defaultBranch -and $ticket -match '^#\d+$') {
        "Closes $ticket"
    }
    elseif ($ticket -match '^#\d+$') {
        "Related issue: $ticket"
    }
    else {
        "Ticket: $ticket"
    }
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

$descriptionFile = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName() + '.md')
try {
    Set-Content -Path $descriptionFile -Value $description -Encoding utf8

    $existing = gh pr list --repo "$Owner/$Repo" --head $SB --base $TB --state open --json number -q '.[0].number // empty'
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Error: Failed to query existing pull requests for $Owner/$Repo."
        exit 1
    }
    if ($existing) {
        gh pr edit $existing --repo "$Owner/$Repo" --title $title --body-file $descriptionFile
    }
    else {
        gh pr create --repo "$Owner/$Repo" --head $SB --base $TB --title $title --body-file $descriptionFile
    }
}
finally {
    if (Test-Path $descriptionFile) {
        Remove-Item -Path $descriptionFile -Force -ErrorAction SilentlyContinue
    }
}
