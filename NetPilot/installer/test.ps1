# End-to-end installer test: install -> run the installed app -> uninstall.
# ASCII only (PS 5.1 reads BOM-less files as ANSI).
$ErrorActionPreference = 'Continue'
$setup  = 'E:\op dn\NetPilot\dist\NetPilot-Setup.exe'
$target = 'C:\Users\Public\NetPilotInstallTest'
$rep    = 'E:\op dn\NetPilot\installer\test-report.txt'
$out = @()

function Reg-View {
    $rk = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try { return $rk } finally { }
}

# ---- 1. install ------------------------------------------------------------
Get-Process -Name NetPilot -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue

$p = Start-Process -FilePath $setup -ArgumentList '/SILENT', ('/DIR=' + $target) -Verb RunAs -PassThru -Wait
$out += "install exit=$($p.ExitCode)"
$exe = Join-Path $target 'NetPilot.exe'
$out += "install: exe=$(Test-Path $exe)"
$out += "install: files=" + @(Get-ChildItem $target -Recurse -File -ErrorAction SilentlyContinue).Count
$out += "install: uninstall.dat=$(Test-Path (Join-Path $target 'uninstall.dat'))"

$rk = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
$sub = $rk.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NetPilot')
if ($sub) {
    $out += "install: reg=OK publisher=" + $sub.GetValue('Publisher') + " ver=" + $sub.GetValue('DisplayVersion')
    $out += "install: regUninstall=" + $sub.GetValue('UninstallString')
    $sub.Close()
} else { $out += 'install: reg=MISSING' }
$rk.Close()

$sm = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\NetPilot.lnk'
$db = Join-Path $env:Public 'Desktop\NetPilot.lnk'
$out += "install: startmenu.lnk=$(Test-Path $sm)"
$out += "install: desktop.lnk=$(Test-Path $db)"

# ---- 2. run the installed copy --------------------------------------------
$before = 0
$cl = Join-Path $env:LOCALAPPDATA 'NetPilot\crash.log'
if (Test-Path $cl) { $before = (Get-Item $cl).Length }

Start-Process -FilePath $exe
Start-Sleep -Seconds 10
$pr = Get-Process -Name NetPilot -ErrorAction SilentlyContinue
$out += "run: alive=$($null -ne $pr) title='" + $(if ($pr) { $pr.MainWindowTitle } else { '' }) + "'"
Get-Process -Name NetPilot -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$after = 0
if (Test-Path $cl) { $after = (Get-Item $cl).Length }
$out += "run: crashlog grew=" + ($after - $before)

# ---- 3. uninstall ----------------------------------------------------------
$p2 = Start-Process -FilePath $setup -ArgumentList '/UNINSTALL', '/SILENT' -Verb RunAs -PassThru -Wait
$out += "uninstall exit=$($p2.ExitCode)"
$out += "uninstall: exe gone=$(-not (Test-Path $exe))"
$out += "uninstall: files left=" + @(Get-ChildItem $target -Recurse -File -ErrorAction SilentlyContinue).Count
$out += "uninstall: dir exists=$(Test-Path $target)"
$out += "uninstall: startmenu.lnk gone=$(-not (Test-Path $sm))"
$out += "uninstall: desktop.lnk gone=$(-not (Test-Path $db))"

$rk2 = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
$sub2 = $rk2.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NetPilot')
$out += "uninstall: regKey gone=" + ($null -eq $sub2)
if ($sub2) { $sub2.Close() }
$rk2.Close()

$out | Set-Content -Path $rep -Encoding UTF8
