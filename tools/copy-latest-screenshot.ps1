<#
.SYNOPSIS
    Copy a screenshot from the Snipping Tool's save folder into docs/manual/img.

.DESCRIPTION
    Manual-screenshot reshoot workflow helper. The user's Snipping Tool auto-saves every capture to
    C:\Users\steve\OneDrive\Pictures\Screenshots. When shooting several in a row (e.g. Settings, then
    Feeds & Speeds, then Job), asking for "the latest" after each one stops working once more than one
    new capture has landed since the last copy - so this indexes into the recent batch by age instead.

.PARAMETER Name
    Target filename in docs/manual/img, e.g. "job-runscreen.png" or "job-runscreen" (extension optional).

.PARAMETER Rank
    1 = oldest of the recent batch, 2 = 2nd oldest, etc. Omit for the newest capture (previous/default
    behavior) - e.g. after shooting 3 in a row, -Rank 1 is the first one taken, -Rank 3 (or omitted) the
    last.

.PARAMETER BatchSize
    How many of the most-recently-modified screenshots count as "the recent batch" that -Rank indexes
    into (default 10) - old screenshots from unrelated earlier sessions don't count.

.EXAMPLE
    tools\copy-latest-screenshot.ps1 -Name job-runscreen.png
    Copy the newest screenshot (unchanged default behavior).

.EXAMPLE
    tools\copy-latest-screenshot.ps1 -Name settings-grbl -Rank 2
    Copy the 2nd-oldest of the last 10 screenshots taken.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Name,
    [int]$Rank = 0,
    [int]$BatchSize = 10
)

$ErrorActionPreference = 'Stop'

$screenshotsDir = 'C:\Users\steve\OneDrive\Pictures\Screenshots'
$repoRoot = Split-Path -Parent $PSScriptRoot
$imgDir = Join-Path $repoRoot 'docs\manual\img'

if (-not ($Name -like '*.png')) { $Name = "$Name.png" }
$dest = Join-Path $imgDir $Name

$recent = Get-ChildItem -Path $screenshotsDir -Filter '*.png' | Sort-Object LastWriteTime -Descending | Select-Object -First $BatchSize
if (-not $recent) {
    Write-Error "No .png files found in $screenshotsDir"
    exit 1
}
$recentAsc = @($recent | Sort-Object LastWriteTime)   # oldest first within the batch

if ($Rank -gt 0) {
    if ($Rank -gt $recentAsc.Count) {
        Write-Error "Asked for rank $Rank but only $($recentAsc.Count) screenshot(s) in the recent batch (-BatchSize $BatchSize)."
        exit 1
    }
    $chosen = $recentAsc[$Rank - 1]
    $rankLabel = "rank $Rank of $($recentAsc.Count)"
}
else {
    $chosen = $recentAsc[-1]
    $rankLabel = "newest"
}

Copy-Item -Path $chosen.FullName -Destination $dest -Force

Write-Host "==> $($chosen.Name) (captured $($chosen.LastWriteTime.ToString('HH:mm:ss')), $rankLabel)"
Write-Host "==> copied to $dest"
