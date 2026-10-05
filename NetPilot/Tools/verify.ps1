param(
    [switch]$Installer,   # also install -> run -> uninstall (prompts for UAC)
    [switch]$Elevated     # run the Windows tests elevated, the way the app itself runs
)

# Runs every automated check in the project and prints one verdict per suite.
#   .\Tools\verify.ps1                  unit tests (Windows) + unit tests & lint (Android)
#   .\Tools\verify.ps1 -Installer       also install -> run -> uninstall (prompts for UAC)
#   .\Tools\verify.ps1 -Elevated        run the Windows tests elevated, the way the app runs
#
# The script also records the machine state it cares about before and after: a test suite
# that passes by changing the user's firewall or proxy settings is worse than no suite.
$ErrorActionPreference = 'Continue'

# Resolved from this script's own location, so a checkout anywhere works.
#
# These were absolute paths into one developer's source tree. Every other machine - including a
# CI runner and including a second developer - would have found no project here and reported
# every suite as missing, which reads as "the project is broken" rather than "the script points
# somewhere that does not exist". The Windows suite in particular could only ever have been run
# by hand from one folder.
$root    = Split-Path -Parent $PSScriptRoot                    # ...\NetPilot
$repo    = Split-Path -Parent $root                            # the repository root
$mobile  = Join-Path $repo 'NetPilotMobile'
$tests   = Join-Path $repo 'NetPilot.Tests\NetPilot.Tests.csproj'
$daemonTests = Join-Path $repo 'NetPilot.Daemon.Tests\NetPilot.Daemon.Tests.csproj'
$coreTests   = Join-Path $repo 'NetPilot.Core.Tests\NetPilot.Core.Tests.csproj'
$dotnet  = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }

# ------------------------------------------------------------------ state fingerprint

function Get-State {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $inet = $null
    try { $inet = Get-ItemProperty $key -ErrorAction Stop } catch { }
    $winHttp = (netsh winhttp show proxy | Select-String 'Proxy Server|Direct access') -join ' '
    $metrics = (Get-NetIPInterface -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.AutomaticMetric -eq 'Disabled' } |
        ForEach-Object { "$($_.InterfaceAlias)=$($_.InterfaceMetric)" }) -join '; '
    return @{
        WinHttp = $winHttp.Trim()
        Proxy    = if ($inet) { "$($inet.ProxyServer) enable=$($inet.ProxyEnable)" } else { 'n/a' }
        Metrics  = $metrics
        Snapshot = (Test-Path "$env:LOCALAPPDATA\NetPilot\mobile_vpn_snapshot.json")
        Marker   = (Test-Path "$root\pending-share.flag")
    }
}

function Show-State($label, $s) {
    Write-Host ("  {0,-9} winhttp='{1}'  proxy='{2}'" -f $label, $s.WinHttp, $s.Proxy)
    Write-Host ("  {0,-9} metrics='{1}'  snapshot={2} marker={3}" -f $label, $s.Metrics, $s.Snapshot, $s.Marker)
}

# ------------------------------------------------------------------ runners

$results = @()

function Add-Result($name, $code, $note = '') {
    $script:results += [pscustomobject]@{
        Suite  = $name
        Result = if ($code -eq 0) { 'PASS' } else { 'FAIL' }
        Note   = $note
    }
}

# Counts a JUnit-style result folder (Gradle writes one XML per test class). More reliable
# than scraping Gradle's console, whose summary line format has changed between versions.
function Get-JUnitCounts($dir) {
    $t = 0; $f = 0
    foreach ($file in @(Get-ChildItem $dir -Recurse -Filter *.xml -ErrorAction SilentlyContinue)) {
        try {
            [xml]$x = Get-Content $file.FullName -Raw
            $t += [int]$x.testsuite.tests
            $f += [int]$x.testsuite.failures + [int]$x.testsuite.errors
        } catch { }
    }
    return "tests=$t failures=$f"
}

Write-Host '== machine state BEFORE ==' -ForegroundColor DarkGray
$before = Get-State
Show-State 'before' $before

Write-Host "`n== Windows unit tests ==" -ForegroundColor Cyan
if ($Elevated) {
    # The app always runs elevated (app.manifest), and two restore paths behave differently
    # when it is: Set-NetIPInterface needs administrator. Run the suite the same way.
    $log = Join-Path $env:TEMP 'netpilot-tests-elevated.txt'
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $cmd = '/c ""' + $dotnet + '" test "' + $tests + '" --nologo > "' + $log + '" 2>&1"'
    $proc = Start-Process -FilePath cmd.exe -ArgumentList $cmd -Verb RunAs -PassThru -WindowStyle Hidden
    $proc.WaitForExit(900000) | Out-Null
    $wt = @(if (Test-Path $log) { Get-Content $log } else { 'the elevated run produced no log' })
    $code = $proc.ExitCode
}
else {
    & $dotnet test $tests --nologo 2>&1 | Tee-Object -Variable wt | Out-Null
    $code = $LASTEXITCODE
}
if ($code -eq 0) {
    $sum = ($wt | Select-String 'Passed!|Total tests:' | Select-Object -Last 1)
    Add-Result 'Windows unit tests' 0 "$(if ($sum) { $sum.Line.Trim() } else { 'ok' })"
}
else {
    Add-Result 'Windows unit tests' 1 (($wt | Select-String 'error|Failed:' | Select-Object -First 3) -join ' | ')
    $wt | Select-String 'Failed |Error Message|Assert' | Select-Object -First 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
}

Write-Host "`n== Android unit tests + lint ==" -ForegroundColor Cyan
Push-Location $mobile
& .\gradlew.bat testDebugUnitTest lintDebug --no-daemon --console=plain 2>&1 | Tee-Object -Variable at | Out-Null
$acode = $LASTEXITCODE
Pop-Location
$atotal = Get-JUnitCounts "$mobile\app\build\test-results"
if ($acode -eq 0) {
    Add-Result 'Android unit tests + lint' 0 $atotal
}
else {
    Add-Result 'Android unit tests + lint' 1 (($at | Select-String 'error:|FAILED' | Select-Object -First 3) -join ' | ')
    $at | Select-String 'FAILED|error:' | Select-Object -First 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
}

if ($Installer) {
    Write-Host "`n== installer (needs UAC) ==" -ForegroundColor Cyan
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$root\installer\test.ps1" 2>&1 | Out-Null
    $rep = "$root\installer\test-report.txt"
    $bad = @(Get-Content $rep -ErrorAction SilentlyContinue |
             Select-String 'False|missing|exit=[1-9]|grew=[1-9]|left=[1-9]')
    if ($bad.Count -eq 0) { Add-Result 'Installer install/run/uninstall' 0 'all checks ok' }
    else { Add-Result 'Installer install/run/uninstall' 1 ($bad -join ' | ') }
}

Write-Host "`n== machine state AFTER ==" -ForegroundColor DarkGray
$after = Get-State
Show-State 'after' $after

$changed = @()
foreach ($k in @('WinHttp', 'Proxy', 'Metrics', 'Snapshot', 'Marker')) {
    if ($before[$k] -ne $after[$k]) { $changed += "$k : '$($before[$k])' -> '$($after[$k])'" }
}
if ($changed.Count -eq 0) { Add-Result 'Machine state untouched' 0 }
else {
    Add-Result 'Machine state untouched' 1 ($changed -join ' | ')
    Write-Host "  a test changed the machine:" -ForegroundColor Red
    $changed | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
}

Write-Host "`n== summary ==" -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object { $_.Result -eq 'FAIL' })
if ($failed.Count -eq 0) { Write-Host 'ALL GREEN' -ForegroundColor Green; exit 0 }
Write-Host ("$($failed.Count) suite(s) FAILED") -ForegroundColor Red
exit 1