<#
    Installs the ioSenderV2 Fusion 360 add-in for the current user by copying it
    into Fusion's AddIns folder, where Fusion auto-discovers add-ins.

    Superseded by ioSenderV2's own Help > Support > Install ioSenderV2 Fusion
    Addin... menu item (which symlinks instead of copying, so add-in updates
    that ship with a new ioSenderV2 build need no reinstall). This script
    remains for manual/offline use.

    Run from PowerShell:
        powershell -ExecutionPolicy Bypass -File .\install-windows.ps1

    .PARAMETER InstallDir
        Install into this folder instead of the default per-user Fusion AddIns
        folder (%APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns) - e.g. a
        non-standard Fusion install location, or a test folder you'll point
        Fusion's Scripts and Add-Ins "My Add-Ins" location override at. Skips
        the "does the default AddIns folder exist" check, since a
        caller-supplied path doesn't need to already exist.

    After installing you must enable it ONCE in Fusion (it cannot be auto-run
    from outside Fusion):
        Utilities tab > ADD-INS > Scripts and Add-Ins (Shift+S) > Add-Ins tab >
        select "ioSenderV2" > Run  (tick "Run on Startup" to keep it).
#>
[CmdletBinding()]
param(
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot 'ioSenderV2'
if (-not (Test-Path $src)) {
    Write-Error "Add-in source folder not found: $src"
    exit 1
}

if ($InstallDir) {
    $addins = $InstallDir
    New-Item -ItemType Directory -Force -Path $addins | Out-Null
}
else {
    $addins = Join-Path $env:APPDATA 'Autodesk\Autodesk Fusion 360\API\AddIns'
    if (-not (Test-Path $addins)) {
        Write-Error "Fusion 360 AddIns folder not found:`n  $addins`nIs Fusion 360 installed for this user? (Or pass -InstallDir to install elsewhere.)"
        exit 1
    }
}

$dest = Join-Path $addins 'ioSenderV2'
if (Test-Path $dest) {
    Remove-Item -Recurse -Force $dest
}
Copy-Item -Recurse -Force $src $dest

Write-Host "Installed ioSenderV2 to:" -ForegroundColor Green
Write-Host "  $dest"
Write-Host ""
Write-Host "Now enable it in Fusion 360 (one time):"
Write-Host "  Utilities > ADD-INS > Scripts and Add-Ins (Shift+S) > Add-Ins tab"
Write-Host "  > select 'ioSenderV2' > Run   (tick 'Run on Startup')."
Write-Host "The 'ioSenderV2' dropdown then appears in the Manufacture workspace toolbar."
