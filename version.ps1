<#
.SYNOPSIS
  Read, set, or bump the Airlift release version across the places it still lives
  by hand, then verify those places agree.

.DESCRIPTION
  native/Directory.Build.props <Version> is the source of truth: it stamps every
  assembly Airlift ships, and Airlift.Core's AppVersion reads that stamp at
  runtime, so the CLI banner, both --version outputs, the GitHub User-Agent and
  the desktop About card follow it without editing any C#.

  Three build inputs cannot read an assembly and are synchronized here instead:
  the forge.toml [app] version, and the installer filename / title in README.md
  and native/README.md. This script keeps them in lockstep so a release cannot
  ship mismatched numbers.

.EXAMPLE
  .\version.ps1                  # report current version + consistency
  .\version.ps1 -Set 0.3.0       # set everywhere, then verify
  .\version.ps1 -Bump patch      # 0.2.2 -> 0.2.3 everywhere, then verify
  .\version.ps1 -Bump minor -DryRun
#>
param(
    [string] $Set,
    [ValidateSet('patch', 'minor', 'major')]
    [string] $Bump,
    [switch] $DryRun,
    [string] $RepoRoot = $PSScriptRoot
)

# ---- pure version helpers ---------------------------------------------------

function Parse-SemVer([string] $text) {
    if ($text -notmatch '^\s*(\d+)\.(\d+)\.(\d+)\s*$') {
        throw "Not a valid x.y.z version: '$text'"
    }
    [pscustomobject]@{
        Major = [int]$Matches[1]
        Minor = [int]$Matches[2]
        Patch = [int]$Matches[3]
    }
}

function Format-SemVer($v) { "{0}.{1}.{2}" -f $v.Major, $v.Minor, $v.Patch }

function Step-SemVer([string] $current, [string] $kind) {
    $v = Parse-SemVer $current
    switch ($kind) {
        'patch' { $v.Patch++ }
        'minor' { $v.Minor++; $v.Patch = 0 }
        'major' { $v.Major++; $v.Minor = 0; $v.Patch = 0 }
        default { throw "Unknown bump kind: '$kind'" }
    }
    Format-SemVer $v
}

function Normalize-Ver([string] $s) {
    # Collapse a 3- or 4-part version to canonical x.y.z (drops a trailing .0).
    $s.Trim() -replace '^(\d+\.\d+\.\d+)\.0$', '$1'
}

# ---- file locations ---------------------------------------------------------

function Get-PropsPath       ([string] $root) { Join-Path $root 'native/Directory.Build.props' }
function Get-ForgePath       ([string] $root) { Join-Path $root 'forge.toml' }
function Get-ReadmePath      ([string] $root) { Join-Path $root 'README.md' }
function Get-NativeReadmePath([string] $root) { Join-Path $root 'native/README.md' }

# Every place a version still lives by hand, as { Where = <label>; Value = <x.y.z> }.
function Get-VersionItems([string] $root) {
    $props        = [System.IO.File]::ReadAllText((Get-PropsPath $root))
    $forge        = [System.IO.File]::ReadAllText((Get-ForgePath $root))
    $readme       = [System.IO.File]::ReadAllText((Get-ReadmePath $root))
    $nativeReadme = [System.IO.File]::ReadAllText((Get-NativeReadmePath $root))
    $items = New-Object System.Collections.Generic.List[object]

    if ($props -match '<Version>\s*(\d+(?:\.\d+)+)\s*</Version>') {
        $items.Add([pscustomobject]@{ Where = 'props <Version>'; Value = (Normalize-Ver $Matches[1]) })
    }
    else { throw 'native/Directory.Build.props has no <Version>; restore it before building or releasing.' }

    if ($forge -match '(?m)^version\s*=\s*"(\d+(?:\.\d+)+)"') {
        $items.Add([pscustomobject]@{ Where = 'forge [app] version'; Value = (Normalize-Ver $Matches[1]) })
    }
    else { throw 'forge.toml has no [app] version; restore it before building or releasing.' }

    if ($nativeReadme -match '(?m)^#\s*Native Airlift\s+(\d+\.\d+\.\d+)\s*$') {
        $items.Add([pscustomobject]@{ Where = 'native README title'; Value = (Normalize-Ver $Matches[1]) })
    }
    else { throw 'native/README.md has no "# Native Airlift x.y.z" title; restore it before releasing.' }

    foreach ($file in @(
        [pscustomobject]@{ Label = 'README installer';        Text = $readme },
        [pscustomobject]@{ Label = 'native README installer'; Text = $nativeReadme })) {
        $setup = [regex]::Matches($file.Text, 'Airlift-Setup-(\d+\.\d+\.\d+)\.exe')
        if ($setup.Count -eq 0) { throw "$($file.Label): no Airlift-Setup-x.y.z.exe reference found; restore it before releasing." }
        for ($i = 0; $i -lt $setup.Count; $i++) {
            $label = if ($setup.Count -eq 1) { $file.Label } else { "$($file.Label) #$($i + 1)" }
            $items.Add([pscustomobject]@{ Where = $label; Value = (Normalize-Ver $setup[$i].Groups[1].Value) })
        }
    }

    , $items.ToArray()
}

# Rewrite every location to $target. Targeted replacements only, so decoys like
# the catalogued third-party versions in src/data/catalog.json are left alone.
function Set-Versions([string] $root, [string] $target, [switch] $DryRun) {
    $target = Format-SemVer (Parse-SemVer $target)   # validates the input

    $propsPath        = Get-PropsPath        $root
    $forgePath        = Get-ForgePath        $root
    $readmePath       = Get-ReadmePath       $root
    $nativeReadmePath = Get-NativeReadmePath $root
    $props        = [System.IO.File]::ReadAllText($propsPath)
    $forge        = [System.IO.File]::ReadAllText($forgePath)
    $readme       = [System.IO.File]::ReadAllText($readmePath)
    $nativeReadme = [System.IO.File]::ReadAllText($nativeReadmePath)

    $props        = [regex]::Replace($props,  '(<Version>)\s*\d+(?:\.\d+)+\s*(</Version>)', "`${1}$target`${2}")
    $forge        = [regex]::Replace($forge,  '(?m)^(version\s*=\s*")\d+(?:\.\d+)+(")',     "`${1}$target`${2}")
    $readme       = [regex]::Replace($readme, '(Airlift-Setup-)\d+\.\d+\.\d+(\.exe)',       "`${1}$target`${2}")
    $nativeReadme = [regex]::Replace($nativeReadme, '(Airlift-Setup-)\d+\.\d+\.\d+(\.exe)', "`${1}$target`${2}")
    $nativeReadme = [regex]::Replace($nativeReadme, '(?m)^(#\s*Native Airlift\s+)\d+\.\d+\.\d+[ \t]*$', "`${1}$target")

    if (-not $DryRun) {
        $encoding = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($propsPath,        $props,        $encoding)
        [System.IO.File]::WriteAllText($forgePath,        $forge,        $encoding)
        [System.IO.File]::WriteAllText($readmePath,       $readme,       $encoding)
        [System.IO.File]::WriteAllText($nativeReadmePath, $nativeReadme, $encoding)
    }
}

# Compare every location against $target (defaults to props <Version>).
function Test-Consistent([string] $root, [string] $target) {
    $items = Get-VersionItems $root
    if (-not $target) {
        $target = ($items | Where-Object { $_.Where -eq 'props <Version>' } | Select-Object -First 1).Value
    }
    $results = foreach ($item in $items) {
        [pscustomobject]@{ Where = $item.Where; Value = $item.Value; Ok = ($item.Value -eq $target) }
    }
    [pscustomobject]@{
        Target = $target
        Items  = $results
        AllOk  = (@($results | Where-Object { -not $_.Ok }).Count -eq 0)
    }
}

# ---- CLI --------------------------------------------------------------------

function Show-Report([string] $root, [string] $target) {
    $consistency = Test-Consistent $root $target
    Write-Host "Version locations (target $($consistency.Target)):"
    foreach ($item in $consistency.Items) {
        $tag   = if ($item.Ok) { 'ok ' } else { 'BAD' }
        $color = if ($item.Ok) { 'DarkGreen' } else { 'Red' }
        Write-Host ("  [{0}] {1,-26} {2}" -f $tag, $item.Where, $item.Value) -ForegroundColor $color
    }
    Write-Host '  [src] assemblies, CLI banner, --version, User-Agent and About follow props via Airlift.Core.AppVersion.' -ForegroundColor DarkGray
    if ($consistency.AllOk) { Write-Host 'All locations agree.' -ForegroundColor Green }
    else                    { Write-Host 'MISMATCH: not all locations agree.' -ForegroundColor Red }
    $consistency.AllOk
}

function Main {
    if ($Set -and $Bump) { Write-Error 'Specify only one of -Set or -Bump.'; exit 2 }

    if ($Set) {
        $target = Format-SemVer (Parse-SemVer $Set)
    }
    elseif ($Bump) {
        $current = ((Get-VersionItems $RepoRoot) | Where-Object { $_.Where -eq 'props <Version>' } | Select-Object -First 1).Value
        if (-not $current) { Write-Error 'Could not read current version from native/Directory.Build.props.'; exit 2 }
        $target = Step-SemVer $current $Bump
        Write-Host "Bumping $current -> $target ($Bump)" -ForegroundColor Cyan
    }
    else {
        # Report-only mode.
        $ok = Show-Report $RepoRoot $null
        exit ([int](-not $ok))
    }

    if ($DryRun) {
        Write-Host "DryRun: would set version to $target everywhere (no files written)." -ForegroundColor Yellow
        exit 0
    }

    Set-Versions $RepoRoot $target
    Write-Host "Set version to $target. Verifying..." -ForegroundColor Cyan
    $ok = Show-Report $RepoRoot $target
    exit ([int](-not $ok))
}

# Run Main only when executed directly; dot-sourcing (e.g. from tests) sets
# InvocationName to '.' and must not trigger the CLI.
if ($MyInvocation.InvocationName -ne '.') { Main }
