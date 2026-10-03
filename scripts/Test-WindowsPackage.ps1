#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $PackageDirectory)
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageDirectory)
$workerPath = Join-Path $package 'LiveTiming/Worker/OpenSkiTime.LiveTiming.Worker.exe'
$serverPath = Join-Path $package 'LiveTiming/Server/OpenSkiTime.LiveTiming.Server.dll'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$endpoint = "http://127.0.0.1:$port"
$name = 'OpenSkiTime-package-' + [guid]::NewGuid().ToString('N')
$pipe = [IO.Pipes.NamedPipeServerStream]::new($name,[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,
    [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly)
$start = [Diagnostics.ProcessStartInfo]::new($workerPath)
$start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.ArgumentList.Add('--pipe'); $start.ArgumentList.Add($name)
$start.Environment['PATH'] = Join-Path $env:SystemRoot 'System32'
$start.Environment['DOTNET_ROOT'] = Join-Path $package 'no-installed-runtime'
$start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
$start.Environment['DOTNET_HOST_PATH'] = Join-Path $package 'no-dotnet.exe'
$start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$worker = $null; $writer = $null; $reader = $null
function Read-Running([int] $PreviousServerPid = 0) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $line = $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult()
        if (!$line) { throw 'Packaged worker disconnected.' }
        $message = $line | ConvertFrom-Json
        if ($message.health.state -eq 'Running') {
            if ($PreviousServerPid) {
                try { $current = (Invoke-RestMethod -Uri "$endpoint/health").processId } catch { continue }
                if ($current -eq $PreviousServerPid) { continue }
            }
            return $message
        }
        if ($message.health.state -eq 'Error') { throw 'Packaged worker reported a permanent failure.' }
    }
    throw 'Packaged worker did not become ready.'
}
try {
    $worker = [Diagnostics.Process]::Start($start)
    $null = $pipe.WaitForConnectionAsync().WaitAsync([TimeSpan]::FromSeconds(20)).GetAwaiter().GetResult()
    $writer = [IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true); $writer.AutoFlush = $true
    $reader = [IO.StreamReader]::new($pipe,[Text.UTF8Encoding]::new($false),$false,4096,$true)
    $at = '2026-10-03T09:00:00+00:00'
    $snapshot = @{
        version=1; competition=@{name='Packaged process test';place='Synthetic';discipline='SL';date='2026-10-03';isFis=$false;codex='';gender='M';category='Club';intermediateCount=0;slope=''};
        competitors=@(@{bib=1;lastName='TEST';firstName='Synthetic';nation='FIN';club='Test';fisCode=''}); currentRun=1;
        runs=@(@{number=1;listCreatedAt=$at;startOrder=@(1);results=@(@{bib=1;status='Ready';at=$at})});updatedAt=$at;paused=$false
    }
    $command = @{command='start';options=@{kind='Local';endpoint=$endpoint;localServerAssembly=$serverPath};snapshot=$snapshot}
    $writer.WriteLine(($command | ConvertTo-Json -Depth 12 -Compress))
    $running = Read-Running
    $state = Invoke-RestMethod -Uri "$endpoint/api/sessions/$($running.session.sessionId)/state"
    if ($state.competitors[0].lastName -ne 'TEST') { throw 'Packaged local server did not receive the snapshot.' }
    $pidValue = (Invoke-RestMethod -Uri "$endpoint/health").processId
    $server = [Diagnostics.Process]::GetProcessById($pidValue)
    $expectedServer = Join-Path $package 'LiveTiming/Server/OpenSkiTime.LiveTiming.Server.exe'
    if ($server.MainModule.FileName -ne $expectedServer) { throw 'Refusing to stop a process outside the package test.' }
    $server.Kill(); $server.WaitForExit(); $server.Dispose()
    $restored = Read-Running -PreviousServerPid $pidValue
    if ($restored.session.sessionId -ne $running.session.sessionId) { throw 'Session identity was lost on server recovery.' }
    $state = Invoke-RestMethod -Uri "$endpoint/api/sessions/$($running.session.sessionId)/state"
    if ($state.version -ne 1) { throw 'Snapshot was not restored after server restart.' }
    & node (Join-Path $PSScriptRoot 'live-smoke.mjs') $endpoint
    if ($LASTEXITCODE -ne 0) { throw 'Packaged server failed its REST/SignalR check.' }
    $writer.WriteLine('{"command":"shutdown"}')
    $writer.Dispose(); $writer = $null
    $reader.Dispose(); $reader = $null
    $pipe.Dispose()
    if (!$worker.WaitForExit(10000)) { throw 'Packaged worker did not shut down.' }
    if ($worker.ExitCode -ne 0) { throw 'Packaged worker failed during shutdown.' }
    Write-Host 'Package check passed: self-contained worker/server, snapshot, restart recovery, REST/SignalR and shutdown.'
} finally {
    if ($writer) { $writer.Dispose() }; if ($reader) { $reader.Dispose() }; $pipe.Dispose()
    if ($worker) { if (!$worker.HasExited) { $worker.Kill($true); $worker.WaitForExit() }; $worker.Dispose() }
}
