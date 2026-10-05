Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @"
using System;using System.Runtime.InteropServices;
public class C4{
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint f);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [StructLayout(LayoutKind.Sequential)] public struct RECT{public int L,T,R,B;}
}
"@
[void][C4]::SetProcessDPIAware()

$out = $PSScriptRoot
$p = Get-Process NetPilot -ErrorAction SilentlyContinue
if (-not $p) { "NO PROCESS"; exit 1 }
$h = $p.MainWindowHandle
$TOP = [IntPtr]::new(-1)
$NOTOP = [IntPtr]::new(-2)

[void][C4]::SetWindowPos($h, $TOP, 0,0,0,0, 0x13)
[void][C4]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 800

function Snap($n) {
    $rr = New-Object C4+RECT
    [void][C4]::GetWindowRect($h, [ref]$rr)
    $w = $rr.R - $rr.L
    $ht = $rr.B - $rr.T
    if ($w -lt 4 -or $ht -lt 4) { "FAILRECT $n"; return }
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rr.L, $rr.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $out ($n + ".png")), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "OK $n"
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { "UIA window NOT found"; exit 1 }

$nc = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::RadioButton)
$rbs = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nc)
"nav items: $($rbs.Count)"
if ($rbs.Count -lt 14) { "too few nav items"; exit 1 }

$pages = @("np-dash","np-dns","np-bench","np-smartdns","np-monitor","np-perapp",
           "np-limiter","np-sched","np-history","np-events","np-tools","np-adapter",
           "np-profiles","np-settings")

for ($i = 0; $i -lt $pages.Count; $i++) {
    [void][C4]::SetWindowPos($h, $TOP, 0,0,0,0, 0x13)
    $el = $rbs.Item($i)
    $state = "unknown"
    try {
        $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $state = [string]$tp.Current.ToggleState
        if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
            $tp.Toggle()
        }
    } catch { "  toggle[$i] failed: $($_.Exception.Message)" }
    "  [$i] state before=$state"
    Start-Sleep -Milliseconds 2600
    Snap $pages[$i]
}

[void][C4]::SetWindowPos($h, $NOTOP, 0,0,0,0, 0x13)
"DONE"
