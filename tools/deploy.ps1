# Copies the plugin into the dev server and reloads it live.
# Oxide hot-reloads on file change; the explicit RCON reload makes it deterministic.
param([switch]$NoReload)

$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot '..\src\PrecisionSorter.cs'
$target = 'C:\RustServer\server\oxide\plugins\PrecisionSorter.cs'

if (-not (Test-Path -LiteralPath $source)) {
    throw "Plugin source not found: $source"
}

Copy-Item -LiteralPath $source -Destination $target -Force
Write-Output "Deployed PrecisionSorter.cs"

if ($NoReload) {
    Write-Output "Reload skipped."
    exit 0
}

$rcon = Join-Path $PSScriptRoot 'rcon.ps1'
if (-not (Test-Path -LiteralPath $rcon)) {
    Write-Output "rcon.ps1 missing, reload skipped."
    exit 0
}

try {
    $reply = & powershell -NoProfile -ExecutionPolicy Bypass -File $rcon -Command 'oxide.reload PrecisionSorter' 2>&1
    if ($reply) { Write-Output ($reply | Select-Object -First 1) } else { Write-Output "Reloaded (no reply, server may be down)." }
}
catch {
    Write-Output "Server not reachable, reload skipped. Start it with tools\start_server.bat"
}
