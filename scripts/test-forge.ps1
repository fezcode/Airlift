param([string]$Forge = (Join-Path $PSScriptRoot '../../Forge/build/forge.exe'))
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/forge-fixture'))
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $root.StartsWith($workspace + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture must remain in the workspace.' }
New-Item -ItemType Directory -Force (Join-Path $root 'payload'), (Join-Path $root 'dist') | Out-Null
$forgeExecutable = [System.IO.Path]::GetFullPath($Forge)
foreach ($version in @('1.0.0', '1.1.0')) {
    & node (Join-Path $PSScriptRoot 'create-forge-fixture.mjs') $version
    if ($LASTEXITCODE -ne 0) { throw 'Could not derive the fixture from forge.toml.' }
    $buildProcess = Start-Process -FilePath $forgeExecutable -ArgumentList 'build' -WorkingDirectory $root -WindowStyle Hidden -Wait -PassThru
    if ($buildProcess.ExitCode -ne 0) { throw 'Forge fixture build failed.' }
}
$previousFixture = $env:AIRLIFT_FORGE_FIXTURE
$previousTelemetry = $env:AVALONIA_TELEMETRY_OPTOUT
$env:AIRLIFT_FORGE_FIXTURE = $root
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
try {
    & dotnet test (Join-Path $workspace 'native/Airlift.Tests/Airlift.Tests.csproj') -c Release --no-restore --filter FullyQualifiedName~ForgeIntegrationTests
    if ($LASTEXITCODE -ne 0) { throw 'Forge integration test failed.' }
}
finally { $env:AIRLIFT_FORGE_FIXTURE = $previousFixture; $env:AVALONIA_TELEMETRY_OPTOUT = $previousTelemetry }
