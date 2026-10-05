# Builds the Google Play feature graphic (1024x500, 24-bit RGB, no alpha).
# The mark is the same compass needle inside a signal ring as the launcher icon
# (drawable/ic_launcher_foreground.xml), drawn with plain GDI+ so nothing has to be
# installed: no design tool, no font download, no image library.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = Join-Path (Split-Path -Parent $PSScriptRoot) '..\release\play-store-1.2.0\feature-graphic-1024x500.png'
$W = 1024; $H = 500

$bmp = New-Object System.Drawing.Bitmap $W, $H, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

# ---------------------------------------------------------------- background
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
        (New-Object System.Drawing.Point 0,0), (New-Object System.Drawing.Point 0,$H),
        ([System.Drawing.Color]::FromArgb(255, 9, 13, 22)),
        ([System.Drawing.Color]::FromArgb(255, 18, 26, 44))
$g.FillRectangle($bg, 0, 0, $W, $H)

# A soft cyan bloom behind the mark, and a purple one in the far corner, so the
# flat dark background is not a dead rectangle at thumbnail size.
function Glow($g2, $x, $y, $r, $c, $alpha) {
    # Concentric translucent circles, widest first: a cheap radial falloff that needs
    # neither PathGradientBrush nor an image filter.
    for ($i = 10; $i -ge 1; $i--) {
        $rr = $r * $i / 10.0
        $br = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb([int]($alpha / 10.0), $c))
        $g2.FillEllipse($br, $x - $rr, $y - $rr, 2*$rr, 2*$rr)
        $br.Dispose()
    }
}
Glow $g 812 250 210 ([System.Drawing.Color]::FromArgb(56, 189, 248)) 46
Glow $g 60 470 190 ([System.Drawing.Color]::FromArgb(167, 139, 250)) 30

# ---------------------------------------------------------------- mark
$cx = 812.0; $cy = 250.0
$ringPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 56, 189, 248)), 9
$g.DrawEllipse($ringPen, $cx - 108, $cy - 108, 216, 216)

# needle: upper (bright) and lower (deep blue) halves, same geometry as the vector
$up = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new($cx - 46, $cy + 46),
    [System.Drawing.PointF]::new($cx, $cy - 62),
    [System.Drawing.PointF]::new($cx + 46, $cy + 46),
    [System.Drawing.PointF]::new($cx, $cy + 18)
)
$down = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new($cx - 46, $cy + 46),
    [System.Drawing.PointF]::new($cx, $cy + 18),
    [System.Drawing.PointF]::new($cx + 46, $cy + 46),
    [System.Drawing.PointF]::new($cx, $cy + 62)
)
$g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 56, 189, 248))), $up)
$g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 42, 143, 214))), $down)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 230, 234, 242))), $cx - 19, $cy - 19, 38, 38)

# ---------------------------------------------------------------- text
$fontName = 'Segoe UI'
$h1 = New-Object System.Drawing.Font $fontName, 82, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$h2 = New-Object System.Drawing.Font $fontName, 31, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$h3 = New-Object System.Drawing.Font $fontName, 22, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 244, 247, 252))
$cyan  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 56, 189, 248))
$muted = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 140, 152, 172))

# Plain ASCII separators: PowerShell 5.1 reads a BOM-less script as ANSI, and a middle
# dot typed as UTF-8 renders as mojibake in the final image. "VPN" is deliberately not on
# the badge: the tunnel is a local on-device feature and leading with it overstates what
# the app is for in a store listing.
$g.DrawString('NetPilot', $h1, $white, 78, 118)
$g.DrawString('DNS  /  Network  /  Traffic control', $h2, $cyan, 84, 228)
$g.DrawString('Share this phone''s tunnel with your Windows PC', $h3, $muted, 84, 284)

# a short accent rule so the block reads as one unit
$rule = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
    (New-Object System.Drawing.Point 84,0), (New-Object System.Drawing.Point 300,0),
    ([System.Drawing.Color]::FromArgb(255, 56, 189, 248)),
    ([System.Drawing.Color]::FromArgb(0, 56, 189, 248))
$g.FillRectangle($rule, 84, 336, 216, 4)

$g.Dispose()
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"wrote $out"
$i = [System.Drawing.Image]::FromFile($out)
"  $($i.Width)x$($i.Height)  $($i.PixelFormat)  $([math]::Round((Get-Item $out).Length/1KB)) KB"
$i.Dispose()