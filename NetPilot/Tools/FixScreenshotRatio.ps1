# Rewrites the store screenshots to the ratio the Play Console validates: 9:16 (or 16:9).
# The phone is 1080x2400 (20:9), so simply cropping to 1080x1920 would cut the app bar and
# half of the last card. Scaling to fit and padding the sides with the app's own background
# keeps every pixel of the screen, keeps the ratio exact, and the bars are invisible because
# they are the same colour as the app background.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$src = 'D:\avdl\shots\raw'
$dst = 'E:\op dn\release\play-store-1.2.0'

$map = [ordered]@{
    '01-dashboard'     = '01-dashboard-health'
    '02-dns'           = '02-dns-servers'
    '03-monitor'       = '03-network-monitor'
    '04-perapp'        = '04-app-usage'
    '05-net-limiter'   = '05-net-limiter'
    '06-network-tools' = '06-network-tools'
    '07-profiles'      = '07-profiles'
    '08-mobile-vpn'    = '08-mobile-vpn'
}

$TARGET_W = 1080
$TARGET_H = 1920          # 9:16
$TRIM_TOP = 52            # Android status bar
$TRIM_BOTTOM = 96         # gesture / navigation bar
$MAX_BYTES = 3MB

foreach ($key in $map.Keys) {
    $img = [System.Drawing.Image]::FromFile((Join-Path $src "$key.png"))

    # 1. trim the system bars
    $contentH = $img.Height - $TRIM_TOP - $TRIM_BOTTOM
    $contentW = $img.Width

    # 2. scale to fit the target height, keeping the aspect ratio
    $newH = $TARGET_H
    $newW = [int][math]::Round($contentW * $newH / $contentH)

    $content = New-Object System.Drawing.Bitmap $newW, $newH, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g1 = [System.Drawing.Graphics]::FromImage($content)
    $g1.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g1.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g1.DrawImage($img,
        (New-Object System.Drawing.Rectangle 0, 0, $newW, $newH),
        (New-Object System.Drawing.Rectangle 0, $TRIM_TOP, $contentW, $contentH),
        [System.Drawing.GraphicsUnit]::Pixel)
    $g1.Dispose()

    # 3. sample the app's own background from the top-left corner for the padding
    $bg = $content.GetPixel(2, 2)

    # 4. pad to exactly 9:16
    $out = New-Object System.Drawing.Bitmap $TARGET_W, $TARGET_H, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.Clear($bg)
    $x = [int](($TARGET_W - $newW) / 2)
    $g2.DrawImage($content, $x, 0, $newW, $newH)
    $g2.Dispose()
    $content.Dispose()
    $img.Dispose()

    $path = Join-Path $dst ($map[$key] + '.png')
    $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $out.Dispose()

    $fi = Get-Item $path
    $ratio = [math]::Round($TARGET_W / $TARGET_H, 4)
    "  {0,-28} {1}x{2}  ratio 1:{3}  {4:N0} KB" -f $fi.Name, $TARGET_W, $TARGET_H, [math]::Round($TARGET_H / $TARGET_W, 3), ($fi.Length / 1KB)
    if ($fi.Length -gt $MAX_BYTES) { "    !! over the 3 MB limit" }
}
"done: 9:16, 1080x1920, all under 3 MB"