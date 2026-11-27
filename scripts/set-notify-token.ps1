<#
.SYNOPSIS
  Stores the airlift-notify token as AIRLIFT_DISPATCH_TOKEN in every catalog app repository.
.DESCRIPTION
  App repositories use this secret to tell fezcode/Airlift that they published, edited or deleted a release.
  The token is read without echo, checked against fezcode/Airlift first, and never written to disk.
  Run it again with a new token when the old one expires. Requires an authenticated `gh`.
.PARAMETER Repository
  Limit to specific repositories (owner/name). Defaults to every repository in src/data/catalog.json.
.EXAMPLE
  .\scripts\set-notify-token.ps1
  .\scripts\set-notify-token.ps1 -Repository fezcode/Beetle
#>
param([string[]]$Repository)
$ErrorActionPreference = 'Stop'

$catalog = Get-Content (Join-Path $PSScriptRoot '..\src\data\catalog.json') -Raw | ConvertFrom-Json
$repos = if ($Repository) { $Repository } else { $catalog.apps | ForEach-Object { $_.repository } | Sort-Object -Unique }

$secure = Read-Host 'Paste the airlift-notify token' -AsSecureString
$token = [System.Net.NetworkCredential]::new('', $secure).Password
if ([string]::IsNullOrWhiteSpace($token)) { throw 'No token entered.' }
$token = $token.Trim()

try {
    # A dispatch nobody listens for is harmless; 204 proves the token can notify fezcode/Airlift.
    Write-Host 'Checking the token against fezcode/Airlift...'
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'airlift-notify-setup' }
    try {
        Invoke-RestMethod -Method Post -Uri 'https://api.github.com/repos/fezcode/Airlift/dispatches' -Headers $headers `
            -ContentType 'application/json' -Body '{"event_type":"airlift-token-check"}' | Out-Null
    }
    catch {
        throw "The token cannot notify fezcode/Airlift. Check that it targets only fezcode/Airlift with Contents: Read and write. ($($_.Exception.Message))"
    }
    Write-Host 'Token works.' -ForegroundColor Green

    $failed = @()
    foreach ($repo in $repos) {
        # gh reads the value from stdin and strips the trailing newline PowerShell adds.
        $ErrorActionPreference = 'Continue'
        $token | gh secret set AIRLIFT_DISPATCH_TOKEN --repo $repo
        $ok = $LASTEXITCODE -eq 0
        $ErrorActionPreference = 'Stop'
        if (-not $ok) { $failed += $repo; Write-Warning "Failed: $repo" }
        else { Write-Host "Set: $repo" }
    }
    if ($failed.Count) { throw "Could not set the secret in: $($failed -join ', ')" }
    Write-Host "Done: $($repos.Count) repositories." -ForegroundColor Green
}
finally {
    $token = $null; $headers = $null
}
