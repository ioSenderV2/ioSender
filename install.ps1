<#
.SYNOPSIS
    One-line installer for ioSender V2 - no build tools, no admin rights.

.DESCRIPTION
    Downloads the latest published ioSender release, installs it to
    %LocalAppData%\Programs\ioSender, creates a desktop shortcut, and
    launches it. Safe to re-run any time - it always updates to the newest
    version, moving the prior install to a "previous" subfolder first so you
    can roll back one version with -Rollback.

.PARAMETER Rollback
    Swap the current install with the one saved under ioSender\previous,
    then launch it. Undoes the last update; running -Rollback twice in a
    row is a no-op flip back to whatever "current" was, so it only ever
    goes back one version.

.EXAMPLE
    From PowerShell:
    irm https://raw.githubusercontent.com/ioSenderV2/ioSender/master/install.ps1 | iex

.EXAMPLE
    From CMD (or PowerShell):
    powershell "irm https://raw.githubusercontent.com/ioSenderV2/ioSender/master/install.ps1 | iex"

.EXAMPLE
    irm https://raw.githubusercontent.com/ioSenderV2/ioSender/master/install.ps1 | iex -Rollback
#>
[CmdletBinding()]
param(
    [switch]$Rollback
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repo = 'ioSenderV2/ioSender'
$installDir = Join-Path $env:LocalAppData 'Programs\ioSender'
$previousDir = Join-Path $installDir 'previous'
$exePath = Join-Path $installDir 'ioSender.exe'
$tempZip = Join-Path $env:TEMP 'ioSender-install.zip'

function New-DesktopShortcut {
    Write-Host "==> Creating desktop shortcut ..." -ForegroundColor Cyan
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'ioSender.lnk'))
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $installDir
    $shortcut.IconLocation = $exePath
    $shortcut.Save()
}

if ($Rollback) {
    if (-not (Test-Path $previousDir)) { throw "No previous version found under $previousDir - nothing to roll back to." }

    Get-Process ioSender -ErrorAction SilentlyContinue | Stop-Process -Force

    $swapDir = Join-Path $env:TEMP 'ioSender-swap'
    if (Test-Path $swapDir) { Remove-Item $swapDir -Recurse -Force }

    Write-Host "==> Rolling back to previous version ..." -ForegroundColor Cyan
    Move-Item $previousDir $swapDir              # previous -> swap (outside installDir)
    Get-ChildItem $installDir -Force | Remove-Item -Recurse -Force   # drop the current version's files
    Get-ChildItem $swapDir -Force | Move-Item -Destination $installDir -Force  # swap contents -> installDir (now "current")
    Remove-Item $swapDir -Recurse -Force

    New-DesktopShortcut
    Write-Host "==> Launching rolled-back ioSender ..." -ForegroundColor Green
    Start-Process $exePath
    return
}

Write-Host "==> Fetching latest ioSender release info ..." -ForegroundColor Cyan
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'ioSender-installer' }
$asset = $release.assets | Where-Object { $_.name -eq 'ioSender.zip' } | Select-Object -First 1
if (-not $asset) { throw "No ioSender.zip asset found on the latest release ($($release.tag_name)) of $repo." }
Write-Host "==> Latest published version: $($release.tag_name)" -ForegroundColor Cyan

Write-Host "==> Downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB) ..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tempZip -UseBasicParsing

Get-Process ioSender -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $installDir) {
    Write-Host "==> Moving current install to previous\ (one-version rollback) ..." -ForegroundColor Cyan
    $swapDir = Join-Path $env:TEMP 'ioSender-swap'
    if (Test-Path $swapDir) { Remove-Item $swapDir -Recurse -Force }
    Move-Item $installDir $swapDir                # free up the ioSender\ name
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Move-Item $swapDir (Join-Path $installDir 'previous')
}
else {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

Write-Host "==> Installing to $installDir ..." -ForegroundColor Cyan
Expand-Archive -Path $tempZip -DestinationPath $installDir -Force
Remove-Item $tempZip -Force

New-DesktopShortcut

Write-Host "==> Launching ioSender ..." -ForegroundColor Green
Start-Process $exePath
