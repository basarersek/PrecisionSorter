# Sends one command to the dev server over Rust's WebSocket RCON and prints the reply.
# Rust authenticates by the WebSocket request path, so the password is part of the URL.
# Usage: powershell -File tools\rcon.ps1 -Command "ps.selftest"
param(
    [Parameter(Mandatory = $true)][string]$Command,
    [string]$Server = '127.0.0.1',
    [int]$Port = 28016,
    [string]$Password = 'devpassword'
)

$ErrorActionPreference = 'Stop'

$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$token = [System.Threading.CancellationToken]::None
$uri = [Uri]("ws://{0}:{1}/{2}" -f $Server, $Port, $Password)

try {
    $ws.ConnectAsync($uri, $token).Wait()
}
catch {
    $inner = $_.Exception.InnerException
    while ($inner -and $inner.InnerException) { $inner = $inner.InnerException }
    throw "RCON connect failed: $($inner.Message)"
}

$payload = '{"Identifier":1,"Message":"' + $Command + '","Name":"dev"}'
$bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
$segment = [ArraySegment[byte]]::new($bytes)
$ws.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $token).Wait()

$cancel = [System.Threading.CancellationTokenSource]::new(5000)
$buffer = New-Object byte[] 65536
$result = $ws.ReceiveAsync([ArraySegment[byte]]::new($buffer), $cancel.Token).Result

if ($result.Count -gt 0) {
    Write-Output ([System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count))
}

$ws.Dispose()
