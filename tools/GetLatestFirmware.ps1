<#
.SYNOPSIS
    Download the latest published grblHAL Teensy 4.x firmware .hex.

.DESCRIPTION
    Queries stevenrwood/iMXRT1062's real GitHub "latest release" (public, no token needed - each build
    publishes its own immutable release, tag fw-<short-sha>; GitHub's own /releases/latest resolves to
    the newest one) and downloads whatever .hex asset is attached to it. Same release ioSender's own
    Machine Setup > Machine tab "Check for Updates" compares a connected board's build against (see
    FirmwareUpdateManager.cs), so this is the manual-flash equivalent of pressing that button.

.PARAMETER DestinationFolder
    Folder to save the .hex into. Default: ~\Downloads.

.EXAMPLE
    .\GetLatestFirmware.ps1
    Downloads to ~\Downloads\firmware-teensy41-<sha>.hex

.EXAMPLE
    .\GetLatestFirmware.ps1 -DestinationFolder C:\temp
#>
[CmdletBinding()]
param(
    [string]$DestinationFolder = (Join-Path $HOME 'Downloads')
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repo = 'stevenrwood/iMXRT1062'

if (-not (Test-Path $DestinationFolder)) {
    New-Item -ItemType Directory -Path $DestinationFolder -Force | Out-Null
}

Write-Host "==> Fetching latest release info for $repo ..." -ForegroundColor Cyan
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'GetLatestFirmware' }

# The release body's first line is "drv:<branch>@<sha>" (see FirmwareUpdateManager.DriverRefPattern) -
# print it so it's obvious which build this actually is, same identifier ioSender's own update-check UI
# shows as "Update available: <sha>".
$drvLine = ($release.body -split "`n" | Where-Object { $_ -match '^drv:' } | Select-Object -First 1)
if ($drvLine) { Write-Host "==> $($drvLine.Trim())" -ForegroundColor DarkYellow }

$asset = $release.assets | Where-Object { $_.name -like '*.hex' } | Select-Object -First 1
if (-not $asset) { throw "No .hex asset found on the latest release of $repo." }

$dest = Join-Path $DestinationFolder $asset.name
Write-Host "==> Downloading $($asset.name) ($([math]::Round($asset.size / 1KB, 1)) KB) ..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $dest -UseBasicParsing

Write-Host "==> Saved to $dest" -ForegroundColor Green
