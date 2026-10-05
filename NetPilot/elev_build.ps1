# ASCII only (PS 5.1 reads BOM-less files as ANSI). Runs elevated: kill, build, relaunch.
$root = $PSScriptRoot
$dotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
$log = Join-Path $root 'build.log'

Get-Process -Name NetPilot -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700

Remove-Item (Join-Path $root 'st-*.png') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $root 'selftest.txt') -ErrorAction SilentlyContinue

$out = & $dotnet build (Join-Path $root 'NetPilot.csproj') -v m 2>&1
$code = $LASTEXITCODE
$text = ($out | Out-String) + "EXIT=$code"
$text | Out-File -FilePath $log -Encoding utf8

if ($code -eq 0) {
    Start-Process -FilePath (Join-Path $root 'bin\Debug\net8.0-windows\NetPilot.exe') -ArgumentList '--selftest'
}
'done' | Out-File -FilePath (Join-Path $root 'elev_done.txt') -Encoding utf8
