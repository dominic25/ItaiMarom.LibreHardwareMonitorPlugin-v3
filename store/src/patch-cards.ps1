Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"

$captures = Join-Path $PSScriptRoot "captures"

function New-Graphics([System.Drawing.Bitmap]$bmp) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    return $g
}

function New-Font([string]$family, [float]$pixels, [System.Drawing.FontStyle]$style) {
    return New-Object System.Drawing.Font($family, $pixels, $style, [System.Drawing.GraphicsUnit]::Pixel)
}

function Fill-RoundedTop([System.Drawing.Graphics]$g, [int]$w, [int]$height, [int]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddLine($radius, 0, $w - $radius, 0)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddLine($w, $radius, $w, $height)
    $path.AddLine($w, $height, 0, $height)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(26, 26, 26))
    $g.FillPath($brush, $path)
    $brush.Dispose()
    $path.Dispose()
}

function Open-Copy([string]$path) {
    $source = [System.Drawing.Bitmap]::FromFile($path)
    $copy = New-Object System.Drawing.Bitmap($source)
    $source.Dispose()
    return $copy
}

# Fan card: the deck label ran the name, the RPM and the control value together
# ("Fan 1 2229RPM64 %"). Redraw the title band with the same type and colours.
$fanPath = Join-Path $captures "strip5-5.png"
$bmp = Open-Copy $fanPath
$g = New-Graphics $bmp
Fill-RoundedTop $g $bmp.Width 172 42

$grey = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(154, 154, 154))
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(245, 245, 245))
$fontTitle = New-Font "Segoe UI Semibold" 38 ([System.Drawing.FontStyle]::Regular)
$fontSub = New-Font "Segoe UI" 22 ([System.Drawing.FontStyle]::Regular)

$g.DrawString("Fan 1", $fontTitle, $grey, 58, 58)
$right = "64 %"
$size = $g.MeasureString($right, $fontTitle)
$g.DrawString($right, $fontTitle, $white, (560 - 56 - $size.Width), 58)
$g.DrawString("2,229 RPM", $fontSub, $grey, 58, 116)

$fontTitle.Dispose(); $fontSub.Dispose(); $grey.Dispose(); $white.Dispose(); $g.Dispose()
$bmp.Save($fanPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

# GPU card: the deck's label was "Hot sport"; it is the hotspot reading.
foreach ($name in @("strip5-3.png", "strip4-2.png")) {
    $path = Join-Path $captures $name
    $bmp = Open-Copy $path
    $g = New-Graphics $bmp
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(26, 26, 26))
    $g.FillRectangle($fill, 92, 478, 160, 46)
    $fill.Dispose()
    $grey = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(154, 154, 154))
    $font = New-Font "Segoe UI" 24 ([System.Drawing.FontStyle]::Regular)
    $g.DrawString("Hot spot", $font, $grey, 106, 486)
    $font.Dispose(); $grey.Dispose(); $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "patched $name"
}

Write-Output "patched strip5-5.png"
