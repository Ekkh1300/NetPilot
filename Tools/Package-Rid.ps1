param(
    [Parameter(Mandatory = $true)][string]$Project,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string[]]$Rids,
    [string]$ExeName = "NetPilot",
    [string]$Readme = ""
)

# Packages a publish into one tar.gz per RID, each with a single top-level folder so that
# extracting cannot scatter twenty files into whatever directory the user happens to be in.
#
# The staging directory is deliberately outside OutDir: an earlier attempt staged inside it and
# then tried to move the staging folder into a subdirectory of itself, which produced archives
# with the wrong name and the wrong contents.

$ErrorActionPreference = 'Stop'
$work = Join-Path $env:TEMP ("np-pkg-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

try {
    foreach ($rid in $Rids) {
        $publish = Join-Path $work "pub-$rid"
        Write-Host "  publishing $rid ..."
        & dotnet publish $Project -c Release -r $rid --self-contained true -o $publish -v q
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

        $folder = "netpilot-$rid"
        $stage = Join-Path $work "stage-$rid"
        New-Item -ItemType Directory -Force -Path (Join-Path $stage $folder) | Out-Null
        Copy-Item (Join-Path $publish '*') (Join-Path $stage $folder) -Recurse -Force
        if ($Readme -and (Test-Path $Readme)) {
            Copy-Item $Readme (Join-Path $stage $folder 'README.md') -Force
        }

        $exe = Join-Path $stage $folder $ExeName
        if (-not (Test-Path $exe)) { throw "no executable named $ExeName in the $rid publish" }

        $archive = Join-Path $OutDir "netpilot-$rid.tar.gz"
        if (Test-Path $archive) { Remove-Item $archive -Force }
        tar -czf $archive -C $stage $folder
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }

        $mb = [math]::Round((Get-Item $archive).Length / 1MB, 1)
        Write-Host ("  {0,-16} -> {1}  {2} MB" -f $rid, (Split-Path $archive -Leaf), $mb)
    }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}