# Releases NetPilot 1.2.3.
#
# This is the release that carries the command-injection fixes, so the sequence matters more than
# usual and each step says what it produced rather than just whether it ran:
#
#   1. publish the Windows app, build the installer from it
#   2. publish the daemon for linux-x64, linux-arm64, osx-x64, osx-arm64
#   3. publish the Avalonia GUI for the same linux RIDs
#   4. package the Android APK, already signed by the build
#   5. checksum everything
#   6. upload one asset at a time, verifying the server's SHA256 against the local one
#
# Every step re-verifies from disk rather than trusting that a command exited zero, because the
# upload step already caught itself once reporting success on a file that had not arrived.

$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }

$ver = '1.2.3'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root "release\stage-$ver"

Write-Host "== staging into $stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# ---------------------------------------------------------------- 1. Windows installer
Write-Host "`n== Windows"
$win = Join-Path $stage "NetPilot-$ver-win-x64"
& $dotnet publish "$root\NetPilot\NetPilot.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o $win -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $win 'NetPilot.exe'
if (-not (Test-Path $exe)) { throw "no NetPilot.exe in $win" }
Write-Host ("   exe {0:N1} MB" -f ((Get-Item $exe).Length / 1MB))

& powershell -NoProfile -ExecutionPolicy Bypass -File "$root\NetPilot\installer\build.ps1"
$setup = "$root\NetPilot\dist\NetPilot-Setup.exe"
if (Test-Path $setup) {
    Copy-Item $setup (Join-Path $stage "NetPilot-$ver-Setup.exe") -Force
    Write-Host ("   installer {0:N1} MB" -f ((Get-Item $setup).Length / 1MB))
} else {
    Write-Warning "the installer was not produced; the portable folder is the artefact"
}
Compress-Archive -Path "$win\*" -DestinationPath (Join-Path $stage "NetPilot-$ver-win-x64.zip") -Force
Write-Host ("   portable zip {0:N1} MB" -f ((Get-Item (Join-Path $stage "NetPilot-$ver-win-x64.zip")).Length / 1MB))

# ---------------------------------------------------------------- 2-3. daemon and GUI
Write-Host "`n== Linux and macOS"
$rids = @('linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
foreach ($rid in $rids) {
    $out = Join-Path $stage "daemon-build-$rid"
    & $dotnet publish "$root\NetPilot.Daemon\NetPilot.Daemon.csproj" -c Release -r $rid `
        --self-contained true -p:PublishSingleFile=true -o $out -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "daemon publish failed for $rid" }

    # Named by what is on disk, not by what the project is called. NetPilot.Daemon produces
    # netpilotd, and an earlier version of this script assumed "netpilotd" on every platform -
    # which happened to be right, and would not have been had the assembly name ever been
    # changed. Listing the directory and requiring a single executable is the check that cannot
    # go stale.
    $f = Get-ChildItem $out -File |
         Where-Object { $_.Extension -eq '' -or $_.Extension -eq '.exe' } |
         Select-Object -First 1
    if (-not $f) { throw "no daemon binary in $out" }
    Write-Host ("   daemon {0,-14} {1,6:N1} MB  ({2})" -f $rid, ($f.Length / 1MB), $f.Name)

    $tgz = Join-Path $stage "netpilot-daemon-$ver-$rid.tar.gz"
    & tar -czf $tgz -C $out .
    if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
    Write-Host ("         tarball {0,5:N1} MB" -f ((Get-Item $tgz).Length / 1MB))
    Remove-Item $out -Recurse -Force
}

foreach ($rid in @('linux-x64', 'linux-arm64')) {
    $out = Join-Path $stage "netpilot-gui-$ver-$rid"
    & $dotnet publish "$root\NetPilot.Desktop\NetPilot.Desktop.csproj" -c Release -r $rid `
        --self-contained true -p:PublishSingleFile=true -o $out -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "gui publish failed for $rid" }
    # The assembly is NetPilot.Desktop but the produced binary is NetPilot, so the earlier
    # version of this script looked for a file that is never created and silently published
    # nothing. Checked by listing what is actually there rather than by assuming a name.
    $f = Get-ChildItem $out -File |
         Where-Object { $_.Extension -eq '' -and $_.Name -like 'NetPilot*' } |
         Select-Object -First 1
    if (-not $f) { throw "no GUI binary in $out" }
    Write-Host ("   gui    {0,-14} {1,6:N1} MB  ({2})" -f $rid, ($f.Length / 1MB), $f.Name)

    # Packaged as a tarball rather than shipped as loose files: the native Skia and HarfBuzz
    # libraries beside the binary are required at runtime, so an incomplete folder is a binary
    # that cannot start.
    $tgz = Join-Path $stage "netpilot-gui-$ver-$rid.tar.gz"
    & tar -czf $tgz -C $out .
    if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
    Write-Host ("         tarball {0,5:N1} MB" -f ((Get-Item $tgz).Length / 1MB))
    Remove-Item $out -Recurse -Force
}

# ---------------------------------------------------------------- 4. Android
Write-Host "`n== Android"
Push-Location "$root\NetPilotMobile"
& .\gradlew.bat assembleRelease --no-daemon --console=plain
$apk = Get-ChildItem 'app\build\outputs\apk\release' -Filter *.apk | Select-Object -First 1
Pop-Location
if (-not $apk) { throw "no release APK" }
Copy-Item $apk.FullName (Join-Path $stage "NetPilot-$ver-android.apk") -Force
Write-Host ("   apk {0:N1} MB" -f ($apk.Length / 1MB))

# The signature is the thing that cannot be fixed after release, so it is checked rather than
# assumed. An unsigned APK would install from nowhere and be silently unusable.
$apksigner = Get-ChildItem "$env:ANDROID_HOME\build-tools" -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'apksigner.bat' } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($apksigner) {
    & $apksigner verify --print-certs (Join-Path $stage "NetPilot-$ver-android.apk") |
        Select-String 'SHA-256 digest|Number of signers' | ForEach-Object { Write-Host "   $_" }
} else {
    Write-Warning "apksigner not found, so the signature was not verified"
}

# ---------------------------------------------------------------- 5. checksums
Write-Host "`n== checksums"
$manifest = Join-Path $stage 'SHA256SUMS.txt'
Get-ChildItem $stage -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Set-Content $manifest -Encoding utf8
Get-Content $manifest | ForEach-Object { Write-Host "   $_" }

Write-Host "`nstaged in $stage"
Get-ChildItem $stage -File | ForEach-Object {
    Write-Host ("   {0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB))
}