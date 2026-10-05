Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @"
using System;using System.Runtime.InteropServices;
public class D{
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint f);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,UIntPtr e);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [StructLayout(LayoutKind.Sequential)] public struct RECT{public int L,T,R,B;}
 [StructLayout(LayoutKind.Sequential)] public struct POINT{public int X,Y;}
}
"@
[void][D]::SetProcessDPIAware()

$out = $PSScriptRoot
$p = Get-Process NetPilot -ErrorAction SilentlyContinue
if (-not $p) { "NO PROCESS"; exit 1 }
$h = $p.MainWindowHandle
$TOP = [IntPtr]::new(-1); $NOTOP = [IntPtr]::new(-2)

[void][D]::SetWindowPos($h,$TOP,0,0,0,0,0x13)
[void][D]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 800

$R = New-Object D+RECT
[void][D]::GetWindowRect($h,[ref]$R)
$X=$R.L; $Y=$R.T; $W=$R.R-$R.L; $WH=$R.B-$R.T
"window: $X,$Y ${W}x${WH}"

function Snap($n){
  $rr = New-Object D+RECT
  [void][D]::GetWindowRect($h,[ref]$rr)
  $w = $rr.R - $rr.L; $ht = $rr.B - $rr.T
  $bmp = New-Object System.Drawing.Bitmap($w,$ht)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($rr.L,$rr.T,0,0,$bmp.Size)
  $bmp.Save((Join-Path $out ($n + ".png")), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}

# --- 1. pixel click + cursor verification ---
[void][D]::SetCursorPos(($X+115), ($Y+161))
Start-Sleep -Milliseconds 300
$c = New-Object D+POINT
[void][D]::GetCursorPos([ref]$c)
"cursor after SetCursorPos: $($c.X),$($c.Y)   (expected $($X+115),$($Y+161))"

[D]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 150
[D]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 2000
Snap "dbg-click"
"clicked; screenshot dbg-click.png saved"

# --- 2. UIA invoke ---
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { "UIA: window NOT found" }
else {
  "UIA: window found '$($win.Current.Name)'"
  $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  "UIA: descendants = $($all.Count)"
  $nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)
  $rbs = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nc)
  "UIA: radio buttons = $($rbs.Count)"
  for ($i=0; $i -lt [math]::Min($rbs.Count,16); $i++) { "  [$i] '$($rbs.Item($i).Current.Name)'" }
  $tc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, [string]"بنچ‌مارک DNS")
  $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($nc,$tc)))
  if (-not $el) { "UIA: 'بنچ‌مارک DNS' not found" }
  else {
    try {
      $pat = $el.GetCurrentPattern([System.Windows.Automation.Patterns]::InvokePattern)
      $pat.Invoke()
      "UIA: invoked OK"
      Start-Sleep -Milliseconds 2500
      Snap "dbg-uia"
      "screenshot dbg-uia.png saved"
    } catch { "UIA invoke failed: $($_.Exception.Message)" }
  }
}

[void][D]::SetWindowPos($h,$NOTOP,0,0,0,0,0x13)
"DONE"
