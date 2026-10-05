Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;using System.Runtime.InteropServices;
public class CAP{
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint f);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,UIntPtr e);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [StructLayout(LayoutKind.Sequential)] public struct RECT{public int L,T,R,B;}
}
"@

$out = $PSScriptRoot
$p = Get-Process NetPilot -ErrorAction SilentlyContinue
if (-not $p) { "NO PROCESS"; exit 1 }
$h = $p.MainWindowHandle
$TOP  = [IntPtr]::new(-1)
$NOTOP = [IntPtr]::new(-2)

[void][CAP]::SetWindowPos($h, $TOP, 0,0,0,0, 0x13)
Start-Sleep -Milliseconds 600
$R = New-Object CAP+RECT
[void][CAP]::GetWindowRect($h, [ref]$R)
$X = $R.L; $Y = $R.T
"window at $X,$Y"

$nav = @(
  @(117,"np-dash"),      @(161,"np-dns"),      @(206,"np-bench"),   @(251,"np-smartdns"),
  @(295,"np-monitor"),   @(340,"np-perapp"),   @(385,"np-limiter"), @(429,"np-sched"),
  @(474,"np-history"),   @(518,"np-events"),   @(563,"np-tools"),   @(607,"np-adapter"),
  @(652,"np-profiles"),  @(697,"np-settings")
)

foreach ($n in $nav) {
  [void][CAP]::SetWindowPos($h, $TOP, 0,0,0,0, 0x13)
  [void][CAP]::SetForegroundWindow($h)
  Start-Sleep -Milliseconds 350
  [void][CAP]::SetCursorPos(($X + 115), ($Y + $n[0]))
  Start-Sleep -Milliseconds 300
  [CAP]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 150
  [CAP]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 2200

  $rr = New-Object CAP+RECT
  [void][CAP]::GetWindowRect($h, [ref]$rr)
  $w = $rr.R - $rr.L
  $ht = $rr.B - $rr.T
  if ($w -gt 4 -and $ht -gt 4) {
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rr.L, $rr.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $out ($n[1] + ".png")), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "OK $($n[1])"
  } else {
    "FAILRECT $($n[1])"
  }
}

[void][CAP]::SetWindowPos($h, $NOTOP, 0,0,0,0, 0x13)
"DONE"
