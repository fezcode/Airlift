param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$bytes = [IO.File]::ReadAllBytes($Path)
$trailer = $bytes.Length - 72
if ($trailer -lt 64 -or [Text.Encoding]::ASCII.GetString($bytes, $trailer, 5) -ne 'FORGE') { throw 'Missing Forge trailer.' }
$offset = [BitConverter]::ToInt64($bytes, $trailer + 16)
$length = [BitConverter]::ToInt64($bytes, $trailer + 24)
if ($offset -lt 64 -or $length -le 0 -or $offset + $length -ne $trailer) { throw 'Invalid Forge payload bounds.' }
$sha = [Security.Cryptography.SHA256]::Create()
try { $hash = $sha.ComputeHash($bytes, $offset, $length) } finally { $sha.Dispose() }
if ([BitConverter]::ToString($hash) -ne [BitConverter]::ToString($bytes, $trailer + 32, 32)) { throw 'Forge checksum mismatch.' }
$stream = [IO.MemoryStream]::new($bytes, [int]$offset, [int]$length, $false)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
try {
    function Read-Entry([string]$Name) {
        $entry = $zip.GetEntry($Name)
        if ($null -eq $entry) { throw "Installer is missing $Name" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    $manifest = (Read-Entry 'manifest.json') | ConvertFrom-Json
    if ($manifest.app.id -ne 'com.fezcode.airlift' -or $manifest.app.version -ne $Version) { throw 'Incorrect installer identity/version.' }
    if ($manifest.ui.theme -ne 'mica') { throw 'Installer must use Mica.' }
    if ($manifest.capabilities -notcontains 'silent-update-handoff-v1') { throw 'Rebuild sibling Forge: this runtime lacks safe silent self-update handoff.' }
    if (($manifest.steps.type -join ',') -ne 'welcome,license,folder,shortcuts,install,finish') { throw 'Installer must contain all six wizard steps in order.' }
    if ((Read-Entry $manifest.steps[1].file) -notmatch 'MIT License') { throw 'MIT license agreement is missing.' }
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
    $destinations = @($manifest.files.dst)
    foreach ($file in @('Airlift.exe', 'Airlift.dll', 'airlift.ico', 'LICENSE.txt', 'cli/airlift-cli.exe', 'cli/airlift-cli.dll', 'cli/LICENSE.txt')) {
        if ($destinations -notcontains ('${INSTALLDIR}/' + $file)) { throw "Payload is missing installed file $file" }
    }
    if ($null -eq $zip.GetEntry('theme/fragments/shortcuts.html') -or (Read-Entry 'theme/theme.js') -notmatch 'SetShortcutEnabled') { throw 'Packaged theme cannot render shortcut choices.' }
    Write-Host "Verified Airlift ${Version}: bundle checksum, six steps, MIT license, shortcuts, registry, folder payload and finish launch."
} finally { $zip.Dispose(); $stream.Dispose() }
