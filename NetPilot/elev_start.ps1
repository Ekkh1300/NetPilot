# ASCII only. Runs elevated: restart NetPilot in self-test mode.
$root = $PSScriptRoot

Get-Process -Name NetPilot -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700

Remove-Item (Join-Path $root 'st-*.png') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $root 'selftest.txt') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $root 'elev_done.txt') -ErrorAction SilentlyContinue

Start-Process -FilePath (Join-Path $root 'bin\Debug\net8.0-windows\NetPilot.exe') -ArgumentList '--selftest'
'done' | Out-File -FilePath (Join-Path $root 'elev_done.txt') -Encoding utf8
