param(
    [Parameter(Mandatory = $true)][string]$Path,
    # Each entry: the exact first line of the block, and the indentation its closing brace has.
    # Anchored on literal text so a missing match fails loudly instead of cutting the file.
    [string[]]$Anchors = $env:NP_ANCHORS -split "`n"
)

if (-not $Anchors -or $Anchors.Count -eq 0) {
    Write-Error "no anchors given (pass -Anchors or set NP_ANCHORS, one anchor per line)"
    exit 1
}

# Removes whole members from a C# file, located by an exact anchor line and terminated by the
# first closing brace at the anchor's own indentation. Refuses to guess: if an anchor is not
# found exactly once, nothing is written.
$lines = Get-Content $Path
$drop = New-Object 'System.Collections.Generic.HashSet[int]'

foreach ($anchor in $Anchors) {
    $hits = @(Select-String -Path $Path -SimpleMatch -Pattern $anchor)
    if ($hits.Count -ne 1) {
        Write-Error ("anchor matched " + $hits.Count + " times: '" + $anchor + "' - file left untouched")
        exit 1
    }

    $start = $hits[0].LineNumber - 1
    # Walk backwards over the doc comment / attribute lines directly above the anchor.
    while ($start -gt 0 -and $lines[$start - 1].TrimStart().StartsWith('/')) { $start-- }

    $indent = ($lines[$start] -replace '^(\s*).*$', '$1')
    $end = $start
    while ($end -lt $lines.Count -and $lines[$end] -ne ($indent + '}')) { $end++ }
    if ($end -ge $lines.Count) {
        Write-Error ("no closing brace at indent '" + $indent + "' for '" + $anchor + "' - file left untouched")
        exit 1
    }

    Write-Output ("  cutting '" + $anchor + "'  lines " + ($start + 1) + ".." + ($end + 1))
    foreach ($i in $start..$end) { [void]$drop.Add($i) }
    # Swallow one trailing blank line so we do not leave a hole.
    if (($end + 1) -lt $lines.Count -and $lines[$end + 1].Trim() -eq '') { [void]$drop.Add($end + 1) }
}

$kept = for ($i = 0; $i -lt $lines.Count; $i++) { if (-not $drop.Contains($i)) { $lines[$i] } }
# WriteAllLines, not Set-Content -NoNewline: the latter concatenates an array into one line.
[IO.File]::WriteAllLines((Resolve-Path $Path).Path, [string[]]$kept)
Write-Output ("  " + $lines.Count + " -> " + $kept.Count + " lines")