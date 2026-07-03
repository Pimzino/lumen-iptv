#!/usr/bin/env pwsh
# Generates the Lumen app icon (multi-resolution .ico) from a drawn mark:
# a rounded square with the accent-blue gradient and a soft glowing "play" lens.
# Run once to (re)create src/Lumen.App/Assets/lumen.ico.

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngStreams = @()

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $pad = [Math]::Max(1, [int]($size * 0.06))
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))
    $radius = [int]($size * 0.22)

    # Rounded-rect path.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    # Base fill: deep app background.
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 11, 13, 16))
    $g.FillPath($bg, $path)

    # Accent gradient wash.
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(230, 76, 141, 255),
        [System.Drawing.Color]::FromArgb(200, 59, 116, 217),
        45.0)
    $g.SetClip($path)
    $g.FillRectangle($grad, $rect)
    $g.ResetClip()

    # Glowing lens (the signature ambient dot) top-right.
    $glowSize = [int]($size * 0.5)
    $glowRect = New-Object System.Drawing.Rectangle(
        [int]($rect.Right - $glowSize * 0.85), [int]($rect.Y - $glowSize * 0.15), $glowSize, $glowSize)
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse($glowRect)
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $glow.CenterColor = [System.Drawing.Color]::FromArgb(120, 255, 255, 255)
    $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.SetClip($path)
    $g.FillPath($glow, $glowPath)
    $g.ResetClip()

    # Play triangle.
    $cx = $size * 0.5
    $cy = $size * 0.5
    $tri = $size * 0.17
    $points = @(
        (New-Object System.Drawing.PointF(($cx - $tri * 0.8), ($cy - $tri))),
        (New-Object System.Drawing.PointF(($cx - $tri * 0.8), ($cy + $tri))),
        (New-Object System.Drawing.PointF(($cx + $tri), $cy))
    )
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 242, 244, 247))
    $g.FillPolygon($white, $points)

    $g.Dispose()
    return $bmp
}

# Build the .ico file manually (icon dir + PNG-compressed entries, valid for modern Windows).
$outDir = Join-Path $PSScriptRoot '..\src\Lumen.App\Assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outPath = Join-Path $outDir 'lumen.ico'

$ms = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($ms)

# ICONDIR
$writer.Write([UInt16]0)            # reserved
$writer.Write([UInt16]1)            # type = icon
$writer.Write([UInt16]$sizes.Count) # image count

$imageData = @()
foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $imgStream = New-Object System.IO.MemoryStream
    $bmp.Save($imgStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $imgStream.ToArray()
    $imageData += ,$bytes
    $bmp.Dispose()
    $imgStream.Dispose()
}

# Directory entries.
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $bytes = $imageData[$i]
    $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size }))) # width
    $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size }))) # height
    $writer.Write([byte]0)     # palette
    $writer.Write([byte]0)     # reserved
    $writer.Write([UInt16]1)   # color planes
    $writer.Write([UInt16]32)  # bpp
    $writer.Write([UInt32]$bytes.Length)
    $writer.Write([UInt32]$offset)
    $offset += $bytes.Length
}

foreach ($bytes in $imageData) {
    $writer.Write($bytes)
}

[System.IO.File]::WriteAllBytes($outPath, $ms.ToArray())
$writer.Dispose()
$ms.Dispose()
Write-Host "Wrote $outPath ($((Get-Item $outPath).Length) bytes)"
