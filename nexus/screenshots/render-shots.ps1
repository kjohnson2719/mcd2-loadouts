# Renders the Nexus screenshots (1920x1080) from shot.html with Microsoft Edge in headless mode: a clean and an
# annotated version of each, and a close-up of the drawer.
# Usage: powershell -ExecutionPolicy Bypass -File nexus\screenshots\render-shots.ps1
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe" }
$page = "file:///" + ((Resolve-Path (Join-Path $PSScriptRoot "shot.html")).Path -replace '\\', '/')
foreach ($shot in @(
    @{ Name = "01-drawer";        Query = "shot=drawer" },
    @{ Name = "01-drawer-notes";  Query = "shot=drawer&notes=1" },
    @{ Name = "02-page";          Query = "shot=page" },
    @{ Name = "02-page-notes";    Query = "shot=page&notes=1" },
    @{ Name = "03-closeup";       Query = "shot=closeup" })) {
    $png = Join-Path $PSScriptRoot "$($shot.Name).png"
    & $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --window-size=1920,1080 `
        --allow-file-access-from-files --virtual-time-budget=8000 --screenshot="$png" "$page`?$($shot.Query)" 2>$null | Out-Null
    Get-Item $png | Select-Object Name, Length
}
