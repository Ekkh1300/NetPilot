# Renders the Play Store app icon from the same geometry as the launcher vector
# (res/drawable/ic_launcher_foreground.xml) on the adaptive background colour
# (res/values/colors.xml -> np_icon_bg). No design tool involved.
#
#   .\Tools\MakePlayIcon.ps1            512x512 (what the Play Console asks for)
#   .\Tools\MakePlayIcon.ps1 -Sizes 1024
param([int[]]$Sizes = @(512, 1024))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outDir = 'E:\op dn\release\play-store-1.2.0'

# Brand colours, straight out of the Android resources.
$BG    = [System.Drawing.Color]::FromArgb(255, 0x1A, 0x1F, 0x2B)   # np_icon_bg
$RING  = [System.Drawing.Color]::FromArgb(255, 0x38, 0xBD, 0xF8)   # ic_launcher_foreground
$NEEDLE_UP = [System.Drawing.Color]::FromArgb(255, 0x38, 0xBD, 0xF8)
$NEEDLE_DN = [System.Drawing.Color]::FromArgb(255, 0x2A, 0x8F, 0xD6)
$DOT   = [System.Drawing.Color]::FromArgb(255, 0xE6, 0xEA, 0xF2)

# Vector units are 108x108 with the mark centred on 54,54. A legacy square icon shows
# the whole canvas (the Play Console rounds the corners itself), so the mark is scaled up
# 1.22x over the vector's own size to land at ~63% of the canvas - large enough to stay
# legible at 48px in the launcher.
$k = 1.22

foreach ($S in $Sizes) {
    $bmp = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

    $g.Clear($BG)

    $u   = $S / 108.0
    $cx  = $S / 2.0
    $cy  = $S / 2.0
    $p   = { param($vx, $vy) [System.Drawing.PointF]::new($cx + ($vx - 54) * $u * $k, $cy + ($vy - 54) * $u * $k) }

    # outer ring: strokeWidth 4 in vector units
    $rr  = 28.0 * $u * $k
    $pen = New-Object System.Drawing.Pen $RING, ([float](4.0 * $u * $k))
    $g.DrawEllipse($pen, [float]($cx - $rr), [float]($cy - $rr), [float](2 * $rr), [float](2 * $rr))

    # needle: upper (bright) half  M42,66 L54,34 L66,66 L54,58
    $up = [System.Drawing.PointF[]]@(
        (& $p 42 66), (& $p 54 34), (& $p 66 66), (& $p 54 58))
    # needle: lower (deep blue) half  M42,66 L54,58 L66,66 L54,72
    $dn = [System.Drawing.PointF[]]@(
        (& $p 42 66), (& $p 54 58), (& $p 66 66), (& $p 54 72))

    $g.FillPolygon((New-Object System.Drawing.SolidBrush $NEEDLE_UP), $up)
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $NEEDLE_DN), $dn)

    # centre dot: r = 5 vector units
    $dr = 5.0 * $u * $k
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $DOT),
                  [float]($cx - $dr), [float]($cy - $dr), [float](2 * $dr), [float](2 * $dr))

    $g.Dispose()

    $out = Join-Path $outDir ("play-icon-{0}x{0}.png" -f $S)
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    $i = [System.Drawing.Image]::FromFile($out)
    "  {0,-26} {1}x{2}  {3}  {4:N0} KB" -f (Split-Path $out -Leaf), $i.Width, $i.Height, $i.PixelFormat, ((Get-Item $out).Length / 1KB)
    $i.Dispose()
}