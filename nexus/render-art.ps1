# Renders the Nexus header (header.png, 1300x372) and thumbnail (thumbnail.png, 1920x1080) from art.html with Microsoft
# Edge in headless mode.
# Usage: powershell -ExecutionPolicy Bypass -File nexus\render-art.ps1
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe" }
$page = "file:///" + ((Resolve-Path (Join-Path $PSScriptRoot "art.html")).Path -replace '\\', '/')
foreach ($art in @(
    @{ Name = "header";    Query = "kind=header";    Size = "1300,372" },
    @{ Name = "thumbnail"; Query = "kind=thumbnail"; Size = "1920,1080" })) {
    $png = Join-Path $PSScriptRoot "$($art.Name).png"
    # The virtual time budget lets the web font and the screenshots load before the shot.
    & $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --window-size=$($art.Size) `
        --allow-file-access-from-files --virtual-time-budget=8000 --screenshot="$png" "$page`?$($art.Query)" 2>$null | Out-Null
    Get-Item $png | Select-Object Name, Length
}
