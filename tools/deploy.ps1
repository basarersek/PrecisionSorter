# Copies the plugin into the running dev server's oxide/plugins folder.
# Oxide hot-reloads the plugin when the file lands, no restart needed.
$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot '..\src\PrecisionSorter.cs'
$target = 'C:\RustServer\server\oxide\plugins\PrecisionSorter.cs'

if (-not (Test-Path -LiteralPath $source)) {
    throw "Plugin source not found: $source"
}

Copy-Item -LiteralPath $source -Destination $target -Force
Write-Output "Deployed PrecisionSorter.cs -> $target"
