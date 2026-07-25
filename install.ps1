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

.PARAMETER Tag
    Install a specific released version (e.g. "2.20") instead of the
    latest. Used by ioSender's own "Check for Updates" when running a
    local dev build, to install a picked release over it for comparison.

.PARAMETER InstallDir
    Install into this directory instead of the default
    %LocalAppData%\Programs\ioSender - e.g. a dev build's own bin folder.
    No desktop shortcut is created when this is set (it would be a
    dev-only, throwaway location).

.EXAMPLE
    From PowerShell:
    irm https://raw.githubusercontent.com/ioSenderV2/ioSender/master/install.ps1 | iex

.EXAMPLE
    From CMD (or PowerShell):
    powershell "irm https://raw.githubusercontent.com/ioSenderV2/ioSender/master/install.ps1 | iex"

.EXAMPLE
    Passing a parameter (-Rollback, -Tag, -InstallDir) through a piped download needs the
    scriptblock form below, NOT "| iex -Rollback" - that pipes -Rollback to Invoke-Expression
    ITSELF (which has no such parameter) and throws "A parameter cannot be found that matches
    parameter name 'Rollback'", not to the downloaded script. Confirmed by testing both forms.
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/ioSenderV2/ioSender/master/install.ps1))) -Rollback

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/ioSenderV2/ioSender/master/install.ps1))) -Tag "2.26"
#>
[CmdletBinding()]
param(
    [switch]$Rollback,
    [string]$Tag,
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repo = 'ioSenderV2/ioSender'
$installDir = if ($InstallDir) { $InstallDir } else { Join-Path $env:LocalAppData 'Programs\ioSender' }
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

    if (-not $InstallDir) { New-DesktopShortcut }
    Write-Host "==> Launching rolled-back ioSender ..." -ForegroundColor Green
    Start-Process $exePath
    return
}

if ($Tag) {
    $tagName = if ($Tag.StartsWith('v')) { $Tag } else { "v$Tag" }
    Write-Host "==> Fetching release info for $tagName ..." -ForegroundColor Cyan
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/tags/$tagName" -Headers @{ 'User-Agent' = 'ioSender-installer' }
}
else {
    Write-Host "==> Fetching latest ioSender release info ..." -ForegroundColor Cyan
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'ioSender-installer' }
}
$asset = $release.assets | Where-Object { $_.name -eq 'ioSender.zip' } | Select-Object -First 1
if (-not $asset) { throw "No ioSender.zip asset found on release ($($release.tag_name)) of $repo." }
Write-Host "==> Installing version: $($release.tag_name)" -ForegroundColor Cyan

Write-Host "==> Downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB) ..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tempZip -UseBasicParsing

Get-Process ioSender -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500   # Stop-Process returns before Windows finishes releasing file handles -
                                 # give it a beat before touching the folder, or the move below can hit
                                 # "used by another process" even though the process is already gone.

if (Test-Path $installDir) {
    Write-Host "==> Moving current install to previous\ (one-version rollback) ..." -ForegroundColor Cyan
    $swapDir = Join-Path $env:TEMP 'ioSender-swap'
    if (Test-Path $swapDir) { Remove-Item $swapDir -Recurse -Force }
    # Best-effort: a stuck file handle (another process, AV scan, Explorer preview, or a handle Windows
    # hasn't released yet) shouldn't hard-abort the whole install - warn and fall back to installing over
    # the existing folder in place (no "previous\" rollback available this run) rather than failing outright.
    try {
        Move-Item $installDir $swapDir -ErrorAction Stop   # free up the ioSender\ name
        New-Item -ItemType Directory -Path $installDir -Force | Out-Null
        Move-Item $swapDir (Join-Path $installDir 'previous') -ErrorAction Stop
    }
    catch {
        Write-Host "==> Could not back up the current install for rollback (a file may still be in use): $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "==> Continuing - installing over the existing files instead. -Rollback won't be available for this update." -ForegroundColor Yellow
        if (Test-Path $swapDir) {
            # Best-effort recovery: if the first move succeeded but the second didn't, don't strand the
            # backup in %TEMP% - try to put it back where it came from so the retry below sees $installDir
            # populated as if the swap never started (falls through to the plain Expand-Archive -Force).
            try { Move-Item $swapDir $installDir -ErrorAction Stop } catch { Remove-Item $swapDir -Recurse -Force -ErrorAction SilentlyContinue }
        }
        if (-not (Test-Path $installDir)) { New-Item -ItemType Directory -Path $installDir -Force | Out-Null }
    }
}
else {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

Write-Host "==> Installing to $installDir ..." -ForegroundColor Cyan
Expand-Archive -Path $tempZip -DestinationPath $installDir -Force
Remove-Item $tempZip -Force

if (-not $InstallDir) { New-DesktopShortcut }

Write-Host "==> Launching ioSender ..." -ForegroundColor Green
Start-Process $exePath
