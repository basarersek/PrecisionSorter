# Stops the dev server.
Get-Process RustDedicated -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force
    Write-Output "Stopped RustDedicated pid $($_.Id)"
}
if (-not (Get-Process RustDedicated -ErrorAction SilentlyContinue)) {
    Write-Output "Server is not running."
}
