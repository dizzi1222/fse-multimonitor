# Generates assets/icon.ico from vector shapes (GDI+). Reproducible, no external tools.
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = "Stop"
$outIcon = Join-Path $PSScriptRoot "icon.ico"
$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

$canvas = New-Object System.Drawing.Bitmap(1024, 1024)
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

$navy = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 12, 18, 26))
$panel = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 16, 26, 38))
$panel2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 15, 23, 34))
$cyan = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 57, 208, 255), 14)
$cyanThin = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 57, 208, 255), 7)
$rim = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 30, 58, 79), 12)
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$tile = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 30, 51, 72))

# Rounded square background with a subtle rim.
$background = New-RoundedPath 32 32 960 960 190
$g.FillPath($navy, $background)
$g.DrawPath($rim, $background)

# Left monitor: game surface with a play triangle.
$left = New-RoundedPath 128 250 560 450 40
$g.FillPath($panel, $left)
$g.DrawPath($cyan, $left)
$play = [System.Drawing.PointF[]]@(
    (New-Object System.Drawing.PointF(330, 360)),
    (New-Object System.Drawing.PointF(330, 590)),
    (New-Object System.Drawing.PointF(540, 475))
)
$g.FillPolygon($white, $play)

# Stand for the left monitor.
$stand = New-RoundedPath 370 700 80 70 12
$g.FillPath($panel, $stand)
$base = New-RoundedPath 320 770 180 34 17
$g.FillPath($panel, $base)
$g.DrawPath($cyanThin, $base)

# Shadow and smaller second monitor with a 2x2 thumbnail grid.
$shadow = New-RoundedPath 596 508 340 330 34
$g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 0, 0, 0))), $shadow)
$right = New-RoundedPath 576 488 340 330 34
$g.FillPath($panel2, $right)
$g.DrawPath($cyan, $right)

$tiles = @(
    (New-RoundedPath 610 528 130 90 10),
    (New-RoundedPath 752 528 130 90 10),
    (New-RoundedPath 610 632 130 90 10),
    (New-RoundedPath 752 632 130 90 10)
)
foreach ($tilePath in $tiles) {
    $g.FillPath($tile, $tilePath)
    $g.DrawPath($cyanThin, $tilePath)
}

$g.Dispose()

function New-IconPng([System.Drawing.Image]$source, [int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.DrawImage($source, 0, 0, $size, $size)
    $graphics.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return $stream.ToArray()
}

$images = New-Object System.Collections.Generic.List[byte[]]
foreach ($size in $sizes) { $images.Add((New-IconPng $canvas $size)) }
$canvas.Dispose()

$stream = [System.IO.File]::Create($outIcon)
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([UInt16]0)                 # reserved
$writer.Write([UInt16]1)                 # type: icon
$writer.Write([UInt16]$sizes.Count)      # image count
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $bytes = $images[$i]
    $dimension = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([Byte]$dimension)      # width (0 = 256)
    $writer.Write([Byte]$dimension)      # height
    $writer.Write([Byte]0)               # palette
    $writer.Write([Byte]0)               # reserved
    $writer.Write([UInt16]1)             # planes
    $writer.Write([UInt16]32)            # bit count
    $writer.Write([UInt32]$bytes.Length)
    $writer.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($bytes in $images) { $writer.Write($bytes) }
$writer.Flush()
$writer.Close()
$stream.Close()

Write-Host "Wrote $outIcon ($([math]::Round((Get-Item -LiteralPath $outIcon).Length / 1KB, 1)) KB, $($sizes.Count) sizes)"
