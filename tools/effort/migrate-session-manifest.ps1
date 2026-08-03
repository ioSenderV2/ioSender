<#
.SYNOPSIS
  ONE-TIME: seed sessions.json from everything that exists today, then never scan history again.

.DESCRIPTION
  Run this once. From here on convo-sessions.ps1 appends one record per session at capture time and
  nothing re-derives the past.

  History lives in three places by now, each less complete than the last, so this pulls from all three
  and prefers the most accurate available for each session:

    1. TRANSCRIPTS (exact)  - the surviving *.jsonl. Split on the OLD 60-minute idle-gap heuristic,
       which is the correct tool for this one job: those sessions predate the boundary rule, so a
       guess is the only thing available. Gives exact start/end/turns/tokens.
    2. THE EXISTING index.html (rounded) - covers sessions whose transcripts Claude Code has already
       deleted (cleanupPeriodDays, default 30). Values come back as rendered: times to the minute,
       tokens as "154.3M". Flagged source=migrated-index so the approximation stays visible.
    3. THE ORPHANED SESSION HTMLs (partial) - files in sessions\ older than the index itself, which
       fell off it when their transcripts aged out. Their own footer still carries start/end/turns;
       tokens and kbd time are unrecoverable and stay blank.

  Sessions are keyed by filename, and any candidate whose start falls inside an already-accepted
  session's window is discarded, so the three sources cannot double-count.

.PARAMETER CheckpointAt
  Ignore turns after this time and set the checkpoint there. Use it to hand over cleanly mid-sitting:
  point it at the end of the last COMPLETED session so the current one is captured whole, by the new
  boundary rule, at wrap-up. Defaults to the last turn found.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\effort\migrate-session-manifest.ps1 -WhatIfOnly
  powershell -ExecutionPolicy Bypass -File tools\effort\migrate-session-manifest.ps1 -CheckpointAt "2026-08-02 20:20:18"
#>
param(
    [string]$ProjectDir       = "$env:USERPROFILE\.claude\projects\c--github-ioSender",
    [string]$OutDir           = "$env:USERPROFILE\Downloads\ClaudeConv",
    [int]$SessionGapMinutes   = 60,     # the retired heuristic, used ONLY for pre-boundary history
    [string]$EffortCsv        = "$PSScriptRoot\sessions.csv",
    [string]$OverviewHtml     = "$PSScriptRoot\..\..\Overview.html",
    [string]$Repo             = "ioSenderV2/ioSender",
    [string]$GhExe            = "$PSScriptRoot\..\gh.ps1",
    [string]$CheckpointAt     = $null,
    [switch]$RewriteHtml,               # re-render session HTMLs that already exist on disk
    [switch]$WhatIfOnly,
    [switch]$Force                      # overwrite an existing sessions.json
)

. "$PSScriptRoot\convo-common.ps1"

$sessionsDir = Join-Path $OutDir 'sessions'
$manifestPath = Get-ManifestPath $OutDir
if ((Test-Path $manifestPath) -and -not $Force -and -not $WhatIfOnly) {
    Write-Host "sessions.json already exists - this is a one-time seed. Pass -Force to rebuild it." -ForegroundColor Yellow
    return
}
if (-not $WhatIfOnly -and -not (Test-Path $sessionsDir)) { New-Item -ItemType Directory -Path $sessionsDir -Force | Out-Null }

$cutoff = [datetime]::MaxValue
if ($CheckpointAt) { $cutoff = [datetime]$CheckpointAt }

$effortRows      = Import-EffortRows $EffortCsv
$validTocNumbers = Import-TocNumbers $OverviewHtml
$releaseTimes    = Get-ReleaseTimes $Repo $GhExe

$accepted = New-Object System.Collections.Generic.List[object]
$takenNames = @{}

# Index of an already-accepted session overlapping this window, or -1.
# "Exact" does not mean "complete": a surviving transcript may hold only the TAIL of a session whose
# earlier transcript was already deleted, while the HTML written at the time still has the whole
# thing. So overlap is resolved by which artifact has more turns, not by which source found it.
function Find-OverlapIndex([datetime]$Start, [datetime]$End) {
    for ($i = 0; $i -lt $accepted.Count; $i++) {
        $as = [datetime]$accepted[$i].start; $ae = [datetime]$accepted[$i].end
        if ($Start -le $ae -and $End -ge $as) { return $i }
    }
    return -1
}
function Test-Overlaps([datetime]$Start, [datetime]$End) { return ((Find-OverlapIndex $Start $End) -ge 0) }

# ============================================================ source 1: transcripts (exact)
Write-Host "`n[1/3] Transcripts (exact) - the last full scan this repo will ever do..." -ForegroundColor Cyan
$scan = Get-NewTurns -ProjectDir $ProjectDir -KnownSizes @{} -Since ([datetime]::MinValue) -Quiet
$all  = @($scan.Turns | Where-Object { $_.When -le $cutoff })
$prose = @($all | Where-Object { $_.Who -ne '' })
Write-Host ("      {0} turns across {1} transcripts" -f $prose.Count, @($scan.Files).Count) -ForegroundColor DarkGray

$tSessions = New-Object System.Collections.Generic.List[object]
if ($prose.Count -gt 0) {
    $cur = New-Object System.Collections.Generic.List[object]
    $cur.Add($prose[0])
    for ($i=1; $i -lt $prose.Count; $i++) {
        if (($prose[$i].When - $prose[$i-1].When).TotalMinutes -ge $SessionGapMinutes) {
            $tSessions.Add($cur.ToArray()); $cur = New-Object System.Collections.Generic.List[object]
        }
        $cur.Add($prose[$i])
    }
    $tSessions.Add($cur.ToArray())
}
Write-Host ("      -> {0} sessions at the {1}-min boundary" -f $tSessions.Count, $SessionGapMinutes) -ForegroundColor DarkGray

foreach ($s in $tSessions) {
    $start = $s[0].When
    $end   = $s[-1].When
    $firstPrompt = ($s | Where-Object { $_.Who -eq 'You' } | Select-Object -First 1).Text
    $base = "{0}_{1}" -f $start.ToString('yyyy-MM-dd_HHmm'), (Get-Slug $firstPrompt)
    $name = $base; $k = 2
    while ($takenNames.ContainsKey($name)) { $name = "$base-$k"; $k++ }
    $takenNames[$name] = $true

    # tokens: every entry in the window, including the tool-only turns that carry no prose
    $tok = [int64]0
    foreach ($t in $all) { if ($t.When -ge $start -and $t.When -le $end) { $tok += [int64]$t.Tokens } }
    $kbd    = Get-KbdMinutes -Start $start -End $end -EffortRows $effortRows
    $hasKbd = ($effortRows.Count -gt 0 -and @($effortRows | Where-Object { $_.End -ge $start -and $_.Start -le $end }).Count -gt 0)

    $accepted.Add((New-SessionRecord -Start $start -End $end -Name $name -Turns $s.Count `
        -Tokens $tok -KbdMin $kbd -HasKbd $hasKbd `
        -Toc (Get-TocHits $s $validTocNumbers) `
        -Release (Get-ReleaseHit $start $end $releaseTimes) -Source 'migrated-transcript'))

    if (-not $WhatIfOnly) {
        $out = Join-Path $sessionsDir ($name + '.html')
        if ($RewriteHtml -or -not (Test-Path $out)) {
            $title = "Conversation - {0}" -f $start.ToString('MMM d, HH:mm')
            [System.IO.File]::WriteAllText($out, (Build-SessionHtml $s $title $start $end), (New-Object System.Text.UTF8Encoding($true)))
            $fi = Get-Item $out; $fi.CreationTime = $start; $fi.LastWriteTime = $start
        }
    }
}
$fromTranscripts = $accepted.Count
Write-Host ("      accepted {0}" -f $fromTranscripts) -ForegroundColor Green

# ============================================================ source 2: orphaned session HTMLs
Write-Host "[2/3] Session HTMLs - the artifact written at the time, exact to the second..." -ForegroundColor Cyan
$fromHtml = 0
$fmt = 'dddd, MMMM d yyyy  HH:mm:ss'
foreach ($f in @(Get-ChildItem $sessionsDir -Filter *.html -ErrorAction SilentlyContinue)) {
    $name = [IO.Path]::GetFileNameWithoutExtension($f.Name)
    if ($takenNames.ContainsKey($name)) { continue }
    $text = [System.IO.File]::ReadAllText($f.FullName)
    if ($text -notmatch '(?s)Session started <strong>(.*?)</strong>.*?ended <strong>(.*?)</strong>.*?&middot;\s*(\d+) turns') { continue }
    $s1 = $matches[1]; $s2 = $matches[2]; $turns = [int]$matches[3]
    $start = [datetime]::MinValue; $end = [datetime]::MinValue
    $ok = [datetime]::TryParseExact($s1, $fmt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$start) -and `
          [datetime]::TryParseExact($s2, $fmt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$end)
    if (-not $ok) {
        # fall back to the timestamp baked into the filename
        if ($name -notmatch '^(\d{4}-\d{2}-\d{2})_(\d{2})(\d{2})_') { continue }
        $start = [datetime]::ParseExact("$($matches[1]) $($matches[2]):$($matches[3])", 'yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
        $end = $start
    }
    if ($start -gt $cutoff) { continue }

    $takenNames[$name] = $true
    $ov = Find-OverlapIndex $start $end
    if ($ov -ge 0) {
        # Same sitting, two artifacts. Keep the one with more turns and widen the window to the union;
        # the token figure survives from the transcript side even though it only covers part of it.
        $a = $accepted[$ov]
        if ($turns -le [int]$a.turns) { continue }
        $uStart = if ($start -lt [datetime]$a.start) { $start } else { [datetime]$a.start }
        $uEnd   = if ($end   -gt [datetime]$a.end)   { $end }   else { [datetime]$a.end }
        $kbdM   = Get-KbdMinutes -Start $uStart -End $uEnd -EffortRows $effortRows
        $hasK   = ($effortRows.Count -gt 0 -and @($effortRows | Where-Object { $_.End -ge $uStart -and $_.Start -le $uEnd }).Count -gt 0)
        $accepted[$ov] = New-SessionRecord -Start $uStart -End $uEnd -Name $name -Turns $turns `
            -Tokens ([int64]$a.tokens) -KbdMin $kbdM -HasKbd $hasK -Toc @($a.toc) `
            -Release (Get-ReleaseHit $uStart $uEnd $releaseTimes) -Source 'migrated-merged'
        Write-Host ("      merged: {0} ({1} turns) supersedes {2} ({3})" -f $name, $turns, $a.name, $a.turns) -ForegroundColor DarkYellow
        continue
    }

    $kbd    = Get-KbdMinutes -Start $start -End $end -EffortRows $effortRows
    $hasKbd = ($effortRows.Count -gt 0 -and @($effortRows | Where-Object { $_.End -ge $start -and $_.Start -le $end }).Count -gt 0)
    $accepted.Add((New-SessionRecord -Start $start -End $end -Name $name -Turns $turns `
        -Tokens 0 -KbdMin $kbd -HasKbd $hasKbd -Toc @() `
        -Release (Get-ReleaseHit $start $end $releaseTimes) -Source 'migrated-html'))
    $fromHtml++
}
Write-Host ("      accepted {0}" -f $fromHtml) -ForegroundColor Green

# ============================================================ source 3: the old index.html (rounded)
# LAST resort - everything here has been through the renderer, so times are rounded to the minute and
# tokens come back as "154.3M". Only reaches sessions the other two sources have no artifact for.
Write-Host "[3/3] Old index.html (rounded) - anything the first two sources missed..." -ForegroundColor Cyan
$indexPath = Join-Path $OutDir 'index.html'
$fromIndex = 0
$idxText = $null
if (Test-Path $indexPath) { $idxText = [System.IO.File]::ReadAllText($indexPath) }
if ($idxText -and $idxText -match 'pure render of sessions\.json') {
    # We wrote this one. Re-reading our own output would launder rounded values back in as if they
    # were source data, so treat a regenerated index as carrying nothing.
    Write-Host "      index.html is already manifest-generated - skipping (no source data in it)." -ForegroundColor DarkGray
    $idxText = $null
}
if ($idxText) {
    foreach ($m in [regex]::Matches($idxText, '(?s)<tr>(?!<th)(.*?)</tr>')) {
        $row = $m.Groups[1].Value
        if ($row -match '<th>') { continue }
        $cells = @([regex]::Matches($row, '(?s)<td[^>]*>(.*?)</td>') | ForEach-Object { $_.Groups[1].Value })
        if ($cells.Count -lt 8) { continue }
        if ($cells[0] -match '^Totals') { continue }
        if ($cells[0] -notmatch '^(\d{4}-\d{2}-\d{2})\s+(\d{1,2}:\d{2}(?:am|pm))\s+-\s+(\d{1,2}:\d{2}(?:am|pm))$') { continue }
        $day = $matches[1]; $t1 = $matches[2]; $t2 = $matches[3]
        try {
            $start = [datetime]::Parse("$day $t1", [Globalization.CultureInfo]::InvariantCulture)
            $end   = [datetime]::Parse("$day $t2", [Globalization.CultureInfo]::InvariantCulture)
        } catch { continue }
        if ($end -lt $start) { $end = $end.AddDays(1) }     # session crossed midnight
        if ($start -gt $cutoff) { continue }
        if (Test-Overlaps $start $end) { continue }
        if ($cells[7] -notmatch 'href="sessions/([^"]+)\.html"') { continue }
        $name = $matches[1]
        if ($takenNames.ContainsKey($name)) { continue }

        $turns = 0; [void][int]::TryParse(($cells[3] -replace '\D',''), [ref]$turns)

        $tok = [int64]0
        if ($cells[4] -match '([\d.,]+)\s*([MkK]?)') {
            $v = [double]($matches[1] -replace ',','')
            switch ($matches[2]) { 'M' { $tok = [int64]($v * 1000000) } 'k' { $tok = [int64]($v * 1000) } 'K' { $tok = [int64]($v * 1000) } default { $tok = [int64]$v } }
        }
        $kbd = 0.0; $hasKbd = $false
        if ($cells[2] -notmatch 'muted') {
            $hasKbd = $true
            if ($cells[2] -match '(?:(\d+)h\s*)?(\d+)m') { $kbd = ([double]$matches[1] * 60) + [double]$matches[2] }
        }
        $toc = @([regex]::Matches($cells[5], 'toc-chip">#(\d+)<') | ForEach-Object { $_.Groups[1].Value })
        $rel = $null
        if ($cells[6] -match 'class="yes">([^<]+)<') { $rel = $matches[1] }

        $takenNames[$name] = $true
        $accepted.Add((New-SessionRecord -Start $start -End $end -Name $name -Turns $turns `
            -Tokens $tok -KbdMin $kbd -HasKbd $hasKbd -Toc $toc -Release $rel -Source 'migrated-index'))
        $fromIndex++
    }
}
Write-Host ("      accepted {0}" -f $fromIndex) -ForegroundColor Green

# ============================================================ write it out
$sorted = @($accepted | Sort-Object { [datetime]$_.start })
$checkpoint = if ($CheckpointAt) { ([datetime]$CheckpointAt) } elseif ($prose.Count -gt 0) { $prose[-1].When } else { Get-Date }

Write-Host ("`nTotal: {0} sessions ({1} exact / {2} from index / {3} from html)  {4} -> {5}" -f `
    $sorted.Count, $fromTranscripts, $fromIndex, $fromHtml, `
    ([datetime]$sorted[0].start).ToString('yyyy-MM-dd'), ([datetime]$sorted[-1].start).ToString('yyyy-MM-dd')) -ForegroundColor Cyan
Write-Host ("Checkpoint: {0} - the next capture takes everything after this." -f $checkpoint.ToString('yyyy-MM-dd HH:mm:ss')) -ForegroundColor Cyan

if ($WhatIfOnly) { Write-Host "-WhatIfOnly: nothing written." -ForegroundColor Yellow; return }

$manifest = New-Manifest
$manifest.sessions = $sorted
$manifest.checkpoint = [pscustomobject]@{
    through = $checkpoint.ToString('yyyy-MM-dd HH:mm:ss')
    files   = $scan.Files
}
Write-Manifest $OutDir $manifest
$idx = Write-IndexHtml $OutDir $manifest
Write-Host ("==> {0}" -f (Get-ManifestPath $OutDir)) -ForegroundColor Green
Write-Host ("==> {0}" -f $idx) -ForegroundColor Green
