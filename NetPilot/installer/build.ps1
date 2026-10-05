# Builds the NetPilot installer. ASCII only (PS 5.1 reads BOM-less files as ANSI).
#   1. publish a self-contained win-x64 build (runs on any PC, no .NET install needed)
#   2. zip the payload
#   3. compile installer\Setup.cs with the C# 5 compiler that ships inside Windows
#      and embed the payload as a manifest resource -> one portable setup exe
$ErrorActionPreference = 'Stop'

# From this script's own location: ...\NetPilot\installer -> ...\NetPilot. This is what makes
# the installer job in CI possible at all - it was pointed at one developer's drive.
$root   = Split-Path -Parent $PSScriptRoot
$stage  = Join-Path $env:TEMP 'netpilot-payload'
$dist   = Join-Path $root 'dist'
$zip    = Join-Path $dist 'payload.zip'
$out    = Join-Path $dist 'NetPilot-Setup.exe'
$dotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
$csc    = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $dotnet)) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
if (-not (Test-Path $csc))    { throw "csc.exe not found at $csc" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$stage = Join-Path $env:TEMP 'netpilot-payload'
# TEMP can be the 8.3 short name (C:\Users\NAME~1\...) on some machines. The FileSystem
# provider refuses to delete through a short path, so resolve to the long path first.
if (Test-Path -LiteralPath $stage) { $stage = (Get-Item -LiteralPath $stage).FullName }
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue

Write-Output '[1/3] publish self-contained payload'
& $dotnet publish (Join-Path $root 'NetPilot.csproj') -c Release -r win-x64 --self-contained true `
    -o $stage -v q /nologo 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "publish failed (EXIT=$LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $stage 'NetPilot.exe'))) { throw 'publish produced no NetPilot.exe' }

$files  = Get-ChildItem $stage -Recurse -File
$sizeMB = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Output ("      payload: {0} files, {1} MB" -f $files.Count, $sizeMB)

Write-Output '[2/3] compress payload'
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$zipMB = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Output ("      payload.zip: {0} MB" -f $zipMB)

Write-Output '[3/3] compile setup'
Remove-Item $out -Force -ErrorAction SilentlyContinue
$manifest = Join-Path $root 'installer\setup.manifest'
$setupCs  = Join-Path $root 'installer\Setup.cs'
$refs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/debug-',
    "/out:$out",
    "/win32manifest:$manifest",
    "/resource:$zip,netpilot.payload.zip",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
    $setupCs
)
& $csc @refs 2>&1
if ($LASTEXITCODE -ne 0) { throw "csc failed (EXIT=$LASTEXITCODE)" }

$setupMB = [math]::Round((Get-Item $out).Length / 1MB, 1)
$md5 = (Get-FileHash $out -Algorithm MD5).Hash
Write-Output ("OK  {0}  ({1} MB)  md5={2}" -f $out, $setupMB, $md5)
