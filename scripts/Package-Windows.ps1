#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string] $Version,
    [string] $OutputDirectory = 'artifacts/windows',
    [string] $Dotnet = 'dotnet',
    [string] $Iscc = 'ISCC.exe',
    [switch] $SkipInstaller
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory))
if (!$output.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output must be within this repository.'
}
if (Test-Path -LiteralPath $output) { throw 'Package output already exists. Choose a new empty output directory to avoid stale release files.' }
New-Item -ItemType Directory -Path $output | Out-Null
$app = Join-Path $output 'app'
$assemblyVersion = ($Version -split '-')[0] + '.0'
function Publish-Project([string] $Project, [string] $Destination) {
    & $Dotnet publish (Join-Path $repository "rewrite/src/$Project/$Project.csproj") -c Release -r win-x64 --self-contained true `
        -p:SkipLiveTimingArtifacts=true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
        "-p:Version=$Version" "-p:AssemblyVersion=$assemblyVersion" "-p:FileVersion=$assemblyVersion" -o $Destination
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $Project" }
}
Publish-Project 'OpenSkiTime.Rewrite.Desktop' $app
Publish-Project 'OpenSkiTime.LiveTiming.ControlPanel' (Join-Path $app 'LiveTiming/ControlPanel')
Publish-Project 'OpenSkiTime.LiveTiming.Worker' (Join-Path $app 'LiveTiming/Worker')
Publish-Project 'OpenSkiTime.LiveTiming.Server' (Join-Path $app 'LiveTiming/Server')
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination (Join-Path $app 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repository 'deploy/windows/NOTICE.txt') -Destination (Join-Path $app 'NOTICE.txt')
foreach ($relative in @('OpenSkiTime.Rewrite.Desktop.exe','TimyUsbHost.exe','tessdata/eng.traineddata',
    'LiveTiming/ControlPanel/OpenSkiTime.LiveTiming.ControlPanel.exe','LiveTiming/Worker/OpenSkiTime.LiveTiming.Worker.exe',
    'LiveTiming/Server/OpenSkiTime.LiveTiming.Server.exe','LiveTiming/Server/wwwroot/live.js')) {
    if (!(Test-Path -LiteralPath (Join-Path $app $relative))) { throw "Missing package component: $relative" }
}
foreach ($directory in @($app,(Join-Path $app 'LiveTiming/ControlPanel'),(Join-Path $app 'LiveTiming/Worker'),(Join-Path $app 'LiveTiming/Server'))) {
    if (!(Test-Path -LiteralPath (Join-Path $directory 'coreclr.dll'))) { throw "Missing self-contained runtime in $directory" }
}
if (!(Test-Path -LiteralPath (Join-Path $app 'LiveTiming/ControlPanel/LiveTiming/Worker'))) {
    # The control panel resolves its children relative to its own installation directory.
    New-Item -ItemType Directory -Path (Join-Path $app 'LiveTiming/ControlPanel/LiveTiming') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $app 'LiveTiming/Worker') -Destination (Join-Path $app 'LiveTiming/ControlPanel/LiveTiming/Worker') -Recurse
    Copy-Item -LiteralPath (Join-Path $app 'LiveTiming/Server') -Destination (Join-Path $app 'LiveTiming/ControlPanel/LiveTiming/Server') -Recurse
}
$portable = Join-Path $output "OpenSkiTime-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $portable
if (!$SkipInstaller) {
    & $Iscc "/DAppVersion=$Version" "/DAppNumericVersion=$assemblyVersion" "/DSourceDir=$app" "/DOutputDir=$output" (Join-Path $repository 'deploy/windows/installer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
$hashes = Get-ChildItem -LiteralPath $output -File | Where-Object Extension -in '.exe','.zip' | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
    $hash.Hash.ToLowerInvariant() + '  ' + $_.Name
}
$hashes | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
Write-Host "Unsigned Windows package created in $output"
