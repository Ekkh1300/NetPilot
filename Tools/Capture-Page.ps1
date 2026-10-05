param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$OutPng
)

# Captures a window without touching the mouse or the keyboard.
#
# The obvious way to verify a page is to click its nav entry and screenshot the result. That
# is wrong on a shared desktop: the click lands wherever the pointer happens to be, so a
# running phone share or someone's half-finished document gets disturbed, and a run that
# activates the wrong window produces a screenshot of the wrong app and still looks like
# success. PrintWindow asks the window to render itself into a bitmap we own, so no input is
# synthesised and the capture cannot depend on which window happens to be in front.
#
# PW_RENDERFULLCONTENT (2) is the flag that matters: without it, a window that composits
# with DWM or uses a layered/GPU path comes back black or transparent, which reads as "the
# page is broken" when it is actually the capture.

Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;

public static class PageCapture
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static string Capture(IntPtr hwnd, string path)
    {
        RECT r;
        if (!GetWindowRect(hwnd, out r)) return "GetWindowRect failed";
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return "window has no area: " + w + "x" + h;

        using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            bool ok;
            try { ok = PrintWindow(hwnd, hdc, 2); }
            finally { g.ReleaseHdc(hdc); }
            if (!ok) return "PrintWindow failed";

            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return "ok " + w + "x" + h;
        }
    }
}
'@ -ReferencedAssemblies System.Drawing, System.Windows.Forms

$p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
if (-not $p) { Write-Output "no such process"; exit 1 }
$p.Refresh()
if ($p.MainWindowHandle -eq [IntPtr]::Zero) { Write-Output "no main window"; exit 1 }

Write-Output ([PageCapture]::Capture($p.MainWindowHandle, $OutPng))