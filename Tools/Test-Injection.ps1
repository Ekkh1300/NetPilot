# Sends real injection payloads at a real NetPilot build and checks they do nothing.
#
# The unit tests call IsValidProxyAddress directly. That proves the validator rejects a string; it
# does not prove the shipped executable does, and this is the second thing that failed review
# round one: a page that compiled, passed every test, and was broken on screen. So this drives the
# actual binary over the actual HTTP API and looks for the one observable an injection leaves
# behind - a file the script wrote.
#
# Every payload tries to drop a marker file. If the app is vulnerable the file appears; if the
# fix holds it never does. That is a direct measurement rather than a proxy for "the string was
# rejected".
#
# Needs the build running and reachable. Skipped, loudly, when it is not - a security test that
# quietly does nothing is worse than no security test.

param(
    # Resolved from this script's own location, not a compiled-in path. A default that names one
    # developer's drive makes the script unusable everywhere else while looking perfectly fine to
    # whoever wrote it - which is exactly what the absolute-path test in NetPilot.Tests exists to
    # catch, and it caught this file the day it was added.
    [string]$Exe        = (Join-Path $PSScriptRoot '..\release\_inject\NetPilot.exe'),
    [string]$MarkerDir  = "$env:TEMP\netpilot-inject-markers",
    [int]$Port          = 8787,
    [int]$PairCode      = 0
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) {
    Write-Host "  no build at $Exe"
    Write-Host "  build one first:  dotnet build NetPilot\NetPilot.csproj -c Release -o release\_inject"
    exit 2
}

Remove-Item $MarkerDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $MarkerDir | Out-Null

Write-Host "  starting the build..."
$p = Start-Process -FilePath $Exe -ArgumentList '--port', $Port -PassThru -WindowStyle Minimized
Start-Sleep -Seconds 25

if ($p.HasExited) { Write-Host "  the app exited immediately - nothing to test"; exit 2 }

$base = "http://127.0.0.1:$Port"

# Pairing needs the code shown in the UI. Without it these requests are refused at the auth
# layer and the test would report success for the wrong reason - so this is stated rather than
# glossed over: without a code, this verifies only that the endpoint refuses anonymous callers.
$token = $null
if ($PairCode -gt 0) {
    try {
        $r = Invoke-RestMethod -Uri "$base/api/v1/pair" -Method Post `
            -ContentType 'application/json' -Body (@{ code = "$PairCode" } | ConvertTo-Json) -TimeoutSec 10
        $token = $r.token
        Write-Host "  paired"
    } catch {
        Write-Host "  the pairing code was refused: $($_.Exception.Message)"
    }
}

$headers = @{}
if ($token) { $headers['Authorization'] = "Bearer $token" }

# Each payload tries to make PowerShell write a file whose name is unique to that payload. If the
# injection works the file exists afterwards; nothing else in the app writes to this directory.
$payloads = @(
    @{ n = "single-quote break";   v = "x'; Set-Content -Path '$MarkerDir\p1.txt' -Value pwned; '" }
    @{ n = "double-quote break";   v = 'x"; Set-Content -Path "' + $MarkerDir + '\p2.txt" -Value pwned; "' }
    @{ n = "subexpression";        v = 'x$(Set-Content -Path "' + $MarkerDir + '\p3.txt" -Value pwned)' }
    @{ n = "backtick";             v = "x``; Set-Content -Path '$MarkerDir\p4.txt' -Value pwned" }
    @{ n = "newline statement";    v = "x`nSet-Content -Path '$MarkerDir\p5.txt' -Value pwned" }
    @{ n = "semicolon";            v = "x; Set-Content -Path '$MarkerDir\p6.txt' -Value pwned" }
    @{ n = "pipeline";             v = "x | Set-Content -Path '$MarkerDir\p7.txt' -Value pwned" }
    @{ n = "ampersand";            v = "x & Set-Content -Path '$MarkerDir\p8.txt' -Value pwned" }
)

Write-Host ""
Write-Host "  sending $($payloads.Count) payloads..."
foreach ($p2 in $payloads) {
    $body = @{ method = "proxy"; proxy = $p2.v } | ConvertTo-Json -Compress
    try {
        Invoke-RestMethod -Uri "$base/api/v1/share" -Method Post -Headers $headers `
            -ContentType 'application/json' -Body $body -TimeoutSec 15 | Out-Null
    } catch {
        # A 4xx is a fine outcome; what matters is whether a file appeared.
    }
}

Start-Sleep -Seconds 12

Write-Host ""
$markers = @(Get-ChildItem $MarkerDir -File -ErrorAction SilentlyContinue)

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue

if (-not $token) {
    Write-Host "  NOT VERIFIED: no pairing code, so every request was refused before reaching the"
    Write-Host "  proxy code. Pass -PairCode to actually exercise the injection path."
    exit 2
}

if ($markers.Count -gt 0) {
    Write-Host "  FAILED. These payloads executed:"
    $markers | ForEach-Object { Write-Host "    $($_.Name)" }
    exit 1
}

Write-Host "  PASS. None of the $($payloads.Count) payloads produced a file."
Write-Host "  The shipped binary rejected all of them, not just the validator in a test."
exit 0