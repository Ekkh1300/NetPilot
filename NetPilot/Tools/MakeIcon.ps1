# Generates Assets\NetPilot.ico - the application icon (exe / shortcut / Alt-Tab / taskbar).
# Same artwork the tray draws at runtime (Services\TrayService.MakeIcon): a cyan->indigo
# gradient badge with a white ring and a white "N".
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\MakeIcon.ps1
#
# Windows PowerShell 5.1 compatible (System.Drawing, no .NET 8 needed).

param(
    [string]$Out = (Join-Path $PSScriptRoot '..\Assets\NetPilot.ico')
)

Add-Type -AssemblyName System.Drawing

$Sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-Badge {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.Clear([System.Drawing.Color]::Transparent)

        # Squircle badge that fills the tile - a circle leaves a transparent notch in the
        # corners, which Windows shows as a ragged white blob on light wallpapers.
        $pad = [math]::Max(1.0, $Size * 0.035)
        $w = $Size - 2 * $pad
        $rad = $w * 0.24
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $rad * 2
        $path.AddArc($pad, $pad, $rad, $rad, 180, 90)
        $path.AddArc($pad + $w - $rad, $pad, $rad, $rad, 270, 90)
        $path.AddArc($pad + $w - $rad, $pad + $w - $rad, $rad, $rad, 0, 90)
        $path.AddArc($pad, $pad + $w - $rad, $rad, $rad, 90, 90)
        $path.CloseFigure()

        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.RectangleF($pad, $pad, $w, $w)),
            [System.Drawing.Color]::FromArgb(255, 56, 189, 248),
            [System.Drawing.Color]::FromArgb(255, 129, 140, 248),
            55.0)
        $g.FillPath($brush, $path)
        $brush.Dispose()
        $path.Dispose()

        # White ring, then the letter on top of it. At 16/24 px a stroked ring plus a
        # hinted font turns into mush, so the small tiles drop the ring and draw a bolder,
        # geometric N out of plain rectangles.
        $small = $Size -le 32
        if (-not $small) {
            $ringW = [math]::Max(1.6, $Size * 0.058)
            $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(235, 255, 255, 255), $ringW)
            $inset = $pad + $w * 0.17
            $g.DrawEllipse($pen, $inset, $inset, $w - 2 * $inset, $w - 2 * $inset)
            $pen.Dispose()
        }

        if ($small) {
            $bw = [math]::Max(2.0, $Size * 0.115)
            $x0 = $Size * 0.275
            $y0 = $Size * 0.255
            $gw = $Size * 0.45
            $gh = $Size * 0.49
            $solid = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
            $g.FillRectangle($solid, $x0, $y0, $bw, $gh)
            $g.FillRectangle($solid, ($x0 + $gw - $bw), $y0, $bw, $gh)
            $poly = New-Object 'System.Drawing.PointF[]' 4
            $poly[0] = New-Object System.Drawing.PointF(($x0 + $bw), $y0)
            $poly[1] = New-Object System.Drawing.PointF(($x0 + 2 * $bw), $y0)
            $poly[2] = New-Object System.Drawing.PointF(($x0 + $gw), ($y0 + $gh))
            $poly[3] = New-Object System.Drawing.PointF(($x0 + $gw - $bw), ($y0 + $gh))
            $g.FillPolygon($solid, $poly)
            $solid.Dispose()
        }
        else {
            $font = New-Object System.Drawing.Font('Segoe UI', ($Size * 0.46), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            $fmt = New-Object System.Drawing.StringFormat
            $fmt.Alignment = [System.Drawing.StringAlignment]::Center
            $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
            # Segoe UI's cap sits high in its line box; nudge down so the letter is centred in
            # the badge rather than the line.
            $fmt.FormatFlags = $fmt.FormatFlags -bor [System.Drawing.StringFormatFlags]::NoClip
            $g.DrawString('N', $font, [System.Drawing.Brushes]::White,
                (New-Object System.Drawing.RectangleF(0, ($Size * 0.04), $Size, $Size)), $fmt)
            $fmt.Dispose()
            $font.Dispose()
        }
    }
    finally { $g.Dispose() }
    # The comma keeps PowerShell from unrolling the object into the pipeline.
    return , $bmp
}

# Icon entries are 32bpp bottom-up DIBs (BMP): the resource compiler that turns the .ico into
# the .res behind ApplicationIcon rejects the PNG-in-ICO form on some SDK versions.
function Get-DibBytes {
    param([System.Drawing.Bitmap]$Bmp, [int]$Size)

    $data = New-Object byte[] ($Size * $Size * 4)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)
    $locked = $Bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try { [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $data, 0, $data.Length) }
    finally { $Bmp.UnlockBits($locked) }

    $stride = $Size * 4
    $rows = New-Object byte[] ($stride * $Size)
    for ($y = 0; $y -lt $Size; $y++) {
        [Array]::Copy($data, ($Size - 1 - $y) * $stride, $rows, $y * $stride, $stride)
    }

    $maskStride = [int](([math]::Ceiling($Size / 32.0)) * 4)
    $mask = New-Object byte[] ($maskStride * $Size)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER
    $bw.Write([int]40)                 # biSize
    $bw.Write([int]$Size)              # biWidth
    $bw.Write([int]($Size * 2))        # biHeight: XOR + AND masks stacked
    $bw.Write([int16]1)                # biPlanes
    $bw.Write([int16]32)               # biBitCount
    $bw.Write([int]0)                  # biCompression = BI_RGB
    $bw.Write([int]($rows.Length + $mask.Length))
    $bw.Write([int]0); $bw.Write([int]0)
    $bw.Write([int]0); $bw.Write([int]0)
    $bw.Write($rows)
    $bw.Write($mask)
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return , $bytes
}

$images = @()
foreach ($s in $Sizes) {
    $bmp = New-Badge -Size $s
    try { $images += , (Get-DibBytes -Bmp $bmp -Size $s) }
    finally { $bmp.Dispose() }
    Write-Host ("  {0,3}x{0,-3} ok" -f $s)
}

$out = [System.IO.Path]::GetFullPath($Out)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($out)) | Out-Null

$fs = [System.IO.File]::Create($out)
try {
    $w = New-Object System.IO.BinaryWriter($fs)
    $w.Write([int16]0)                 # reserved
    $w.Write([int16]1)                 # type = icon
    $w.Write([int16]$images.Count)
    $offset = 6 + 16 * $images.Count
    for ($i = 0; $i -lt $images.Count; $i++) {
        $img = $images[$i]
        # A dimension byte of 0 means 256.
        $dim = 0
        if ($Sizes[$i] -lt 256) { $dim = $Sizes[$i] }
        $w.Write([byte]$dim)
        $w.Write([byte]$dim)
        $w.Write([byte]0)              # colour count (0 = no palette)
        $w.Write([byte]0)              # reserved
        $w.Write([int16]1)             # planes
        $w.Write([int16]32)            # bits per pixel
        $w.Write([int]$img.Length)
        $w.Write([int]$offset)
        $offset += $img.Length
    }
    foreach ($img in $images) { $w.Write($img) }
    $w.Flush()
}
finally { $fs.Dispose() }

Write-Host ("wrote {0} ({1} bytes, {2} sizes)" -f $out, (Get-Item $out).Length, $images.Count)
