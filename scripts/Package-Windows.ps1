#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string] $Version,
    [string] $OutputDirectory = 'artifacts/windows',
    [string] $Dotnet = 'dotnet',
    [string] $Iscc = 'ISCC.exe',
    [switch] $SkipInstaller,
    # Publish creates artifacts/windows/app; Assemble turns an existing (possibly signed) app directory into the ZIP,
    # installer and SHA256SUMS. All does both. Release signing runs Assemble on the signed app directory.
    [ValidateSet('All','Publish','Assemble')][string] $Stage = 'All',
    # ssign.exe path. The installer and its uninstaller are then signed while Inno Setup compiles them.
    [string] $SignTool
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory))
if (!$output.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output must be within this repository.'
}
$app = Join-Path $output 'app'
if ($Stage -eq 'Assemble') {
    if (!(Test-Path -LiteralPath $app -PathType Container)) { throw 'Assemble needs an existing app directory in the package output.' }
    if (@(Get-ChildItem -LiteralPath $output | Where-Object FullName -ne $app).Count) { throw 'Package output must contain only the app directory before assembly.' }
} else {
    if (Test-Path -LiteralPath $output) { throw 'Package output already exists. Choose a new empty output directory to avoid stale release files.' }
    New-Item -ItemType Directory -Path $output | Out-Null
}
$assemblyVersion = ($Version -split '-')[0] + '.0'
function Publish-Project([string] $Project, [string] $Destination) {
    & $Dotnet publish (Join-Path $repository "src/$Project/$Project.csproj") -c Release -r win-x64 --self-contained true `
        -p:SkipLiveTimingArtifacts=true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
        "-p:Version=$Version" "-p:AssemblyVersion=$assemblyVersion" "-p:FileVersion=$assemblyVersion" -o $Destination
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $Project" }
}
if ($Stage -ne 'Assemble') {
    Publish-Project 'OpenSkiTime.Desktop' $app
    Publish-Project 'OpenSkiTime.LiveTiming.ControlPanel' (Join-Path $app 'LiveTiming/ControlPanel')
    Publish-Project 'OpenSkiTime.LiveTiming.Worker' (Join-Path $app 'LiveTiming/Worker')
    Publish-Project 'OpenSkiTime.LiveTiming.Server' (Join-Path $app 'LiveTiming/Server')
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination (Join-Path $app 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $repository 'deploy/windows/NOTICE.txt') -Destination (Join-Path $app 'NOTICE.txt')
}
foreach ($relative in @('OpenSkiTime.Desktop.exe','TimyUsbHost.exe','tessdata/eng.traineddata',
    'LiveTiming/ControlPanel/OpenSkiTime.LiveTiming.ControlPanel.exe','LiveTiming/Worker/OpenSkiTime.LiveTiming.Worker.exe',
    'LiveTiming/Server/OpenSkiTime.LiveTiming.Server.exe','LiveTiming/Server/wwwroot/live.js')) {
    if (!(Test-Path -LiteralPath (Join-Path $app $relative))) { throw "Missing package component: $relative" }
}
foreach ($directory in @($app,(Join-Path $app 'LiveTiming/ControlPanel'),(Join-Path $app 'LiveTiming/Worker'),(Join-Path $app 'LiveTiming/Server'))) {
    if (!(Test-Path -LiteralPath (Join-Path $directory 'coreclr.dll'))) { throw "Missing self-contained runtime in $directory" }
}
if ($Stage -ne 'Assemble' -and !(Test-Path -LiteralPath (Join-Path $app 'LiveTiming/ControlPanel/LiveTiming/Worker'))) {
    # The control panel resolves its children relative to its own installation directory.
    New-Item -ItemType Directory -Path (Join-Path $app 'LiveTiming/ControlPanel/LiveTiming') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $app 'LiveTiming/Worker') -Destination (Join-Path $app 'LiveTiming/ControlPanel/LiveTiming/Worker') -Recurse
    Copy-Item -LiteralPath (Join-Path $app 'LiveTiming/Server') -Destination (Join-Path $app 'LiveTiming/ControlPanel/LiveTiming/Server') -Recurse
}
if ($Stage -eq 'Publish') {
    Write-Host "Unsigned Windows application published in $app"
    return
}
$portable = Join-Path $output "OpenSkiTime-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $portable
if (!$SkipInstaller) {
    $definitions = @("/DAppVersion=$Version", "/DAppNumericVersion=$assemblyVersion", "/DSourceDir=$app", "/DOutputDir=$output")
    if ($SignTool) {
        $tool = [IO.Path]::GetFullPath($SignTool)
        if (!(Test-Path -LiteralPath $tool -PathType Leaf)) { throw 'Sign tool not found.' }
        # Inno Setup replaces $q with a quote and $f with the quoted file name. ssign signs in place and
        # reuses the cloud session that signing the application files opened, so no new code is needed.
        $definitions += @('/DSignInstaller', ('/Sssign=$q' + $tool + '$q -n OpenSkiTime -u https://openskiti.me $f'))
    }
    & $Iscc @definitions (Join-Path $repository 'deploy/windows/installer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
$hashes = Get-ChildItem -LiteralPath $output -File | Where-Object Extension -in '.exe','.zip' | Sort-Object Name | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
    $hash.Hash.ToLowerInvariant() + '  ' + $_.Name + "`n"
}
# Standard sha256sum format: LF terminators and no BOM, so `sha256sum -c` works and the release job can append lines.
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), (-join $hashes), [Text.UTF8Encoding]::new($false))
Write-Host "$(if ($SignTool) { 'Signed' } else { 'Unsigned' }) Windows package created in $output"
