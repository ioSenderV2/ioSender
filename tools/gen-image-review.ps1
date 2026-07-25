<#
.SYNOPSIS
    Generate a quick local HTML gallery of every image in docs/manual/img, for eyeballing a batch of
    reshot screenshots at once instead of opening the full manual.

.DESCRIPTION
    Not part of the published manual, but tracked in the repo since it's a recurring maintenance aid -
    regenerate it (default: docs/manual/_image-review.html) whenever screenshots are reshot so it always
    reflects the current img folder contents; re-commit the regenerated file like any other tracked asset.

.PARAMETER OutFile
    Where to write the gallery HTML. Default: docs\manual\_image-review.html (repo-relative).

.EXAMPLE
    tools\gen-image-review.ps1
#>
[CmdletBinding()]
param(
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$imgDir = Join-Path $repoRoot 'docs\manual\img'
if (-not $OutFile) { $OutFile = Join-Path $repoRoot 'docs\manual\_image-review.html' }

$files = Get-ChildItem -Path $imgDir -Filter '*.png' | Sort-Object Name

$cards = ($files | ForEach-Object {
    $mtime = $_.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
    "<div class='card'><img src='img/$($_.Name)' loading='lazy'><div class='cap'>$($_.Name)<br><span class='mtime'>$mtime</span></div></div>"
}) -join "`n"

$html = @"
<!doctype html>
<html><head><meta charset="utf-8"><title>Manual image review</title>
<style>
body { font-family: Segoe UI, sans-serif; background:#1e1e1e; color:#ddd; margin:0; padding:16px; }
h1 { font-size:16px; font-weight:600; }
.grid { display:grid; grid-template-columns: repeat(auto-fill, minmax(360px, 1fr)); gap:16px; }
.card { background:#2a2a2a; border-radius:6px; overflow:hidden; }
.card img { width:100%; display:block; border-bottom:1px solid #444; }
.cap { padding:6px 8px; font-size:13px; }
.mtime { color:#999; font-size:11px; }
</style></head>
<body>
<h1>docs/manual/img - $($files.Count) images (regenerate: tools\gen-image-review.ps1)</h1>
<div class="grid">
$cards
</div>
</body></html>
"@

Set-Content -Path $OutFile -Value $html -Encoding utf8
Write-Host "==> wrote $OutFile ($($files.Count) images)"
Start-Process $OutFile
