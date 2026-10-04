# Uploads the release assets one at a time, retrying, and only publishes the release once
# every asset is present on the server AND its hash matches the local file.
#
# Written because the first attempt uploaded all four assets in one `gh release create` and lost
# everything to a single TCP timeout, with the draft left half-populated. Two small files
# survived and the two large ones did not, which is exactly the failure this script is shaped
# to avoid: the small ones do not need the connection to hold for an hour.
#
# Paths are hard-coded on purpose. Passing these through -File or a splat kept mangling the
# arguments, and a packaging script that cannot be invoked reliably is worse than no script.

$ErrorActionPreference = 'Continue'

$gh    = 'C:\Program Files\GitHub CLI\gh.exe'
$repo  = 'Ekkh1300/NetPilot'
$tag   = 'v1.2.2-linux'
$dir   = 'E:\op dn\release\NetPilot-linux-1.2.2'

# Largest first: if the connection dies again we want to know about it early rather than after
# an hour of the last file.
$assets = @(
    'netpilot-linux-x64.tar.gz',
    'netpilot-linux-arm64.tar.gz',
    'README.md',
    'SHA256SUMS.txt'
)

function Say($m) { Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }

function LocalHash($name) {
    (Get-FileHash (Join-Path $dir $name) -Algorithm SHA256).Hash.ToLower()
}

# What the server actually has, with its hash. Compared against the local file rather than
# trusted, because a truncated upload that returns success is the failure nobody notices.
function RemoteAssets {
    try {
        $raw = (& $gh release view $tag --repo $repo --json assets 2>$null) -join ''
        if (-not $raw) { return @{} }
        $map = @{}
        foreach ($a in ($raw | ConvertFrom-Json).assets) {
            $map[$a.name] = ($a.digest -replace '^sha256:', '').ToLower()
        }
        return $map
    } catch { return @{} }
}

$maxTries = 8
foreach ($name in $assets) {
    $local = LocalHash $name
    $mb    = [math]::Round((Get-Item (Join-Path $dir $name)).Length / 1MB, 1)

    $remote = RemoteAssets
    if ($remote.ContainsKey($name) -and $remote[$name] -eq $local) {
        Say ("skip {0} - already on the server and the hash matches" -f $name)
        continue
    }

    Say ("uploading {0} ({1} MB)" -f $name, $mb)
    $ok = $false
    for ($try = 1; $try -le $maxTries -and -not $ok; $try++) {
        Say ("  attempt {0} of {1}" -f $try, $maxTries)
        # --clobber, because a previous attempt may have left a partial upload behind and a
        # second upload under the same name fails rather than replacing it.
        & $gh release upload $tag (Join-Path $dir $name) --repo $repo --clobber 2>&1 |
            Out-String | ForEach-Object { if ($_.Trim()) { Write-Host "    $_" } }

        Start-Sleep -Seconds 5
        $remote = RemoteAssets
        if ($remote.ContainsKey($name) -and $remote[$name] -eq $local) {
            Say ("  verified: {0} on the server matches the local hash" -f $name)
            $ok = $true
        } else {
            Say ("  not there yet, or the hash differs; waiting before retrying")
            Start-Sleep -Seconds 20
        }
    }

    if (-not $ok) { Say ("GIVING UP on {0} - do not publish with a partial upload" -f $name) }
}

# Publish only if every asset is present and correct. A release that goes live missing its main
# binary is worse than a draft that is honestly incomplete.
$remote = RemoteAssets
$missing = @()
foreach ($name in $assets) {
    if (-not $remote.ContainsKey($name) -or $remote[$name] -ne (LocalHash $name)) { $missing += $name }
}

if ($missing.Count -eq 0) {
    Say 'every asset verified - publishing the release'

    # Verified, not assumed. The first version of this script printed "DONE: published"
    # whatever gh returned, and gh had failed with "unexpected EOF" - so the script reported a
    # published release that was still sitting there as a draft. Same failure mode as every
    # other one this logging work turned up: claiming something without evidence for it.
    $published = $false
    for ($try = 1; $try -le 5 -and -not $published; $try++) {
        & $gh release edit $tag --repo $repo --draft=false --prerelease 2>&1 | Out-Null
        Start-Sleep -Seconds 4
        $state = (& $gh release view $tag --repo $repo --json isDraft,url 2>$null) -join ''
        if ($state -match '"isDraft":false') {
            $url = ([regex]::Match($state, '"url":"([^"]+)"')).Groups[1].Value
            Say ("published: {0}" -f $url)
            $published = $true
        } else {
            Say ("  the release is still a draft (attempt {0}); retrying" -f $try)
            Start-Sleep -Seconds 10
        }
    }

    if (-not $published) {
        Say 'PUBLISH FAILED - every asset is uploaded and verified, but the release is still a draft.'
        Say 'It is complete and safe; run this script again to publish it, or open the draft and press publish.'
    }
} else {
    Say ('left as a draft; these did not land: ' + ($missing -join ', '))
}