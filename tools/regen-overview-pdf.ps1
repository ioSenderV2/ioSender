<#
.SYNOPSIS
  Regenerate Overview.pdf from Overview.html via headless Edge.
  Playbook: docs/playbooks/regenerate_overview_pdf.md.

.DESCRIPTION
  Prints Overview.html to Overview.pdf. Uses a FRESH --user-data-dir every
  run: without it Edge attaches to the already-running instance and the
  print silently no-ops (size/timestamp unchanged). Verifies the PDF's
  LastWriteTime actually advanced, so a silent no-op is caught, not trusted.

.EXAMPLE
  tools\regen-overview-pdf.ps1
#>
[CmdletBinding()]
param(
    [string]$Html = 'Overview.html',
    [string]$Pdf  = 'Overview.pdf'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$htmlPath = Join-Path $repo $Html
$pdfPath  = Join-Path $repo $Pdf

if (-not (Test-Path $htmlPath)) { Write-Host "ERROR: $htmlPath not found" -ForegroundColor Red; exit 1 }

$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
if (-not (Test-Path $edge)) { Write-Host "ERROR: Edge not found at $edge" -ForegroundColor Red; exit 1 }

$before = if (Test-Path $pdfPath) { (Get-Item $pdfPath).LastWriteTime } else { [datetime]::MinValue }

# Unique profile dir per run - the whole point (see playbook).
$profileDir = Join-Path $env:TEMP ("edgepdf_" + [guid]::NewGuid().ToString('N'))

$htmlUri = 'file:///' + ($htmlPath -replace '\\','/')

Write-Host "Printing $Html -> $Pdf ..." -ForegroundColor Cyan
& $edge --headless=new --disable-gpu --no-pdf-header-footer `
    "--user-data-dir=$profileDir" "--print-to-pdf=$pdfPath" $htmlUri

Start-Sleep -Seconds 4
try { Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue } catch {}

if (-not (Test-Path $pdfPath)) { Write-Host "ERROR: $Pdf was not produced" -ForegroundColor Red; exit 1 }
$after = (Get-Item $pdfPath).LastWriteTime
if ($after -le $before) {
    Write-Host "ERROR: $Pdf LastWriteTime did not advance ($after) - Edge likely no-op'd." -ForegroundColor Red
    Write-Host "Close any running Edge and retry, or increase the wait." -ForegroundColor Yellow
    exit 1
}

$kb = [math]::Round((Get-Item $pdfPath).Length / 1KB)
Write-Host "OK  $Pdf regenerated ($kb KB, $after)" -ForegroundColor Green
