# Captures the live NetPilot window (works without admin, which is why the test build is
# launched with app.test.manifest). Optionally clicks a point inside the window first - handy
# for driving the UI from an automated run, since UIPI only blocks lower -> higher windows.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\CaptureLive.ps1 -Out x.png -ClickX 128 -ClickY 717

param(
    [string]$Out = (Join-Path $PSScriptRoot "..\..\live.png"),
    [int]$ClickX = -1,
    [int]$ClickY = -1,
    [switch]$UsePrintWindow,
    [switch]$HideOther
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
$code = @"
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public const uint LD = 0x0002;
  public const uint LU = 0x0004;
  public const uint KEYUP = 0x0002;
  public const int SW_RESTORE = 9;
  public const int SW_MINIMIZE = 6;
}
"@
Add-Type -TypeDefinition $code

$p = Get-Process -Name NetPilot -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { throw "NetPilot is not running" }
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { throw "NetPilot has no main window (still starting?)" }

$r = New-Object W+RECT
[void][W]::GetWindowRect($h, [ref]$r)
Write-Host ("window: {0},{1} - {2},{3}" -f $r.Left, $r.Top, $r.Right, $r.Bottom)

# Windows refuses a plain SetForegroundWindow from a background process; the ALT tap is the
# documented way to be allowed to steal focus. -HideOther first minimizes whatever is in
# front (the harness terminal), which otherwise covers the window we want to photograph.
if ($HideOther) {
    $fg = [W]::GetForegroundWindow()
    if ($fg -ne [IntPtr]::Zero -and $fg -ne $h) { [void][W]::ShowWindow($fg, [W]::SW_MINIMIZE) }
    Start-Sleep -Milliseconds 400
}
[void][W]::ShowWindow($h, [W]::SW_RESTORE)
[W]::keybd_event(0x12, 0, 0, [IntPtr]::Zero)
[void][W]::SetForegroundWindow($h)
[void][W]::BringWindowToTop($h)
[W]::keybd_event(0x12, 0, [W]::KEYUP, [IntPtr]::Zero)
Start-Sleep -Milliseconds 900

if ($ClickX -ge 0 -and $ClickY -ge 0) {
    [void][W]::SetCursorPos($r.Left + $ClickX, $r.Top + $ClickY)
    Start-Sleep -Milliseconds 250
    [W]::mouse_event([W]::LD, 0, 0, 0, [IntPtr]::Zero)
    [W]::mouse_event([W]::LU, 0, 0, 0, [IntPtr]::Zero)
    Write-Host ("clicked {0},{1} (window local)" -f $ClickX, $ClickY)
    Start-Sleep -Seconds 3
}

$w = $r.Right - $r.Left
$h2 = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h2
$g = [System.Drawing.Graphics]::FromImage($bmp)
# PrintWindow asks the window to render itself, so a window that is not on top still
# produces its real content instead of whatever happens to be on screen.
$hdc = $g.GetHdc()
if ($UsePrintWindow) {
    # Only works for GDI windows; a WPF surface renders black here.
    [void][W]::PrintWindow($h, $hdc, 2)
    $g.ReleaseHdc($hdc)
}
else {
    $g.ReleaseHdc($hdc)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $h2))
}
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $Out"
