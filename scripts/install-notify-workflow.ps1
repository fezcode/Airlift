<#
.SYNOPSIS
  Adds or updates .github/workflows/airlift.yml in every catalog app repository.
.DESCRIPTION
  The workflow (scripts/airlift-notify.yml) tells fezcode/Airlift when an app publishes, edits or
  deletes a release, so release.json is rebuilt. Commits go straight to each repository's default
  branch through the GitHub API; repositories that already have the current file are skipped.
  Afterwards, `git pull` in each local copy before tagging the next release: GitHub runs the
  workflow from the tagged commit, so a tag on an older commit does not notify Airlift.
  Requires an authenticated `gh`, and the AIRLIFT_DISPATCH_TOKEN secret (scripts/set-notify-token.ps1).
.PARAMETER Repository
  Limit to specific repositories (owner/name). Defaults to every repository in src/data/catalog.json.
.PARAMETER DryRun
  Report what would change without committing.
.EXAMPLE
  .\scripts\install-notify-workflow.ps1 -DryRun
  .\scripts\install-notify-workflow.ps1 -Repository fezcode/Beetle
#>
param([string[]]$Repository, [switch]$DryRun)
$ErrorActionPreference = 'Stop'

$path = '.github/workflows/airlift.yml'
$template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'airlift-notify.yml')).Replace("`r`n", "`n")
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($template))
$catalog = Get-Content (Join-Path $PSScriptRoot '..\src\data\catalog.json') -Raw | ConvertFrom-Json
$repos = if ($Repository) { $Repository } else { $catalog.apps | ForEach-Object { $_.repository } | Sort-Object -Unique }

$failed = @()
foreach ($repo in $repos) {
    # A missing file is the normal case for a new repository, so its 404 is not an error.
    # Windows PowerShell turns any native stderr into a terminating error under 'Stop'.
    $ErrorActionPreference = 'Continue'
    $response = gh api "repos/$repo/contents/$path" 2>$null
    $found = $LASTEXITCODE -eq 0
    $ErrorActionPreference = 'Stop'
    $existing = if ($found) { $response | ConvertFrom-Json } else { $null }
    $sha = $null
    if ($existing) {
        $current = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($existing.content -replace '\s', '')))
        if ($current.Replace("`r`n", "`n") -eq $template) { Write-Host "Current: $repo"; continue }
        $sha = $existing.sha
    }
    $action = if ($sha) { 'Update' } else { 'Add' }
    if ($DryRun) { Write-Host "Would $($action.ToLower()): $repo"; continue }
    $ghArgs = @('api', '--method', 'PUT', "repos/$repo/contents/$path", '-f', "message=$action Airlift release notification", '-f', "content=$encoded")
    if ($sha) { $ghArgs += @('-f', "sha=$sha") }
    $ErrorActionPreference = 'Continue'
    gh @ghArgs | Out-Null
    $ok = $LASTEXITCODE -eq 0
    $ErrorActionPreference = 'Stop'
    if (-not $ok) { $failed += $repo; Write-Warning "Failed: $repo" } else { Write-Host "$(if ($sha) { 'Updated' } else { 'Added' }): $repo" }
}
if ($failed.Count) { throw "Could not update: $($failed -join ', ')" }
if (-not $DryRun) { Write-Host 'Done. Run `git pull` in each local copy before tagging its next release.' -ForegroundColor Green }
exit 0 # The expected 404 lookups would otherwise leave a failing exit code behind.
