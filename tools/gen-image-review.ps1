<#
.SYNOPSIS
    Generate a local HTML status board for every manual screenshot - the ones that exist, the ones that
    are owed, and the ones nothing references any more.

.DESCRIPTION
    Not part of the published manual, but tracked in the repo since it's a recurring maintenance aid.
    Regenerate it (default: docs/manual/_image-review.html) whenever screenshots are reshot or the debt
    list changes, and re-commit the regenerated file like any other tracked asset.

    Status is derived, not hand-maintained, so the board can't drift from reality:

      orphaned   - the file exists in img/ but index.html references no <img src="img/<name>">.
      wanted     - $Wanted lists it and there is no file yet. Rendered as a dashed placeholder card,
                   so a shot that is owed is as visible as one that is wrong.
      reshoot    - $Reshoot lists it: the file exists and is referenced, but shows superseded UI.
      current    - referenced, not flagged.

    The two tables below are the only things to edit by hand. Everything else follows from the
    filesystem and index.html. Note-only entries are fine in either table; the note is what a reader
    needs to know, the status is what colours the card.

.PARAMETER OutFile
    Where to write the gallery HTML. Default: docs\manual\_image-review.html (repo-relative).

.PARAMETER NoLaunch
    Write the file without opening it in a browser.

.EXAMPLE
    tools\gen-image-review.ps1
#>
[CmdletBinding()]
param(
    [string]$OutFile,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$imgDir = Join-Path $repoRoot 'docs\manual\img'
$indexFile = Join-Path $repoRoot 'docs\manual\index.html'
if (-not $OutFile) { $OutFile = Join-Path $repoRoot 'docs\manual\_image-review.html' }

# --- the two hand-maintained tables -------------------------------------------------------------

# Shots the manual is waiting on. Keyed by the filename it will be saved as; the value is what the
# shot has to show. These render as dashed placeholder cards until the file appears.
$Wanted = [ordered]@{
    'machine-setup-calibration.png'= 'Machine Setup > 8 - Calibration > Stepper: fixture pick, true/measured size, new steps/mm'
    'work-order-surface.png'       = 'a Work Order with a Surface toolpath selected, Entire Spoilboard ticked'
}

# Files that exist and are referenced, but show UI that has since changed.
$Reshoot = [ordered]@{
    'machine-setup-overview.png'  = 'dead - shot before #208, and the step count went from eight to nine (#197 Calibration)'
}

# --- derive everything else ---------------------------------------------------------------------

Add-Type -AssemblyName System.Web

$index = Get-Content -Path $indexFile -Raw
$files = @(Get-ChildItem -Path $imgDir -Filter '*.png' | Sort-Object Name)
$present = @($files | ForEach-Object { $_.Name })

$rows = @()

foreach ($f in $files) {
    $referenced = $index -match [regex]::Escape("img/$($f.Name)")

    if (-not $referenced) {
        $status = 'orphaned'
        $note = 'orphaned - nothing in index.html references it (kept: git history makes it recoverable)'
    }
    elseif ($Reshoot.Contains($f.Name)) {
        $status = 'reshoot'
        $note = $Reshoot[$f.Name]
    }
    else {
        $status = 'current'
        $note = ''
    }

    $rows += [pscustomobject]@{
        Name   = $f.Name
        Status = $status
        Note   = $note
        Mtime  = $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
        Exists = $true
    }
}

foreach ($name in $Wanted.Keys) {
    if ($present -contains $name) { continue }
    $rows += [pscustomobject]@{
        Name   = $name
        Status = 'wanted'
        Note   = $Wanted[$name]
        Mtime  = ''
        Exists = $false
    }
}

# Owed work first, then everything that is fine.
$order = @{ 'reshoot' = 0; 'wanted' = 1; 'orphaned' = 2; 'current' = 3 }
$rows = $rows | Sort-Object @{ Expression = { $order[$_.Status] } }, Name

$cards = ($rows | ForEach-Object {
    $noteHtml = if ($_.Note) { "<br><span class='note'>$([System.Web.HttpUtility]::HtmlEncode($_.Note))</span>" } else { '' }
    if ($_.Exists) {
        "<div class='card $($_.Status)'><img src='img/$($_.Name)' loading='lazy'><div class='cap'><span class='pill $($_.Status)'>$($_.Status)</span> $($_.Name)<br><span class='mtime'>$($_.Mtime)</span>$noteHtml</div></div>"
    }
    else {
        "<div class='card $($_.Status)'><div class='ph'>shot needed</div><div class='cap'><span class='pill $($_.Status)'>$($_.Status)</span> $($_.Name)$noteHtml</div></div>"
    }
}) -join "`n"

$counts = $rows | Group-Object Status | ForEach-Object { "$($_.Count) $($_.Name)" }
$summary = ($counts -join ' &middot; ')
$stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm')

$html = @"
<!doctype html>
<html><head><meta charset="utf-8"><title>Manual screenshot status</title>
<style>
body { font-family: Segoe UI, sans-serif; background:#1e1e1e; color:#ddd; margin:0; padding:16px; }
h1 { font-size:16px; font-weight:600; margin:0 0 4px; }
.sub { color:#999; font-size:12px; margin-bottom:16px; }
.grid { display:grid; grid-template-columns: repeat(auto-fill, minmax(360px, 1fr)); gap:16px; }
.card { background:#2a2a2a; border-radius:6px; overflow:hidden; }
.card img { width:100%; display:block; border-bottom:1px solid #444; }
.cap { padding:6px 8px; font-size:13px; }
.mtime { color:#999; font-size:11px; }
.note { color:#bbb; font-size:11px; }
.card.reshoot { outline:2px solid #c0392b; }
.card.orphaned { outline:2px solid #7f8c8d; }
.card.current { outline:2px solid #2ecc71; }
.card.wanted { background:#332; border:2px dashed #d4a017; display:flex; flex-direction:column; }
.card.wanted .ph { flex:1; min-height:180px; display:flex; align-items:center; justify-content:center;
                   color:#d4a017; font-size:13px; text-align:center; padding:12px; }
.pill { display:inline-block; padding:1px 6px; border-radius:3px; font-size:10px; text-transform:uppercase;
        letter-spacing:.04em; vertical-align:1px; margin-right:4px; }
.pill.reshoot { background:#c0392b; color:#fff; }
.pill.wanted { background:#d4a017; color:#221; }
.pill.orphaned { background:#7f8c8d; color:#fff; }
.pill.current { background:#2ecc71; color:#132; }
</style></head>
<body>
<h1>Manual screenshot status - $summary</h1>
<div class="sub">generated $stamp by tools\gen-image-review.ps1 &middot; status derived from docs/manual/index.html
plus the `$Wanted / `$Reshoot tables in that script</div>
<div class="grid">
$cards
</div>
</body></html>
"@

Set-Content -Path $OutFile -Value $html -Encoding utf8
Write-Host "==> wrote $OutFile ($($rows.Count) entries: $($summary -replace '&middot;', '|'))"
if (-not $NoLaunch) { Start-Process $OutFile }
