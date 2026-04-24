# Generate a brand red-themed OG image (1200x630) matching the favicon
# Output: code/admin-chat/public/og-image.png

Add-Type -AssemblyName System.Drawing

$red = [System.Drawing.Color]::FromArgb(237, 8, 18)   # #ED0812 brand
$redDark = [System.Drawing.Color]::FromArgb(180, 5, 12)
$white = [System.Drawing.Color]::White
$whiteDim = [System.Drawing.Color]::FromArgb(235, 235, 235)

$W = 1200
$H = 630

$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.InterpolationMode = 'HighQualityBicubic'
$g.PixelOffsetMode = 'HighQuality'
$g.TextRenderingHint = 'AntiAliasGridFit'

# --- Background: vertical red gradient ---
$rect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
$gradBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect, $red, $redDark, [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
$g.FillRectangle($gradBrush, $rect)

# --- Chat bubble glyph on the left (matches favicon) ---
# White rounded square containing a smaller red chat bubble with 3 white dots
$glyphSize = 320
$glyphX = 90
$glyphY = ($H - $glyphSize) / 2

# White rounded background card behind the glyph
$cardR = 56
$cardRect = New-Object System.Drawing.Rectangle($glyphX, $glyphY, $glyphSize, $glyphSize)
$cardPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$cardPath.AddArc($cardRect.X, $cardRect.Y, $cardR * 2, $cardR * 2, 180, 90)
$cardPath.AddArc($cardRect.Right - $cardR * 2, $cardRect.Y, $cardR * 2, $cardR * 2, 270, 90)
$cardPath.AddArc($cardRect.Right - $cardR * 2, $cardRect.Bottom - $cardR * 2, $cardR * 2, $cardR * 2, 0, 90)
$cardPath.AddArc($cardRect.X, $cardRect.Bottom - $cardR * 2, $cardR * 2, $cardR * 2, 90, 90)
$cardPath.CloseFigure()
$whiteBrush = New-Object System.Drawing.SolidBrush($white)
$g.FillPath($whiteBrush, $cardPath)

# Red chat bubble inside the white card
$pad = 56
$bubbleX = $glyphX + $pad
$bubbleY = $glyphY + $pad
$bubbleW = $glyphSize - 2 * $pad
$bubbleH = [int]($bubbleW * 0.78)
$bubbleR = 36
$bubbleRect = New-Object System.Drawing.Rectangle($bubbleX, $bubbleY, $bubbleW, $bubbleH)
$bubblePath = New-Object System.Drawing.Drawing2D.GraphicsPath
$bubblePath.AddArc($bubbleRect.X, $bubbleRect.Y, $bubbleR * 2, $bubbleR * 2, 180, 90)
$bubblePath.AddArc($bubbleRect.Right - $bubbleR * 2, $bubbleRect.Y, $bubbleR * 2, $bubbleR * 2, 270, 90)
$bubblePath.AddArc($bubbleRect.Right - $bubbleR * 2, $bubbleRect.Bottom - $bubbleR * 2, $bubbleR * 2, $bubbleR * 2, 0, 90)
$bubblePath.AddArc($bubbleRect.X, $bubbleRect.Bottom - $bubbleR * 2, $bubbleR * 2, $bubbleR * 2, 90, 90)
$bubblePath.CloseFigure()
$redBrush = New-Object System.Drawing.SolidBrush($red)
$g.FillPath($redBrush, $bubblePath)

# Bubble tail (triangle pointing down-left)
$tailPts = [System.Drawing.PointF[]]@(
    (New-Object System.Drawing.PointF -ArgumentList ([single]($bubbleX + $bubbleW * 0.22)), ([single]($bubbleY + $bubbleH))),
    (New-Object System.Drawing.PointF -ArgumentList ([single]($bubbleX + $bubbleW * 0.10)), ([single]($bubbleY + $bubbleH + 38))),
    (New-Object System.Drawing.PointF -ArgumentList ([single]($bubbleX + $bubbleW * 0.40)), ([single]($bubbleY + $bubbleH)))
)
$g.FillPolygon($redBrush, $tailPts)

# 3 white dots inside the red bubble
$dotR = 18
$dotY = $bubbleY + ($bubbleH / 2) - $dotR
$dotGap = 30
$dotMidX = $bubbleX + ($bubbleW / 2) - $dotR
$g.FillEllipse($whiteBrush, [single]($dotMidX - 2 * $dotR - $dotGap), [single]$dotY, [single](2 * $dotR), [single](2 * $dotR))
$g.FillEllipse($whiteBrush, [single]$dotMidX, [single]$dotY, [single](2 * $dotR), [single](2 * $dotR))
$g.FillEllipse($whiteBrush, [single]($dotMidX + 2 * $dotR + $dotGap), [single]$dotY, [single](2 * $dotR), [single](2 * $dotR))

# --- Text on the right ---
$textX = $glyphX + $glyphSize + 70
$textW = $W - $textX - 60

# Wordmark: CALLCENTER
$titleFont = New-Object System.Drawing.Font('Segoe UI', 78, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$titleBrush = New-Object System.Drawing.SolidBrush($white)
$g.DrawString('CALLCENTER', $titleFont, $titleBrush, [single]$textX, [single]200)

# Subtitle / tagline
$subFont = New-Object System.Drawing.Font('Segoe UI', 30, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$subBrush = New-Object System.Drawing.SolidBrush($whiteDim)
$g.DrawString('AI-drevet kundeservice', $subFont, $subBrush, [single]$textX, [single]300)
$bullet = [char]0x2022  # •
$g.DrawString("Onboarding  $bullet  Regningsforklaring  $bullet  MFA", $subFont, $subBrush, [single]$textX, [single]345)

$g.Dispose()

$out = Join-Path $PSScriptRoot '..\code\admin-chat\public\og-image.png'
$out = [System.IO.Path]::GetFullPath($out)
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host "Wrote: $out ($((Get-Item $out).Length) bytes)"
