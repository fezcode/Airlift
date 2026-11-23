param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Version,
      [string]$Forge = (Join-Path $PSScriptRoot '../../Forge/build/forge.exe'))
$ErrorActionPreference = 'Stop'
$bundleText = & $Forge inspect $Path --json
if ($LASTEXITCODE -ne 0) { throw 'Forge checksum or manifest verification failed.' }
$bundle = ($bundleText -join "`n") | ConvertFrom-Json
$manifest = $bundle.manifest
if ($manifest.app.id -ne 'com.fezcode.airlift' -or $manifest.app.version -ne $Version) { throw 'Incorrect installer identity/version.' }
if ($manifest.ui.theme -notin @('mica', 'glass')) { throw 'Installer must use the Mica/glass theme.' }
if ($manifest.capabilities -notcontains 'silent-update-handoff-v1') { throw 'Rebuild sibling Forge: runtime lacks safe silent self-update handoff.' }
if (($manifest.steps.type -join ',') -ne 'welcome,license,folder,shortcuts,install,finish') { throw 'Installer must contain all six wizard steps in order.' }
$license = & $Forge inspect $Path --entry $manifest.steps[1].file
if ($LASTEXITCODE -ne 0 -or ($license -join "`n") -notmatch 'MIT License') { throw 'MIT license agreement is missing.' }
if (@($manifest.shortcuts).Count -ne 2) { throw 'Expected Desktop and Start Menu choices.' }
foreach ($shortcut in $manifest.shortcuts) {
    if (-not $shortcut.optional -or -not $shortcut.default -or $shortcut.target -ne '${INSTALLDIR}/Airlift.exe' -or $shortcut.icon -ne '${INSTALLDIR}/airlift.ico') { throw 'Incorrect shortcut configuration.' }
}
if (($manifest.shortcuts.location -join '|') -ne '${DESKTOP}/Airlift.lnk|${STARTMENU}/Airlift.lnk') { throw 'Unexpected shortcut destinations.' }
foreach ($name in @('InstallDir', 'Version')) {
    $entry = @($manifest.registry | Where-Object { $_.value -eq $name -and $_.hive -eq 'HKCU' -and $_.key -eq 'Software\fezcode\Airlift' })
    $expected = if ($name -eq 'Version') { '${app.version}' } else { '${INSTALLDIR}' }
    if ($entry.Count -ne 1 -or $entry[0].data -ne $expected) { throw "Missing vendor registration: $name" }
}
if ($manifest.uninstall.settings_dirs -notcontains '${LOCALAPPDATA}/Fezcode/Airlift') { throw 'User data preservation is not configured.' }
$launch = $manifest.steps[5].launches
if (@($launch).Count -ne 1 -or -not $launch[0].checked -or $launch[0].target -ne '${INSTALLDIR}/Airlift.exe') { throw 'Open Airlift must be selected on Finish.' }
foreach ($file in @('Airlift.exe', 'Airlift.dll', 'airlift.ico', 'LICENSE.txt', 'cli/airlift-cli.exe', 'cli/airlift-cli.dll', 'cli/LICENSE.txt')) {
    if ($bundle.files.dst -notcontains ('${INSTALLDIR}/' + $file)) { throw "Payload is missing installed file $file" }
}
$entries = & $Forge inspect $Path
if ($LASTEXITCODE -ne 0 -or ($entries -join "`n") -notmatch 'wizard/Forge.Wizard.exe') { throw 'Installer is missing the native wizard.' }
Write-Host "Verified Airlift ${Version}: bundle checksum, native wizard, six steps, MIT license, shortcuts, registry, payload and finish launch."
