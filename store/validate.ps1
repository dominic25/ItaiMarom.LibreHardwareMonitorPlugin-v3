$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$store = $PSScriptRoot
$expected = @(
    "01-hero.png",
    "02-sensor-window.png",
    "03-variables.png",
    "04-fan-control.png",
    "05-remote.png"
)

$failed = $false
foreach ($name in $expected) {
    $path = Join-Path $store $name
    if (-not (Test-Path $path)) {
        Write-Output ("MISSING  {0}" -f $name)
        $failed = $true
        continue
    }

    $image = [System.Drawing.Image]::FromFile($path)
    $width = $image.Width
    $height = $image.Height
    $image.Dispose()
    $bytes = (Get-Item $path).Length

    $problems = @()
    if ($width -ne 1920 -or $height -ne 1080) { $problems += "not 1920x1080" }
    if ([math]::Abs(($width / $height) - (16 / 9)) -gt 0.001) { $problems += "not 16:9" }
    if ($bytes -gt 1MB) { $problems += "over 1 MB" }

    $state = if ($problems.Count -eq 0) { "OK    " } else { "FAIL  " }
    Write-Output ("{0}{1}  {2}x{3}  {4:N0} KB  {5}" -f $state, $name, $width, $height, ($bytes / 1KB), ($problems -join ", "))
    if ($problems.Count -gt 0) { $failed = $true }
}

if ($failed) { exit 1 }
