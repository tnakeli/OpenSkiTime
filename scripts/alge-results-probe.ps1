<#
.SYNOPSIS
  Read-only check of the ALGE Results (MT1) realtime API: login, SockJS/STOMP push and REST trigger count.

.DESCRIPTION
  Prompts for the password locally (never printed or stored). Prints no authorization token.
  Subscribes to /topic/device/{DeviceId}/trigger and /topic/user/{userId}/devices/trigger and shows, for each trigger,
  the device clock time (timestamp + timeOffset, as documented) and the delay from trigger to reception.
  Sends no device commands and does not modify anything on ALGE Results.

.EXAMPLE
  pwsh -File scripts/alge-results-probe.ps1 -Username club@example.test -DeviceId 231203016 -Channel 1
#>
param(
    [Parameter(Mandatory)] [string] $Username,
    [Parameter(Mandatory)] [string] $DeviceId,
    [int] $Channel = 1,
    [int] $Seconds = 120
)
$ErrorActionPreference = 'Stop'
$root = 'https://www.alge-results.com'
$secure = Read-Host -AsSecureString "ALGE Results password for $Username"
$password = [System.Net.NetworkCredential]::new('', $secure).Password

function Stamp { (Get-Date).ToString('HH:mm:ss.fff') }

# 1. Login (one call; repeated logins count against the IP-based API quota).
$login = Invoke-WebRequest -Method Post -Uri "$root/mt1/api/user/login" -ContentType 'application/json' `
    -Body (@{ username = $Username; password = $password } | ConvertTo-Json) -SkipHttpErrorCheck
$password = $null
$body = $login.Content | ConvertFrom-Json
if ($login.StatusCode -ne 200 -or $body.status -ne 0) { throw "Login failed: HTTP $($login.StatusCode) status $($body.status) $($body.message)" }
$token = [string]$login.Headers['authorization']
$user = $body.data[0]
Write-Host "$(Stamp) Login OK · user id $($user.id) · roles $($user.roles -join ', ') · token received (not shown)"

# 2. REST count for the configured channel (baseline).
function Count {
    $r = Invoke-RestMethod -Uri "$root/mt1/api/devices/$DeviceId/channel/$Channel/trigger/count" -Headers @{ authorization = $token }
    if ($r.status -ne 0) { throw "Count failed: $($r.status) $($r.message)" }
    return [long]$r.data[0].value
}
$baseline = Count
Write-Host "$(Stamp) REST trigger count device $DeviceId C$Channel = $baseline"

# 3. SockJS endpoint information and raw WebSocket transport.
$info = Invoke-RestMethod -Uri "$root/devices/info"
Write-Host "$(Stamp) SockJS info: websocket=$($info.websocket) origins=$($info.origins -join ',') cookie_needed=$($info.cookie_needed)"
$server = '{0:000}' -f (Get-Random -Maximum 1000)
$session = -join ((1..8) | ForEach-Object { [char](97 + (Get-Random -Maximum 26)) })
$uri = [Uri]("wss://www.alge-results.com/devices/$server/$session/websocket")
$ws = [System.Net.WebSockets.ClientWebSocket]::new()
[void]$ws.ConnectAsync($uri, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
Write-Host "$(Stamp) WebSocket open: $($uri.AbsolutePath)"

function Send([string] $frame) {
    $json = ConvertTo-Json -InputObject @($frame) -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    [void]$ws.SendAsync([ArraySegment[byte]]::new($bytes), "Text", $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}
# One pending receive is kept across waits. Cancelling a ClientWebSocket receive aborts the socket, so a timeout
# only stops waiting; the same receive continues on the next call.
$script:receiveBuffer = [byte[]]::new(65536)
$script:pendingReceive = $null
$script:partial = [Text.StringBuilder]::new()
function Receive([int] $timeoutMs) {
    while ($true) {
        if ($null -eq $script:pendingReceive) {
            $script:pendingReceive = $ws.ReceiveAsync([ArraySegment[byte]]::new($script:receiveBuffer), [Threading.CancellationToken]::None)
        }
        if (-not $script:pendingReceive.Wait($timeoutMs)) { return $null }
        $result = $script:pendingReceive.GetAwaiter().GetResult()
        $script:pendingReceive = $null
        if ($result.MessageType -eq 'Close') { return 'CLOSED' }
        [void]$script:partial.Append([Text.Encoding]::UTF8.GetString($script:receiveBuffer, 0, $result.Count))
        if ($result.EndOfMessage) { $text = $script:partial.ToString(); [void]$script:partial.Clear(); return $text }
    }
}
function StompFrames([string] $sockjs) {
    if (-not $sockjs.StartsWith('a')) { return @() }
    return (ConvertFrom-Json $sockjs.Substring(1))
}

$open = Receive 10000
Write-Host "$(Stamp) SockJS: $open"
$nul = [char]0
Send ("CONNECT`naccept-version:1.2,1.1`nheart-beat:10000,10000`nauthorization:$token`n`n" + $nul)
$connected = $false
$deadline = (Get-Date).AddSeconds(10)
while (-not $connected -and (Get-Date) -lt $deadline) {
    $frame = Receive 10000
    if ($null -eq $frame) { break }
    foreach ($stomp in StompFrames $frame) {
        $command = $stomp.Split("`n")[0]
        Write-Host "$(Stamp) STOMP $command $(($stomp.Split("`n") | Select-Object -Skip 1 | Where-Object { $_ -match '^(version|heart-beat|server|message):' }) -join ' ')"
        if ($command -eq 'CONNECTED') { $connected = $true }
        if ($command -eq 'ERROR') { throw "STOMP CONNECT rejected" }
    }
}
if (-not $connected) { throw 'No STOMP CONNECTED frame received.' }
Send ("SUBSCRIBE`nid:sub-device`ndestination:/topic/device/$DeviceId/trigger`n`n" + $nul)
Send ("SUBSCRIBE`nid:sub-user`ndestination:/topic/user/$($user.id)/devices/trigger`n`n" + $nul)
Write-Host "$(Stamp) Subscribed. PRESS THE BUTTON on $DeviceId C$Channel a few times now (listening $Seconds s)..."

$end = (Get-Date).AddSeconds($Seconds); $pushes = 0; $lastSend = Get-Date; $lastServer = Get-Date
while ((Get-Date) -lt $end -and $ws.State -eq 'Open') {
    if (((Get-Date) - $lastSend).TotalSeconds -ge 10) { Send "`n"; $lastSend = Get-Date }
    $frame = Receive 1000
    if ($null -eq $frame) {
        if (((Get-Date) - $lastServer).TotalSeconds -gt 25) { Write-Host "$(Stamp) No data or heartbeat from server for 25 s" ; $lastServer = Get-Date }
        continue
    }
    $lastServer = Get-Date
    if ($frame -eq 'a["\n"]') { Write-Host "$(Stamp) STOMP heartbeat from server"; continue }
    if ($frame -eq 'h') { Write-Host "$(Stamp) SockJS heartbeat"; continue }
    if ($frame -eq 'CLOSED' -or $frame.StartsWith('c')) { Write-Host "$(Stamp) Closed by server: $frame"; break }
    foreach ($stomp in StompFrames $frame) {
        $received = [DateTimeOffset]::UtcNow
        $parts = $stomp -split "`n`n", 2
        $headers = $parts[0].Split("`n")
        $destination = ($headers | Where-Object { $_.StartsWith('destination:') }) -replace '^destination:', ''
        if ($headers[0] -ne 'MESSAGE') { Write-Host "$(Stamp) STOMP $($headers[0])"; continue }
        $payload = $parts[1].TrimEnd($nul) | ConvertFrom-Json
        $dto = $payload.dto
        $utcTicks = [long]$dto.timestamp + [DateTime]::UnixEpoch.Ticks
        $deviceTime = [DateTime]::new($utcTicks + [long]$dto.timeOffset * 600000000L)
        $delay = ($received.UtcTicks - $utcTicks) / 10000.0
        $pushes++
        Write-Host ("{0} PUSH {1} · {2} {3} {4} · device time {5:HH:mm:ss.fffffff} · bib {6} · valid {7} · delay {8:N0} ms · via {9}" -f
            (Stamp), $payload.type, $dto.deviceId, $dto.timingChannel, $dto.type, $deviceTime, $dto.startNumber.startNumber, $dto.valid, $delay,
            ($(if ($destination -like '/topic/device/*') { 'device topic' } else { 'user topic' })))
    }
}
$ws.Dispose()
$after = Count
Write-Host "$(Stamp) REST trigger count device $DeviceId C$Channel = $after (+$($after - $baseline)); push messages received: $pushes"
$latest = Invoke-RestMethod -Uri "$root/mt1/api/devices/$DeviceId/channel/$Channel/trigger?limit=3" -Headers @{ authorization = $token }
foreach ($t in $latest.data) {
    $time = [DateTime]::new([long]$t.timestamp + [DateTime]::UnixEpoch.Ticks + [long]$t.timeOffset * 600000000L)
    Write-Host ("{0} REST latest · {1} {2} · device time {3:HH:mm:ss.fffffff} · timeOffset {4} min" -f (Stamp), $t.timingChannel, $t.type, $time, $t.timeOffset)
}
