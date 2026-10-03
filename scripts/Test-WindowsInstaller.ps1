#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Installer)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or !$env:RUNNER_TEMP) { throw 'Installer acceptance runs only on an ephemeral GitHub Windows runner.' }
$installerPath = [IO.Path]::GetFullPath($Installer)
if (!(Test-Path -LiteralPath $installerPath)) { throw 'Installer not found.' }
$directory = Join-Path $env:RUNNER_TEMP ('OpenSkiTime-installer-test-' + [guid]::NewGuid().ToString('N'))
function Run-Setup([string] $Executable, [string[]] $Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    try { if ($process.ExitCode -ne 0) { throw 'Installer/uninstaller acceptance failed.' } }
    finally { $process.Dispose() }
}
$arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=',"/DIR=`"$directory`"")
Run-Setup $installerPath $arguments
if (!(Test-Path -LiteralPath (Join-Path $directory 'OpenSkiTime.Desktop.exe'))) { throw 'Desktop was not installed.' }
& (Join-Path $PSScriptRoot 'Test-WindowsPackage.ps1') -PackageDirectory $directory
# A file not supplied by the installer must survive both reinstall and uninstall.
$sentinel = Join-Path $directory 'synthetic-user-data.txt'
Set-Content -LiteralPath $sentinel -Value 'Synthetic preservation check'
Run-Setup $installerPath $arguments
if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'Synthetic preservation check') { throw 'Reinstall changed user data.' }
Run-Setup (Join-Path $directory 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
if (!(Test-Path -LiteralPath $sentinel)) { throw 'Uninstall removed an unrelated user file.' }
if (Test-Path -LiteralPath (Join-Path $directory 'OpenSkiTime.Desktop.exe')) { throw 'Uninstall left the application installed.' }
Write-Host 'Installer check passed: install, self-contained processes, reinstall and user-file preservation on uninstall.'
# Leave the small synthetic sentinel for the ephemeral runner to clean up. No recursive deletion is needed.
