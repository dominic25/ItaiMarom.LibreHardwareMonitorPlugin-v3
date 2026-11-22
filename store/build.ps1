param(
    [switch]$SkipWindowRebuild
)

$ErrorActionPreference = "Stop"
$store = $PSScriptRoot
$src = Join-Path $store "src"
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) {
    $edge = "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
}
if (-not (Test-Path $edge)) {
    throw "Microsoft Edge was not found. It is used as the headless renderer."
}

if (-not $SkipWindowRebuild) {
    & node (Join-Path $src "make-windows.mjs")
    if ($LASTEXITCODE -ne 0) { throw "make-windows.mjs failed." }
}

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src "patch-cards.ps1")
if ($LASTEXITCODE -ne 0) { throw "patch-cards.ps1 failed." }

$slides = Get-ChildItem (Join-Path $src "*.html") |
    Where-Object { $_.Name -match '^\d{2}-' } |
    Sort-Object Name

foreach ($slide in $slides) {
    $out = Join-Path $store ($slide.BaseName + ".png")
    $url = "file:///" + (($slide.FullName -replace '\\', '/'))
    $profile = Join-Path $env:LOCALAPPDATA "Temp\opencode\edge-profile"
    & $edge --headless=new --disable-gpu --hide-scrollbars --no-first-run `
        --allow-file-access-from-files --force-device-scale-factor=1 `
        --window-size=1920,1080 --virtual-time-budget=5000 `
        --user-data-dir="$profile" --screenshot="$out" $url | Out-Null
    if (-not (Test-Path $out)) { throw "No screenshot was written for $($slide.Name)." }
    Write-Output ("{0}  {1:N0} KB" -f $slide.BaseName, ((Get-Item $out).Length / 1KB))
}
