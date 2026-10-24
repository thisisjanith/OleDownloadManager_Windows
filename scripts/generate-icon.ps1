<#
.SYNOPSIS
    Regenerates src/OleDownloadManager.App/Assets/app.ico (multi-resolution, PNG-compressed entries).
.DESCRIPTION
    Draws the OleDownloadManager mark (a download arrow over a tray on a rounded blue tile)
    at every standard icon size so it stays crisp at all display scales.
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\src\OleDownloadManager.App\Assets\app.ico'),
    [string]$ExtensionIconDirectory = (Join-Path $PSScriptRoot '..\browser-extensions\chrome\icons')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng([int]$size, [switch]$Png) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size
    $margin = [Math]::Max(0.5, $s * 0.04)
    $tile = New-RoundedRectPath $margin $margin ($s - 2 * $margin) ($s - 2 * $margin) ($s * 0.22)
    $top = [System.Drawing.Color]::FromArgb(255, 0x1A, 0x7F, 0xD4)
    $bottom = [System.Drawing.Color]::FromArgb(255, 0x00, 0x5A, 0xB0)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, $s)), $top, $bottom
    $g.FillPath($brush, $tile)

    $white = [System.Drawing.Color]::White
    $stroke = [Math]::Max(1.5, $s * 0.095)
    $pen = New-Object System.Drawing.Pen $white, $stroke
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $cx = $s / 2
    # Arrow shaft and head
    $g.DrawLine($pen, $cx, $s * 0.22, $cx, $s * 0.58)
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($s * 0.32, $s * 0.43),
        [System.Drawing.PointF]::new($cx, $s * 0.61),
        [System.Drawing.PointF]::new($s * 0.68, $s * 0.43)))
    # Tray
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($s * 0.24, $s * 0.66),
        [System.Drawing.PointF]::new($s * 0.24, $s * 0.77),
        [System.Drawing.PointF]::new($s * 0.76, $s * 0.77),
        [System.Drawing.PointF]::new($s * 0.76, $s * 0.66)))

    $pen.Dispose(); $brush.Dispose(); $tile.Dispose(); $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    if ($size -ge 256 -or $Png) {
        # Large frames are PNG-compressed (supported since Windows Vista).
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Small frames use classic 32-bit DIBs for compatibility with every icon decoder.
        $w = New-Object System.IO.BinaryWriter $stream
        $w.Write([UInt32]40); $w.Write([Int32]$size); $w.Write([Int32]($size * 2))
        $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
        $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
        $w.Write([UInt32]($size * $size * 4 + $maskStride * $size))
        $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bitmap.GetPixel($x, $y)
                $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
            }
        }
        $w.Write((New-Object byte[] ($maskStride * $size)))
        $w.Flush()
    }
    $bitmap.Dispose()
    return , $stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) { , (New-IconPng $size) }

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([UInt16]0)            # reserved
$writer.Write([UInt16]1)            # type: icon
$writer.Write([UInt16]$sizes.Count) # image count
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$images[$i].Length)
    $writer.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()

$dir = Split-Path -Parent $Output
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes((Resolve-Path -LiteralPath $dir).Path + '\' + (Split-Path -Leaf $Output), $out.ToArray())
Write-Host "Wrote $Output ($($out.Length) bytes, sizes: $($sizes -join ', '))"

# Browser extension icons (PNG).
if (-not (Test-Path $ExtensionIconDirectory)) { New-Item -ItemType Directory -Path $ExtensionIconDirectory | Out-Null }
foreach ($size in 16, 32, 48, 128) {
    [System.IO.File]::WriteAllBytes((Join-Path (Resolve-Path -LiteralPath $ExtensionIconDirectory).Path "icon$size.png"), (New-IconPng $size -Png))
}
Write-Host "Wrote extension icons to $ExtensionIconDirectory"
