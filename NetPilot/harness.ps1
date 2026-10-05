# Non-admin harness: polls the self-test marker and screenshots each page.
# CopyFromScreen works across the UIPI boundary; only input/UIA is blocked.
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class WinCap {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr i, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
[WinCap]::SetProcessDPIAware() | Out-Null

$marker = Join-Path $env:LOCALAPPDATA 'NetPilot\selftest.txt'
$outDir = $PSScriptRoot
$done = @{}
$deadline = (Get-Date).AddSeconds(115)

while ((Get-Date) -lt $deadline) {
    $key = $null
    if (Test-Path $marker) {
        try { $key = (Get-Content $marker -Raw -ErrorAction Stop).Trim() } catch { }
    }

    if ($key -and -not $done.ContainsKey($key)) {
        Start-Sleep -Milliseconds 1700   # let layout/animation settle

        $p = Get-Process -Name NetPilot -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) {
            $h = $p.MainWindowHandle
            $r = New-Object WinCap+RECT
            if ([WinCap]::GetWindowRect($h, [ref]$r)) {
                [WinCap]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x13) | Out-Null
                Start-Sleep -Milliseconds 300
                try {
                    $w = $r.R - $r.L
                    $ht = $r.B - $r.T
                    if ($w -gt 0 -and $ht -gt 0) {
                        $bmp = New-Object System.Drawing.Bitmap $w, $ht
                        $g = [System.Drawing.Graphics]::FromImage($bmp)
                        $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $ht))
                        $g.Dispose()
                        $bmp.Save((Join-Path $outDir "st-$key.png"), [System.Drawing.Imaging.ImageFormat]::Png)
                        $bmp.Dispose()
                        $done[$key] = $true
                        Write-Output "captured $key"
                    }
                } catch { Write-Output "ERR $key : $($_.Exception.Message)" }
                [WinCap]::SetWindowPos($h, [IntPtr](-2), 0, 0, 0, 0, 0x13) | Out-Null
            }
        }
        continue
    }

    Start-Sleep -Milliseconds 250
}

Write-Output ("total=" + $done.Count)
