<#
.SYNOPSIS
    Copy the newest screenshot from the Snipping Tool's save folder into docs/manual/img.

.DESCRIPTION
    Manual-screenshot reshoot workflow helper. The user's Snipping Tool auto-saves every capture to
    C:\Users\steve\OneDrive\Pictures\Screenshots. This finds the most recently modified .png there and
    copies it over the named target in docs/manual/img, so a reshoot round-trip is just: snip -> tell
    Claude the filename -> this script runs.

.PARAMETER Name
    Target filename in docs/manual/img, e.g. "job-runscreen.png" or "job-runscreen" (extension optional).

.EXAMPLE
    tools\copy-latest-screenshot.ps1 -Name job-runscreen.png
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Name
)

$ErrorActionPreference = 'Stop'

$screenshotsDir = 'C:\Users\steve\OneDrive\Pictures\Screenshots'
$repoRoot = Split-Path -Parent $PSScriptRoot
$imgDir = Join-Path $repoRoot 'docs\manual\img'

if (-not ($Name -like '*.png')) { $Name = "$Name.png" }
$dest = Join-Path $imgDir $Name

$latest = Get-ChildItem -Path $screenshotsDir -Filter '*.png' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $latest) {
    Write-Error "No .png files found in $screenshotsDir"
    exit 1
}

Copy-Item -Path $latest.FullName -Destination $dest -Force

Write-Host "==> $($latest.Name) (captured $($latest.LastWriteTime.ToString('HH:mm:ss')))"
Write-Host "==> copied to $dest"
