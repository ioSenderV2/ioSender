<#
.SYNOPSIS
  Add a #N changelog entry to Overview.html - all four places + totals, from a JSON spec.
  Playbook: docs/playbooks/add_changelog_entry.md.

.DESCRIPTION
  You supply only content (title, tag, group, glance line, curated file rows, description).
  The script derives the entry number, computes every count from the file rows, inserts the
  detail block / TOC row / at-a-glance row, re-sums the totals row, bumps the header count,
  and self-checks that all the places agree. Preserves LF line endings.

  Spec JSON (see add_changelog_entry.md for the full shape):
    {
      "title":    "Detail-section H2 title (may contain HTML entities)",
      "tocTitle": "optional shorter title for the TOC row (defaults to title)",
      "tag":      "NEW | CHG | FIX",
      "group":    "one of the at-a-glance <h3> groups, plain '&' ok",
      "glance":   "one-line at-a-glance description",
      "files": [
        { "path": "CNC Controls/Foo.cs", "add": 120, "del": 4 },
        { "path": "CNC Core/Bar.cs",     "add": 30,  "del": 0, "status": "new" },
        { "label": "Locale/*/csv (7 locales)", "add": 90, "del": 0, "count": 7 }
      ],
      "desc": "<p>...</p><ul><li><strong>..</strong> ..</li></ul>",
      "foot": "optional; rendered as <p class=\"foot\">..</p> inside the desc"
    }
  Per-file: status = mod (default) | new | del ; count = files represented (default 1).

.EXAMPLE
  tools\add-changelog-entry.ps1 -Spec C:\...\scratchpad\entry.json
  tools\add-changelog-entry.ps1 -Spec entry.json -Pdf
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Spec,
    [string]$Html = 'Overview.html',
    [switch]$Pdf
)

$ErrorActionPreference = 'Stop'
function Fail($m) { Write-Host "ERROR: $m" -ForegroundColor Red; exit 1 }
function Prop($o, $name, $default) {
    if ($o.PSObject.Properties.Name -contains $name -and $null -ne $o.$name) { $o.$name } else { $default }
}

$repo = Split-Path -Parent $PSScriptRoot
$htmlPath = if ([System.IO.Path]::IsPathRooted($Html)) { $Html } else { Join-Path $repo $Html }
if (-not (Test-Path $htmlPath)) { Fail "$htmlPath not found" }
if (-not (Test-Path $Spec)) { Fail "spec file $Spec not found" }

$s = Get-Content -Raw $Spec | ConvertFrom-Json
foreach ($req in 'title', 'tag', 'group', 'glance', 'files', 'desc') {
    if (-not ($s.PSObject.Properties.Name -contains $req)) { Fail "spec is missing required field '$req'" }
}
$tag = $s.tag.ToUpper()
if ('NEW', 'CHG', 'FIX' -notcontains $tag) { Fail "tag must be NEW, CHG or FIX (got '$($s.tag)')" }
$tocTitle = Prop $s 'tocTitle' $s.title

$content = [System.IO.File]::ReadAllText($htmlPath)

# --- 1. Entry number -------------------------------------------------------
$ids = [regex]::Matches($content, 'id="pr(\d+)"') | ForEach-Object { [int]$_.Groups[1].Value }
if (-not $ids) { Fail "no existing id=""prN"" entries found - wrong file?" }
$N = ($ids | Measure-Object -Maximum).Maximum + 1

# --- 2. Counts from the file rows -----------------------------------------
$linesAdd = 0; $linesDel = 0; $fChg = 0; $fNew = 0; $fDel = 0
$rowHtml = foreach ($f in $s.files) {
    $add = [int](Prop $f 'add' 0)
    $del = [int](Prop $f 'del' 0)
    $count = [int](Prop $f 'count' 1)
    $status = (Prop $f 'status' 'mod').ToLower()
    $label = Prop $f 'label' (Prop $f 'path' $null)
    if (-not $label) { Fail "a file row has neither 'path' nor 'label'" }
    $linesAdd += $add; $linesDel += $del
    switch ($status) {
        'new' { $fNew += $count }
        'del' { $fDel += $count }
        default { $fChg += $count }
    }
    $delClass = if ($del -eq 0) { 'num' } else { 'num del' }
    "<tr><td class=""mono"">$label</td><td class=""num add"">$add</td><td class=""$delClass"">$del</td></tr>"
}
$rowHtml = $rowHtml -join "`n"

# --- 3. Detail block (insert before the last </section>) -------------------
$foot = Prop $s 'foot' $null
$descInner = $s.desc
if ($foot) { $descInner += "<p class=""foot"">$foot</p>" }
$detail = @"
<!-- ===== #$N ===== -->
<div class="pr">
<h2 id="pr$N"><span class="badge">#$N</span>$($s.title)</h2>
<table class="files">
<tr><th>File</th><th style="text-align:right">+</th><th style="text-align:right">&minus;</th></tr>
$rowHtml
</table>
<div class="desc">$descInner</div>
</div>

"@
$secIdx = $content.LastIndexOf('</section>')
if ($secIdx -lt 0) { Fail "no </section> found" }
$content = $content.Insert($secIdx, $detail)

# --- 4. TOC row (insert before the totals row) -----------------------------
# The release workflow inserts a "Version N.N" subheader row above the first entry
# of each release once it's published (tools/cut-release.ps1) - not per-row here.
$tocRow = "<tr><td class=""n"">$N</td><td><a href=""#pr$N"">$tocTitle</a></td><td class=""sz""><span class=""k"">Files:</span> <span class=""chg"">$fChg</span> <span class=""del"">$fDel</span> <span class=""add"">+$fNew</span><br><span class=""k"">Lines:</span> <span class=""add"">+$linesAdd</span> <span class=""del"">-$linesDel</span></td></tr>`n"
$totIdx = $content.IndexOf('<tr class="tot">')
if ($totIdx -lt 0) { Fail "no <tr class=""tot""> totals row found" }
$content = $content.Insert($totIdx, $tocRow)

# --- 5. Totals row: add this entry's numbers to the existing totals --------
$totRe = 'Totals \((\d+) changes\).*?<span class="chg">(\d+)</span> <span class="del">(\d+)</span> <span class="add">\+?(\d+)</span><br><span class="k">Lines:</span> <span class="add">\+(\d+)</span> <span class="del">-?(\d+)</span>'
$totM = [regex]::Match($content, $totRe)
if (-not $totM.Success) { Fail "could not parse the existing totals row" }
$changes = [int]$totM.Groups[1].Value + 1
$sumC = [int]$totM.Groups[2].Value + $fChg
$sumD = [int]$totM.Groups[3].Value + $fDel
$sumA = [int]$totM.Groups[4].Value + $fNew
$sumLA = [int]$totM.Groups[5].Value + $linesAdd
$sumLD = [int]$totM.Groups[6].Value + $linesDel
$totNew = "<tr class=""tot""><td class=""n""></td><td>Totals ($changes changes)</td><td class=""sz""><span class=""k"">Files:</span> <span class=""chg"">$sumC</span> <span class=""del"">$sumD</span> <span class=""add"">+$sumA</span><br><span class=""k"">Lines:</span> <span class=""add"">+$sumLA</span> <span class=""del"">-$sumLD</span></td></tr>"
$content = [regex]::Replace($content, '<tr class="tot">.*?</tr>', { $totNew })

# Non-failing drift guard: additive totals should match a fresh re-sum of the rows.
$rowRe = '<td class="n">\d+</td>.*?<span class="chg">(\d+)</span> <span class="del">(\d+)</span> <span class="add">\+?(\d+)</span><br><span class="k">Lines:</span> <span class="add">\+(\d+)</span> <span class="del">-?(\d+)</span>'
$rC = 0; $rD = 0; $rA = 0; $rLA = 0; $rLD = 0
foreach ($m in [regex]::Matches($content, $rowRe)) {
    $rC += [int]$m.Groups[1].Value; $rD += [int]$m.Groups[2].Value; $rA += [int]$m.Groups[3].Value
    $rLA += [int]$m.Groups[4].Value; $rLD += [int]$m.Groups[5].Value
}
if ($rC -ne $sumC -or $rD -ne $sumD -or $rA -ne $sumA -or $rLA -ne $sumLA -or $rLD -ne $sumLD) {
    Write-Host ("WARN: totals drift - additive says Files {0}/{1}/+{2} Lines +{3}/-{4}, row re-sum says {5}/{6}/+{7} +{8}/-{9}. The stored totals were off before this entry." -f $sumC, $sumD, $sumA, $sumLA, $sumLD, $rC, $rD, $rA, $rLA, $rLD) -ForegroundColor Yellow
}

# --- 6. Header count -------------------------------------------------------
$content = [regex]::Replace($content, '(<strong>)\d+( improvements in the)', { param($m) $m.Groups[1].Value + $N + $m.Groups[2].Value })

# --- 7. At-a-glance row (into the named <h3> group) ------------------------
$tagClass = 't-' + $tag.ToLower()
$glanceRow = "<tr><td>$($s.glance)</td><td class=""tag""><span class=""$tagClass"">$tag</span></td><td class=""prs""><a href=""#pr$N"">$N</a></td></tr>`n"
$featIdx = $content.IndexOf('<div class="feat">')
if ($featIdx -lt 0) { Fail "no <div class=""feat""> at-a-glance section found" }
# Accept the group either plain ('A & B') or already-encoded ('A &amp; B').
$groupHtml = (([string]$s.group) -replace '&amp;', '&') -replace '&', '&amp;'
$h3Re = '<h3[^>]*>' + [regex]::Escape($groupHtml) + '</h3>'
$h3 = [regex]::Match($content.Substring($featIdx), $h3Re)
if (-not $h3.Success) {
    $groups = [regex]::Matches($content.Substring($featIdx), '<h3[^>]*>(.*?)</h3>') | ForEach-Object { '  - ' + ($_.Groups[1].Value -replace '&amp;', '&') }
    Fail "at-a-glance group '$($s.group)' not found. Available groups:`n$($groups -join "`n")"
}
$closeRel = $content.IndexOf('</table>', $featIdx + $h3.Index)
if ($closeRel -lt 0) { Fail "no </table> after group '$($s.group)'" }
$content = $content.Insert($closeRel, $glanceRow)

# --- write back (UTF-8 no BOM, LF preserved) -------------------------------
[System.IO.File]::WriteAllText($htmlPath, $content)

# --- 8. Self-check ---------------------------------------------------------
$prDivs = ([regex]::Matches($content, '<div class="pr">')).Count
$prIds = ([regex]::Matches($content, 'id="pr\d+"')).Count
$tocN = ([regex]::Matches($content, '<td class="n">\d+</td>')).Count
$hdr = [int]([regex]::Match($content, '<strong>(\d+) improvements in the').Groups[1].Value)
$ok = $true
Write-Host "`nEntry #$N added: $($s.title)" -ForegroundColor Green
Write-Host ("  Files: chg {0}  del {1}  add +{2}    Lines: +{3} -{4}" -f $fChg, $fDel, $fNew, $linesAdd, $linesDel)
function Check($name, $val) {
    if ($val -eq $N) { Write-Host ("  OK  {0,-18} = {1}" -f $name, $val) -ForegroundColor Green }
    else { Write-Host ("  BAD {0,-18} = {1} (expected {2})" -f $name, $val, $N) -ForegroundColor Red; $script:ok = $false }
}
Check 'detail blocks' $prDivs
Check 'id="prN"' $prIds
Check 'TOC rows' $tocN
Check 'header count' $hdr
Check 'Totals(N changes)' $changes
if (-not $ok) { Fail "self-check failed - the four places disagree; review the diff and fix before commit." }

Write-Host "`n--- git diff --stat ---" -ForegroundColor Cyan
git -C $repo --no-pager diff --stat -- $Html

if ($Pdf) {
    Write-Host "`nRegenerating Overview.pdf ..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'regen-overview-pdf.ps1')
}

Write-Host "`nReview the diff, then: git add Overview.html$(if($Pdf){' Overview.pdf'}) && commit." -ForegroundColor Yellow
