#requires -Version 5.1
# Generates a red-themed favicon (white phone glyph on Norlys-red rounded square).
Add-Type -AssemblyName System.Drawing

$red = [System.Drawing.Color]::FromArgb(237, 8, 18)
$white = [System.Drawing.Color]::White

function New-FaviconBmp([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # Rounded-square red background
    $r = [int]($size * 0.22)
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r * 2, $r * 2, 180, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Y, $r * 2, $r * 2, 270, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Bottom - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush $red
    $g.FillPath($brush, $path)

    # White chat bubble with tail
    $whiteBrush = New-Object System.Drawing.SolidBrush $white

    $pad = [single]($size * 0.18)
    $bubbleW = [single]($size - $pad * 2)
    $bubbleH = [single]($size * 0.55)
    $bubbleX = $pad
    $bubbleY = $pad
    $cornerR = [single]($size * 0.18)

    $bubblePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bubblePath.AddArc($bubbleX, $bubbleY, $cornerR * 2, $cornerR * 2, 180, 90)
    $bubblePath.AddArc($bubbleX + $bubbleW - $cornerR * 2, $bubbleY, $cornerR * 2, $cornerR * 2, 270, 90)
    $bubblePath.AddArc($bubbleX + $bubbleW - $cornerR * 2, $bubbleY + $bubbleH - $cornerR * 2, $cornerR * 2, $cornerR * 2, 0, 90)
    $bubblePath.AddArc($bubbleX, $bubbleY + $bubbleH - $cornerR * 2, $cornerR * 2, $cornerR * 2, 90, 90)
    $bubblePath.CloseFigure()
    $g.FillPath($whiteBrush, $bubblePath)

    # Tail (triangle pointing down-left) — built via PointF array
    $tailTopX = [single]($bubbleX + $bubbleW * 0.30)
    $tailTopY = [single]($bubbleY + $bubbleH - 1)
    $p1 = New-Object System.Drawing.PointF -ArgumentList $tailTopX, $tailTopY
    $p2 = New-Object System.Drawing.PointF -ArgumentList ([single]($tailTopX + $size * 0.20)), $tailTopY
    $p3 = New-Object System.Drawing.PointF -ArgumentList ([single]($tailTopX - $size * 0.02)), ([single]($size - $pad * 0.4))
    $tailPts = [System.Drawing.PointF[]]@($p1, $p2, $p3)
    $g.FillPolygon($whiteBrush, $tailPts)

    # Three dots inside (chat indicator)
    $dot = [single]($size * 0.10)
    $dotY = [single]($bubbleY + $bubbleH / 2 - $dot / 2)
    $cx = [single]($bubbleX + $bubbleW / 2)
    $gap = [single]($size * 0.18)
    $redBrush2 = New-Object System.Drawing.SolidBrush $red
    foreach ($offset in @( - $gap, 0, $gap)) {
        $g.FillEllipse($redBrush2, [single]($cx + $offset - $dot / 2), $dotY, $dot, $dot)
    }

    $g.Dispose()
    return $bmp
}

$pub = Join-Path $PSScriptRoot '..\code\admin-chat\public'
$pub = (Resolve-Path $pub).Path

# Standalone PNGs
(New-FaviconBmp 48).Save("$pub\favicon.png", [System.Drawing.Imaging.ImageFormat]::Png)
(New-FaviconBmp 180).Save("$pub\apple-touch-icon.png", [System.Drawing.Imaging.ImageFormat]::Png)

# Multi-size .ico (16, 32, 48) — PNG-compressed entries
$sizes = @(16, 32, 48)
$bmps = $sizes | ForEach-Object { New-FaviconBmp $_ }

$pngBytes = @()
foreach ($b in $bmps) {
    $mss = New-Object System.IO.MemoryStream
    $b.Save($mss, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes += , ($mss.ToArray())
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]$s)
    $bw.Write([byte]$s)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$pngBytes[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngBytes[$i].Length
}
foreach ($pb in $pngBytes) { $bw.Write($pb) }

[System.IO.File]::WriteAllBytes("$pub\favicon.ico", $ms.ToArray())
$bw.Dispose()

Write-Host "Wrote:"
Get-ChildItem "$pub\favicon.ico", "$pub\favicon.png", "$pub\apple-touch-icon.png" | Format-Table Name, Length
