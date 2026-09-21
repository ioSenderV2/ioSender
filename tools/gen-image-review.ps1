<#
.SYNOPSIS
    Generate a local HTML status board for every manual screenshot - the ones that exist, the ones that
    are owed, and the ones nothing references any more.

.DESCRIPTION
    Not part of the published manual, but tracked in the repo since it's a recurring maintenance aid.
    Regenerate it whenever screenshots are reshot or the debt list changes, and re-commit the
    regenerated file like any other tracked asset.

    Two views, one set of facts:

      (default)  docs\manual\_image-review.html       - a contact sheet. Everything at a glance.
      -Pages     docs\manual\_image-review-pages.html - ONE SHOT PER SCREEN, in the order they are
                 owed. This is the worklist for a reshoot session: step down it, shoot, refresh.

    Status is derived, not hand-maintained, so the board can't drift from reality:

      orphaned   - the file exists in img/ but index.html references no <img src="img/<name>">.
      wanted     - $Wanted lists it and there is no file yet. Rendered as a dashed placeholder,
                   so a shot that is owed is as visible as one that is wrong.
      reshoot    - $Reshoot lists it: the file exists and is referenced, but shows superseded UI.
      current    - referenced, not flagged.

    The topic each shot belongs to, and the caption it carries, are read out of index.html too - so a
    figure that gets re-captioned cannot leave this board describing the old one.

    The three tables below are the only things to edit by hand. Everything else follows from the
    filesystem and index.html. Note-only entries are fine in any of them; the note is what a reader
    needs to know, the status is what colours the entry.

    A note may end with "SHOOT: <instruction>". That part is split off and shown on its own line - what
    to point the camera at, as opposed to why the shot is owed.

    The -Pages view has a working "update from newest capture" button, but only when it is being served
    by tools\serve-image-review.ps1 - a page opened straight off disk cannot write to the filesystem.
    Open it that way and the buttons say so rather than failing silently.

.PARAMETER Pages
    Write the one-shot-per-screen worklist instead of the contact sheet.

.PARAMETER OutFile
    Where to write. Defaults to _image-review.html, or _image-review-pages.html with -Pages.

.PARAMETER NoLaunch
    Write the file without opening it in a browser.

.EXAMPLE
    tools\gen-image-review.ps1 -Pages
    The reshoot worklist. Step down it with the arrow keys; press R after reshooting one.
#>
[CmdletBinding()]
param(
    [switch]$Pages,
    [string]$OutFile,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$imgDir = Join-Path $repoRoot 'docs\manual\img'
$indexFile = Join-Path $repoRoot 'docs\manual\index.html'
if (-not $OutFile) {
    $OutFile = Join-Path $repoRoot ($Pages ? 'docs\manual\_image-review-pages.html' : 'docs\manual\_image-review.html')
}

# --- the three hand-maintained tables ------------------------------------------------------------

# Shots the manual is waiting on. Keyed by the filename it will be saved as; the value is what the
# shot has to show. These render as dashed placeholders until the file appears.
# Source: MANUAL-AUDIT-2026-09-17.md section 6, "New shots wanted".
$Wanted = [ordered]@{
    'carving-svg.png'       = 'An SVG carve in the Work Order tree with the stock preview. #carving shipped with NO figure at all - the only topic in the manual in that state, so this one is owed rather than speculative. SHOOT: Work Order with an SVG artwork toolpath selected and the stock preview showing.'
    'carving-negative.png'  = 'The same artwork with Negative on, so the badge stands proud of a cleared floor. The depth model is the hard part of #carving to picture; this is the shot that does it. SHOOT: The same SVG toolpath with Negative ticked, preview showing the raised badge.'
    'laser-dialog.png'      = "The SVG laser dialog's three tabs. #laser describes controls no figure shows. SHOOT: Open the SVG laser dialog (needs -enableSVGLaserJob) and frame all three tabs."
    'heightmap-surface.png' = "Height Map's Surface Map pane with its legend. Replaces the deleted heightmap.png, which showed the pre-rebuild panel. SHOOT: Height Map, Surface Map pane, with a probed map loaded so the legend has numbers."
    'work-order-drawing.png'= 'A Save Drawing PDF page - the dimensioned sheet a work order produces (M8). SHOOT: Save Drawing from a work order, then shoot a page of the PDF.'
    'calibration-tabs.png'  = "The Calibration view's four sub-tabs: Stepper (probe), Stepper (scratch), Squareness (pins), Squareness (probe). SHOOT: Tools > Calibration, framed so all four sub-tabs are readable."
}

# Files that exist and are referenced, but show UI that has since changed.
# ORDER IS PRIORITY - top of this table is shot first. Source: audit section 6's ranking, which ranks
# by how badly the shot MISLEADS, not by how old it is. All 19 referenced figures predate #216; these
# seven are the ones a reader would be actively misled by.
$Reshoot = [ordered]@{
    'job-runscreen.png'          = 'The run strip replaced the run bar, and the bottom status bar is gone (#220) - the largest single visual change in the wave, on the manual''s most-visited topic. One shot can also pay off the toolpath outline and Peek, both of which the manual now describes with no figure. Its caption currently admits it is out of date. SHOOT: A job with three tool changes, RUNNING, one outline section expanded - so Feed Hold, Stop and Peek are all on the strip.'
    'main-window-tools-menu.png' = 'Shows the old four-tab bar and a Tools menu with no Calibration. Caption already admits it. SHOOT: Default config, main window, Tools menu open.'
    'machine-setup-overview.png' = 'Shows NINE steps with Calibration expanded. Machine Setup is eight steps and Calibration left it in #332. Caption already admits it. SHOOT: Machine Setup on STEP 1, not the Overview page - step 1 shows the whole eight-step tree and its status dots.'
    'settings-top-level-tabs.png'= 'Its caption describes the pre-#248 four-tab default; #248 restored the full nine-tab bar. Reshooting fixes the figure and the caption together. SHOOT: Settings > User Interface > Top-level tabs, on a default config so the nine-tab default shows.'
    'start-job-panel.png'        = 'Setup grew a Verify skew / Touch corners / V-bit picker / Scribe square row (#362, #365, #367) - the row that had to be made to wrap. Needed by the Scribe square text anyway. SHOOT: The Setup tab with the actions row visible, including Scribe square.'
    'work-order-composition.png' = 'The tree now carries Text, SVG and Indirect geometries plus group headers, and the operations list grew five kinds. SHOOT: A work order whose tree has a Text and an SVG toolpath plus a group header.'
    'machine-setup-calibration.png'= 'Depicts Calibration as Machine Setup step 8, which it is not (#332). This should become a CALIBRATION VIEW shot instead - retire the filename rather than re-file the same name, and repoint the figure in #machine-setup. SHOOT: Tools > Calibration, Stepper (probe) page, saved under a NEW name.'
}

# Shots that must be taken on a DEFAULT CONFIG - they show what a fresh install looks like, and none of
# them needs data a fresh install would not have. Everything else is shot from your own config, because
# it needs a fixture, a declared probe, a real work order, a Fusion export, controller settings or a
# probed map - things a default config has none of. Orphans are a delete decision and take no shot.
#
# A judgement call per shot, not a derivable fact, which is why it is a list to correct rather than a
# rule to trust.
$DefaultConfigShots = @(
    'main-window-tools-menu.png'    # the tab bar and the Tools menu AS SHIPPED - the whole point of it
    'machine-setup-overview.png'    # the eight-step tree; its dots are allowed to read "not done yet"
    'settings-top-level-tabs.png'   # must show the nine-tab default, which only a fresh config has
    'settings-search.png'           # searching the pages needs no machine
    'connect-dialog.png'            # a dialog, and the first one a new user meets
    'errors-dialog.png'             # a Help-menu reference dialog
    'gcode-viewer.png'              # needs a loaded file (macros/sample_stock_40x400.nc), not a config
    'job-runscreen.png'             # also needs the SIMULATOR and a file with three tool changes
)

# Orphans: referenced by nothing. The default note says only that; anything here replaces it, which is
# where a "checked, and here is the verdict" goes. Source: audit section 6, "Four orphans".
$Orphans = [ordered]@{
    'heightmap.png'          = 'CHECKED 2026-09-17 and safe to DELETE: it predates the Height Map rebuild (old tab bar, run bar with Rewind, bottom status bar, a Full table radio, probe depth/feed on the panel, a single Surface pane). The new #heightmap topic wants heightmap-surface.png instead.'
    'odd-jobs-work-order.png'= 'Depicts the retired Odd Jobs arrangement. DELETE.'
    'probing-tabs.png'       = 'Depicts the Probing tab as it was before Setup absorbed it. DELETE.'
    'tools-tab.png'          = 'Depicts the retired Tools tab wrapper. DELETE.'
}

# --- derive everything else ----------------------------------------------------------------------

Add-Type -AssemblyName System.Web
Add-Type -AssemblyName System.Drawing

function Enc([string]$s) { [System.Web.HttpUtility]::HtmlEncode($s) }

$index = Get-Content -Path $indexFile -Raw

# Where every topic section starts, so an <img> can be attributed to the topic that shows it.
$sectionStarts = @()
foreach ($m in [regex]::Matches($index, '<section class="topic" id="([a-z0-9-]+)"')) {
    $sectionStarts += [pscustomobject]@{ Pos = $m.Index; Id = $m.Groups[1].Value }
}

function Get-TopicFor([int]$pos) {
    $id = ''
    foreach ($s in $sectionStarts) {
        if ($s.Pos -lt $pos) { $id = $s.Id } else { break }
    }
    return $id
}

# The figure's own caption, tags stripped. Read rather than restated: a re-captioned figure must not
# leave this board describing the caption it used to have.
function Get-FigureInfo([string]$name) {
    $rx = '<img src="img/' + [regex]::Escape($name) + '"[^>]*>\s*<figcaption>(?<cap>.*?)</figcaption>'
    $m = [regex]::Match($index, $rx, 'Singleline')
    if (-not $m.Success) {
        $m2 = [regex]::Match($index, '<img src="img/' + [regex]::Escape($name) + '"')
        if (-not $m2.Success) { return $null }
        return [pscustomobject]@{ Topic = Get-TopicFor $m2.Index; Caption = '' }
    }
    $cap = $m.Groups['cap'].Value -replace '<[^>]+>', ' ' -replace '\s+', ' '
    return [pscustomobject]@{ Topic = Get-TopicFor $m.Index; Caption = $cap.Trim() }
}

function Get-PixelSize([string]$path) {
    try {
        $img = [System.Drawing.Image]::FromFile($path)
        try { return "$($img.Width)x$($img.Height)" } finally { $img.Dispose() }
    } catch { return '' }
}

$files = @(Get-ChildItem -Path $imgDir -Filter '*.png' | Sort-Object Name)
$present = @($files | ForEach-Object { $_.Name })
$reshootOrder = @($Reshoot.Keys)

$rows = @()

foreach ($f in $files) {
    $info = Get-FigureInfo $f.Name

    if ($null -eq $info) {
        $status = 'orphaned'
        $note = if ($Orphans.Contains($f.Name)) { $Orphans[$f.Name] }
                else { 'Nothing in index.html references it. Decide: reuse it, or delete it (git history makes it recoverable).' }
        $topic = ''; $caption = ''
    }
    elseif ($Reshoot.Contains($f.Name)) {
        $status = 'reshoot'
        $note = $Reshoot[$f.Name]
        $topic = $info.Topic; $caption = $info.Caption
    }
    else {
        $status = 'current'
        $note = ''
        $topic = $info.Topic; $caption = $info.Caption
    }

    $rows += [pscustomobject]@{
        Name     = $f.Name
        Status   = $status
        Note     = $note
        Topic    = $topic
        Caption  = $caption
        Config   = if ($status -eq 'orphaned') { '' } elseif ($DefaultConfigShots -contains $f.Name) { 'default' } else { 'yours' }
        Mtime    = $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm')
        Pixels   = Get-PixelSize $f.FullName
        Priority = [math]::Max(0, $reshootOrder.IndexOf($f.Name) + 1)
        Exists   = $true
    }
}

foreach ($name in $Wanted.Keys) {
    if ($present -contains $name) { continue }
    $rows += [pscustomobject]@{
        Name     = $name
        Status   = 'wanted'
        Note     = $Wanted[$name]
        Topic    = ''
        Caption  = ''
        Config   = if ($DefaultConfigShots -contains $name) { 'default' } else { 'yours' }
        Mtime    = ''
        Pixels   = ''
        Priority = 0
        Exists   = $false
    }
}

# Owed work first, then everything that is fine. Within 'reshoot', the $Reshoot table's own order.
$order = @{ 'reshoot' = 0; 'wanted' = 1; 'orphaned' = 2; 'current' = 3 }
$rows = $rows | Sort-Object @{ Expression = { $order[$_.Status] } },
                            @{ Expression = { if ($_.Priority -gt 0) { $_.Priority } else { 99 } } },
                            Name

$counts = $rows | Group-Object Status | ForEach-Object { "$($_.Count) $($_.Name)" }
$summary = ($counts -join ' &middot; ')
$stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm')

# --- contact sheet ---------------------------------------------------------------------------------

function Build-ContactSheet {
    $cards = ($rows | ForEach-Object {
        $noteHtml = if ($_.Note) { "<br><span class='note'>$(Enc $_.Note)</span>" } else { '' }
        if ($_.Exists) {
            "<div class='card $($_.Status)'><img src='img/$($_.Name)' loading='lazy'><div class='cap'><span class='pill $($_.Status)'>$($_.Status)</span> $($_.Name)<br><span class='mtime'>$($_.Mtime)</span>$noteHtml</div></div>"
        }
        else {
            "<div class='card $($_.Status)'><div class='ph'>shot needed</div><div class='cap'><span class='pill $($_.Status)'>$($_.Status)</span> $($_.Name)$noteHtml</div></div>"
        }
    }) -join "`n"

    $head = @'
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
'@

    return $head +
           "<h1>Manual screenshot status - $summary</h1>`n" +
           "<div class=`"sub`">generated $stamp by tools\gen-image-review.ps1 &middot; one-per-screen worklist: <code>-Pages</code></div>`n" +
           "<div class=`"grid`">`n$cards`n</div>`n</body></html>"
}

# --- one shot per screen ----------------------------------------------------------------------------

function Build-Pages {
    $n = 0
    $total = $rows.Count

    $sections = ($rows | ForEach-Object {
        $n++
        $r = $_
        $badge = if ($r.Status -eq 'reshoot' -and $r.Priority -gt 0) { "reshoot &middot; priority $($r.Priority)" } else { $r.Status }

        # "why it is owed" and "what to point the camera at" are different sentences with different
        # jobs: the first is for the reader of this page, the second goes into the app's own -message.
        $why = $r.Note
        $shoot = ''
        $split = $r.Note.IndexOf('SHOOT:', [StringComparison]::Ordinal)
        if ($split -ge 0) {
            $why = $r.Note.Substring(0, $split).TrimEnd()
            $shoot = $r.Note.Substring($split + 6).Trim()
        }

        $cfgHtml = switch ($r.Config) {
            'default' { '<span class="cfg default" title="Shoot this one on a default config - it shows what a fresh install looks like">default config</span>' }
            'yours'   { '<span class="cfg yours" title="Shoot this one from your own config - it needs a fixture, a probe, a real work order or controller settings">your config</span>' }
            default   { '' }
        }

        $meta = @()
        if ($r.Topic) { $meta += "<a href=`"index.html#$($r.Topic)`" target=`"_blank`">#$($r.Topic)</a>" }
        $meta += "<span class=`"px`">$(Enc $r.Pixels)</span>"
        $meta += "<span class=`"mt`">$(if ($r.Mtime) { "captured $($r.Mtime)" } else { 'no file yet' })</span>"
        $metaHtml = ($meta | Where-Object { $_ -ne '<span class="px"></span>' }) -join ' &middot; '

        $noteHtml = if ($why) { "<p class=`"why`">$(Enc $why)</p>" } else { '' }
        if ($shoot) { $noteHtml += "<p class=`"shoot`"><b>Shoot:</b> $(Enc $shoot)</p>" }
        $capHtml = if ($r.Caption) { "<p class=`"cap`"><b>Caption today:</b> $(Enc $r.Caption)</p>" } else { '' }

        # An orphan is a delete decision, not a reshoot - it gets no update button, because filing a
        # fresh capture as a filename nothing references would just make a newer orphan.
        $actions = if ($r.Status -eq 'orphaned') {
            '<span class="noact">orphan &mdash; decide delete or reuse; nothing to update</span>'
        } else {
            '<button class="update">update from newest capture</button>' +
            '<button class="reload">reload</button>' +
            '<span class="result"></span>'
        }

        $frame = if ($r.Exists) {
            "<div class=`"frame`"><img src=`"img/$($r.Name)`" data-base=`"img/$($r.Name)`" loading=`"lazy`" alt=`"$(Enc $r.Name)`"></div>"
        } else {
            "<div class=`"frame empty`"><div class=`"ph`">no file yet &mdash; shoot it, then press <b>update from newest capture</b></div></div>"
        }

        @"
<section class="shot $($r.Status)" id="s$n" data-status="$($r.Status)" data-name="$($r.Name)">
  <header>
    <span class="idx">$n / $total</span>
    <span class="pill $($r.Status)">$badge</span>
    <span class="fname">$($r.Name)</span>
    $cfgHtml
    <span class="meta">$metaHtml</span>
    <span class="acts">$actions</span>
  </header>
  <div class="brief">$noteHtml$capHtml</div>
  $frame
</section>
"@
    }) -join "`n"

    $head = @'
<!doctype html>
<html><head><meta charset="utf-8"><title>Screenshot worklist</title>
<style>
* { box-sizing:border-box; }
html { scroll-snap-type:y mandatory; scroll-behavior:smooth; }
body { font-family:Segoe UI, sans-serif; background:#161616; color:#ddd; margin:0; }

#bar { position:fixed; top:0; left:0; right:0; height:30px; z-index:20; display:flex; align-items:center;
       gap:14px; padding:0 12px; background:#111; border-bottom:1px solid #333; font-size:12px; color:#999; }
#bar b { color:#ddd; font-weight:600; }
#bar .spacer { flex:1; }
#bar button { background:#2a2a2a; color:#ddd; border:1px solid #444; border-radius:4px; padding:2px 8px;
              font-size:11px; cursor:pointer; }
#bar button:hover { background:#383838; }
#bar button.on { background:#c0392b; border-color:#c0392b; color:#fff; }
#bar #latest { color:#9fd28a; }
#bar #latest.stale { color:#777; }

#offline { display:none; position:fixed; top:30px; left:0; right:0; z-index:19; padding:4px 12px;
           background:#3a2a10; border-bottom:1px solid #6b5310; color:#e8c877; font-size:12px; }
#offline code { color:#f0e6c0; }
body.offline #offline { display:block; }
body.offline .shot { padding-top:60px; }
body.offline .shot .acts button.update { display:none; }

.shot { height:100vh; scroll-snap-align:start; display:flex; flex-direction:column;
        padding:34px 14px 10px; }
.shot.hidden { display:none; }

.shot header { display:flex; align-items:center; gap:10px; flex-wrap:wrap; font-size:13px; }
.shot .idx { color:#777; font-variant-numeric:tabular-nums; min-width:52px; }
.shot .fname { font-weight:600; font-size:15px; }
.shot .meta { color:#888; font-size:12px; }
.shot .meta a { color:#5aa9e6; text-decoration:none; }
.cfg { font-size:10.5px; padding:1px 7px; border-radius:3px; border:1px solid; }
.cfg.default { color:#7fc4e8; border-color:#2f5d75; background:#132430; }
.cfg.yours { color:#c9b6e0; border-color:#5a4a72; background:#221c2c; }

.shot .acts { margin-left:auto; display:flex; align-items:center; gap:8px; }
.shot .acts button { background:#2a2a2a; color:#ddd; border:1px solid #444; border-radius:4px;
                     padding:3px 10px; font-size:11px; cursor:pointer; }
.shot .acts button:hover:not(:disabled) { background:#383838; }
.shot .acts button.update { background:#2f5d40; border-color:#3e7a55; color:#e6f5ea; }
.shot .acts button.update:hover:not(:disabled) { background:#3e7a55; }
.shot .acts button:disabled { opacity:.4; cursor:default; }
.shot .acts .result { font-size:11px; color:#9fd28a; min-width:0; }
.shot .acts .result.bad { color:#e8a09a; }
.shot .acts .noact { font-size:11px; color:#777; font-style:italic; }

.brief { margin:6px 0 8px; max-width:1100px; }
.brief p { margin:3px 0; font-size:12.5px; line-height:1.45; }
.brief .why { color:#e8b9b3; }
.shot.current .brief .why, .shot.orphaned .brief .why { color:#aaa; }
.brief .shoot { color:#f0e6c0; }
.brief .shoot b { color:#d4a017; }
.brief .cap { color:#888; }

.frame { flex:1; min-height:0; display:flex; align-items:center; justify-content:center;
         background:#0d0d0d; border:1px solid #303030; border-radius:6px; overflow:hidden; }
.frame img { max-width:100%; max-height:100%; object-fit:contain; display:block; }
.frame.empty { border-style:dashed; border-color:#6b5310; }
.frame .ph { color:#d4a017; font-size:14px; }

.pill { display:inline-block; padding:1px 7px; border-radius:3px; font-size:10px; text-transform:uppercase;
        letter-spacing:.04em; }
.pill.reshoot { background:#c0392b; color:#fff; }
.pill.wanted { background:#d4a017; color:#221; }
.pill.orphaned { background:#7f8c8d; color:#fff; }
.pill.current { background:#2ecc71; color:#132; }
</style></head>
<body>
'@

    $bar = "<div id=`"bar`"><b>Screenshot worklist</b><span>$summary</span>" +
           "<span id=`"latest`" title=`"Newest capture in the Snipping Tool folder - this is what 'update' would file`"></span>" +
           "<span class=`"spacer`"></span>" +
           "<span id=`"pos`"></span>" +
           "<button id=`"filter`">owed only (F)</button>" +
           "<button id=`"prev`">&uarr;</button><button id=`"next`">&darr;</button>" +
           "<button id=`"reloadall`">reload all (A)</button>" +
           "<span title=`"arrows or j/k step &middot; U updates this shot &middot; R reloads it &middot; A reloads all &middot; F filters`">keys: &darr;&uarr; U R A F</span>" +
           "<span>generated $stamp</span></div>" +
           "<div id=`"offline`">Opened as a file, so <b>update</b> cannot write to disk. Serve it instead: <code>tools\serve-image-review.ps1</code></div>"

    $tail = @'
<script>
(function () {
  var all = Array.prototype.slice.call(document.querySelectorAll('section.shot'));
  var owedOnly = false;

  function visible() { return all.filter(function (s) { return !s.classList.contains('hidden'); }); }

  // Which section is filling the screen. Read from geometry rather than tracked on scroll, so it is
  // still right after a filter change, a resize, or a browser refresh that restores the position.
  function current() {
    var vis = visible(), best = vis[0], bestD = Infinity;
    vis.forEach(function (s) {
      var d = Math.abs(s.getBoundingClientRect().top - 34);
      if (d < bestD) { bestD = d; best = s; }
    });
    return best;
  }

  function mark() {
    var c = current();
    if (!c) { document.getElementById('pos').textContent = ''; return; }
    var vis = visible();
    document.getElementById('pos').textContent = (vis.indexOf(c) + 1) + ' of ' + vis.length + ' shown';
    // The hash is the whole persistence story: press F5 after reshooting and the browser re-reads the
    // PNG from disk AND lands back on the same shot, which is the loop this page exists for.
    if (location.hash !== '#' + c.id) history.replaceState(null, '', '#' + c.id);
  }

  function go(delta) {
    var vis = visible(), i = vis.indexOf(current()) + delta;
    if (i < 0) i = 0;
    if (i > vis.length - 1) i = vis.length - 1;
    vis[i].scrollIntoView();
  }

  // Cache-bust so a just-reshot file is re-read without a full page refresh. Best effort: some
  // browsers ignore a query string on a file:// URL, and F5 is the guaranteed way either way.
  function reload(sec) {
    var img = sec.querySelector('img[data-base]');
    if (!img) return;
    img.src = img.getAttribute('data-base') + '?t=' + Date.now();
  }

  document.getElementById('next').onclick = function () { go(1); };
  document.getElementById('prev').onclick = function () { go(-1); };
  document.getElementById('reloadall').onclick = function () { all.forEach(reload); };

  document.getElementById('filter').onclick = function () {
    var keep = current();
    owedOnly = !owedOnly;
    this.classList.toggle('on', owedOnly);
    all.forEach(function (s) {
      s.classList.toggle('hidden', owedOnly && s.dataset.status === 'current');
    });
    // Stay where we were if that shot survived the filter; otherwise go to the top of what is left.
    var target = keep.classList.contains('hidden') ? visible()[0] : keep;
    if (target) target.scrollIntoView();
    mark();
  };

  document.querySelectorAll('button.reload').forEach(function (b) {
    b.onclick = function () { reload(b.closest('section.shot')); };
  });

  // --- filing a capture -------------------------------------------------------------------------
  // Only possible when this page is being SERVED: a page opened off disk has no way to write a file,
  // so say so once, up front, rather than letting every button fail on click.
  var served = location.protocol === 'http:' || location.protocol === 'https:';
  if (!served) document.body.classList.add('offline');

  function ago(iso) {
    var s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
    if (s < 90) return Math.round(s) + 's ago';
    if (s < 5400) return Math.round(s / 60) + 'm ago';
    return Math.round(s / 3600) + 'h ago';
  }

  // What 'update' would file, and how old it is. Shown because the failure mode this page has to
  // prevent is filing the WRONG capture - you shot one, got distracted, shot another. A timestamp in
  // the bar makes that visible before the click rather than after it.
  function pollLatest() {
    if (!served) return;
    fetch('api/recent').then(function (r) { return r.json(); }).then(function (j) {
      var el = document.getElementById('latest');
      if (!j.ok || !j.newest) { el.textContent = 'no captures found'; el.className = 'stale'; return; }
      el.textContent = 'newest capture ' + ago(j.newest.time);
      el.className = (Date.now() - new Date(j.newest.time).getTime() > 600000) ? 'stale' : '';
    }).catch(function () { });
  }

  function update(sec) {
    var btn = sec.querySelector('button.update');
    var out = sec.querySelector('.result');
    if (!btn) return;
    btn.disabled = true;
    out.className = 'result';
    out.textContent = 'filing...';
    fetch('api/update?name=' + encodeURIComponent(sec.dataset.name))
      .then(function (r) { return r.json(); })
      .then(function (j) {
        btn.disabled = false;
        if (!j.ok) { out.className = 'result bad'; out.textContent = j.error; return; }
        out.textContent = 'filed ' + j.from + ' (' + j.pixels + ')';
        // Swap the image and its stamps in place. The whole reason for the button is not having to
        // refresh, so nothing here may depend on a reload happening afterwards.
        var frame = sec.querySelector('.frame');
        var img = sec.querySelector('img[data-base]');
        if (!img) {
          frame.classList.remove('empty');
          frame.innerHTML = '';
          img = document.createElement('img');
          img.setAttribute('data-base', 'img/' + sec.dataset.name);
          frame.appendChild(img);
        }
        img.src = img.getAttribute('data-base') + '?t=' + Date.now();
        var px = sec.querySelector('.px'), mt = sec.querySelector('.mt');
        if (px) px.textContent = j.pixels;
        if (mt) mt.textContent = 'captured ' + j.mtime;
        pollLatest();
      })
      .catch(function (e) {
        btn.disabled = false;
        out.className = 'result bad';
        out.textContent = 'failed: ' + e.message;
      });
  }

  document.querySelectorAll('button.update').forEach(function (b) {
    b.onclick = function () { update(b.closest('section.shot')); };
  });

  document.addEventListener('keydown', function (e) {
    if (e.target.tagName === 'INPUT' || e.ctrlKey || e.altKey || e.metaKey) return;
    var k = e.key;
    if (k === 'ArrowDown' || k === 'j' || k === 'PageDown' || k === ' ') { e.preventDefault(); go(1); }
    else if (k === 'ArrowUp' || k === 'k' || k === 'PageUp') { e.preventDefault(); go(-1); }
    else if (k === 'r' || k === 'R') { reload(current()); }
    else if (k === 'a' || k === 'A') { all.forEach(reload); }
    else if (k === 'f' || k === 'F') { document.getElementById('filter').click(); }
    else if (k === 'u' || k === 'U') { e.preventDefault(); update(current()); }
  });

  pollLatest();
  setInterval(pollLatest, 5000);

  var t = null;
  window.addEventListener('scroll', function () { clearTimeout(t); t = setTimeout(mark, 80); });
  window.addEventListener('resize', mark);

  // Restore position from the hash on load. scroll-snap plus lazy images means the browser's own
  // fragment jump can land short, so re-issue it once everything has settled.
  function restore() {
    var id = location.hash.replace('#', '');
    var s = id && document.getElementById(id);
    if (s) s.scrollIntoView();
    mark();
  }
  window.addEventListener('load', function () { setTimeout(restore, 50); });
  restore();
})();
</script>
</body></html>
'@

    return $head + $bar + "`n" + $sections + "`n" + $tail
}

# --- write -----------------------------------------------------------------------------------------

$html = $Pages ? (Build-Pages) : (Build-ContactSheet)

# WriteAllText, not Set-Content: PS 5.1's -Encoding utf8 emits a BOM, and this file is read back by a
# browser and diffed in git like any other tracked asset.
[System.IO.File]::WriteAllText($OutFile, $html, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "==> wrote $OutFile ($($rows.Count) entries: $($summary -replace '&middot;', '|'))"
if (-not $NoLaunch) { Start-Process $OutFile }
