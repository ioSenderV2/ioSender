<#
    Installs the ioSenderBatchPost Fusion 360 add-in for the current user by
    copying it into Fusion's AddIns folder, where Fusion auto-discovers add-ins.

    Run from PowerShell:
        powershell -ExecutionPolicy Bypass -File .\install-windows.ps1

    After installing you must enable it ONCE in Fusion (it cannot be auto-run
    from outside Fusion):
        Utilities tab > ADD-INS > Scripts and Add-Ins (Shift+S) > Add-Ins tab >
        select "ioSenderBatchPost" > Run  (tick "Run on Startup" to keep it).
#>

$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot 'ioSenderBatchPost'
if (-not (Test-Path $src)) {
    Write-Error "Add-in source folder not found: $src"
    exit 1
}

$addins = Join-Path $env:APPDATA 'Autodesk\Autodesk Fusion 360\API\AddIns'
if (-not (Test-Path $addins)) {
    Write-Error "Fusion 360 AddIns folder not found:`n  $addins`nIs Fusion 360 installed for this user?"
    exit 1
}

$dest = Join-Path $addins 'ioSenderBatchPost'
if (Test-Path $dest) {
    Remove-Item -Recurse -Force $dest
}
Copy-Item -Recurse -Force $src $dest

Write-Host "Installed ioSenderBatchPost to:" -ForegroundColor Green
Write-Host "  $dest"
Write-Host ""
Write-Host "Now enable it in Fusion 360 (one time):"
Write-Host "  Utilities > ADD-INS > Scripts and Add-Ins (Shift+S) > Add-Ins tab"
Write-Host "  > select 'ioSenderBatchPost' > Run   (tick 'Run on Startup')."
Write-Host "The 'Batch Post (ioSender)' button then appears in the Manufacture Actions panel."
