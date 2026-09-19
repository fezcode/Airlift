param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Test,
    [switch]$Publish,
    [switch]$Run,
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
. (Join-Path $projectRoot 'version.ps1')
if (-not (Show-Report $projectRoot $null)) { throw 'Version mismatch. Run version.ps1 before building.' }
if ($Runtime -notin @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')) { throw "Unsupported runtime: $Runtime" }
$previousTelemetry = $env:AVALONIA_TELEMETRY_OPTOUT
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Push-Location $projectRoot
try {
    Invoke-DotNet @('restore', 'native/Airlift.slnx', '--configfile', 'native/NuGet.Config', '--packages', '.nuget/packages')
    Invoke-DotNet @('build', 'native/Airlift.slnx', '--no-restore', '-c', $Configuration)
    if ($Test) { Invoke-DotNet @('test', 'native/Airlift.slnx', '--no-build', '--no-restore', '-c', $Configuration) }
    if ($Publish) {
        foreach ($projectName in @('Airlift.Desktop', 'Airlift.Cli')) {
            $outputName = if ($projectName -eq 'Airlift.Desktop') { 'desktop' } else { 'cli' }
            $outputPath = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "dist/$Runtime/$outputName"))
            if (-not $outputPath.StartsWith((Join-Path $projectRoot 'dist') + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Publish output must stay within dist.' }
            # Replace only this runtime's generated payload, so old single-file builds cannot linger.
            if (Test-Path -LiteralPath $outputPath) {
                $items = @((Get-Item -LiteralPath $outputPath)) + @(Get-ChildItem -LiteralPath $outputPath -Recurse -Force)
                if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw "Refusing to clean linked publish output: $outputPath" }
                Remove-Item -LiteralPath $outputPath -Recurse -Force
            }
            Invoke-DotNet @('publish', "native/$projectName/$projectName.csproj", '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:IncludeNativeLibrariesForSelfExtract=false', '-p:DebugType=None', "-p:RestorePackagesPath=$(Join-Path $projectRoot '.nuget/packages')", '--source', 'https://api.nuget.org/v3/index.json', '-o', "dist/$Runtime/$outputName")
            Get-ChildItem -LiteralPath $outputPath -File -Filter '*.pdb' | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
        }
    }
    if ($Run) { Invoke-DotNet @('run', '--project', 'native/Airlift.Desktop', '-c', $Configuration, '--no-build') }
}
finally { Pop-Location; $env:AVALONIA_TELEMETRY_OPTOUT = $previousTelemetry }
