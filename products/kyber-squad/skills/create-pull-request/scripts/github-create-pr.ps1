<#
.SYNOPSIS
Creates or updates one GitHub pull request with the GitHub CLI, then reads it back and fails if
the title or body did not land.

.DESCRIPTION
Mechanics only: the create-pull-request skill decides the target branch, title, and body; this
script never derives them from the branch name. -Head defaults to the current branch and -Repo to
the github.com origin remote. -Draft applies only when a new pull request is created; an existing
one keeps its state.

.EXAMPLE
pwsh -NoProfile -NonInteractive -File github-create-pr.ps1 -Base main -Title "Add retry to the importer" -BodyFile ./pr-body.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Base,
    [Parameter(Mandatory = $true)] [string] $Title,
    [Parameter(Mandatory = $true)] [string] $BodyFile,
    [string] $Head,
    [string] $Repo,
    [switch] $Draft
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Gh {
    param([Parameter(Mandatory = $true)] [string[]] $Arguments)

    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return (@($output) -join "`n")
}

if (-not (Test-Path -LiteralPath $BodyFile -PathType Leaf)) {
    throw "Body file '$BodyFile' does not exist."
}

if ([string]::IsNullOrWhiteSpace($Head)) {
    $Head = (git branch --show-current)
    if ($LASTEXITCODE -ne 0) {
        throw "git branch --show-current failed with exit code $LASTEXITCODE."
    }
    if ([string]::IsNullOrWhiteSpace($Head)) {
        throw 'No current branch (detached HEAD); pass -Head.'
    }
}

if ([string]::IsNullOrWhiteSpace($Repo)) {
    $remoteUrl = (git remote get-url origin 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "git remote get-url origin failed with exit code $LASTEXITCODE."
    }
    if ($remoteUrl -match '^(?:git@github\.com:|https?://(?:[^/@]+@)?github\.com/|ssh://git@github\.com/)([^/]+)/([^/]+)$') {
        $Repo = "$($Matches[1])/$($Matches[2] -replace '\.git$', '')"
    }
    else {
        throw "Origin '$remoteUrl' is not a github.com remote; pass -Repo <owner/name>."
    }
}

function Find-OpenPullRequest {
    Invoke-Gh @('pr', 'list', '--repo', $Repo, '--head', $Head, '--base', $Base, '--state', 'open',
        '--json', 'number', '--jq', '.[0].number // empty')
}

$number = Find-OpenPullRequest
if (-not [string]::IsNullOrWhiteSpace($number)) {
    Invoke-Gh @('pr', 'edit', $number, '--repo', $Repo, '--title', $Title, '--body-file', $BodyFile) | Out-Null
}
else {
    $createArguments = @('pr', 'create', '--repo', $Repo, '--head', $Head, '--base', $Base,
        '--title', $Title, '--body-file', $BodyFile)
    if ($Draft) {
        $createArguments += '--draft'
    }

    Invoke-Gh $createArguments | Out-Null
    $number = Find-OpenPullRequest
    if ([string]::IsNullOrWhiteSpace($number)) {
        throw "The pull request from '$Head' into '$Base' was not found after creation."
    }
}

# GitHub may store line endings differently and trims trailing newlines, so compare without
# carriage returns or trailing whitespace.
$actualTitle = Invoke-Gh @('pr', 'view', $number, '--repo', $Repo, '--json', 'title', '--jq', '.title')
$actualBody = (Invoke-Gh @('pr', 'view', $number, '--repo', $Repo, '--json', 'body', '--jq', '.body')) -replace "`r", ''
$expectedBody = (Get-Content -LiteralPath $BodyFile -Raw -Encoding utf8) -replace "`r", ''

if ($actualTitle -cne $Title) {
    throw "Pull request #$number title is '$actualTitle', expected '$Title'."
}

if ($actualBody.TrimEnd() -cne $expectedBody.TrimEnd()) {
    throw "Pull request #$number body does not match '$BodyFile'."
}

Invoke-Gh @('pr', 'view', $number, '--repo', $Repo, '--json', 'url', '--jq', '.url')
