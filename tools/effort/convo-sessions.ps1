<#
.SYNOPSIS
  END-OF-SESSION CAPTURE. Writes this session's conversation to its own HTML, appends one record to
  sessions.json, and re-renders index.html. Incremental: only transcripts that grew since the last
  capture are opened.

.DESCRIPTION
  THE BOUNDARY IS THIS SCRIPT'S OWN RUN. Running it is what ends a session, so there is nothing to
  infer: every turn logged after the previous capture belongs to this one. (Before 2026-08-02 this
  script pooled EVERY transcript in the project folder - ~580 MB by then - and re-split the whole
  history on a 60-minute idle-gap heuristic on every single run. Both the rescan and the guess are
  gone.)

  What it does:
    1. Reads sessions.json for the checkpoint (last captured turn + each transcript's size then).
    2. Opens only the transcripts whose size changed, and keeps only turns after the checkpoint.
    3. Writes <yyyy-MM-dd_HHmm>_<slug>.html (slug from the session's first prompt) into sessions\,
       stamping the file's create/modify times to the session start so Explorer sorts chronologically.
    4. Appends the session record - turns, tokens, kbd minutes, TOC # entries, release - to
       sessions.json and advances the checkpoint.
    5. Re-renders index.html from the manifest alone (no transcript parsing).

  sessions.json is the durable record. Claude Code deletes transcripts after cleanupPeriodDays
  (30 by default), so a session that is not recorded at capture time is not recoverable later.

.EXAMPLE
  # The end-of-session step (run it LAST, after the summary prose - see the playbook):
  powershell -ExecutionPolicy Bypass -File tools\effort\convo-sessions.ps1

.EXAMPLE
  # Ran it too early and kept working? Fold the extra turns into the session just written:
  powershell -ExecutionPolicy Bypass -File tools\effort\convo-sessions.ps1 -Amend

.EXAMPLE
  # See what would be captured without writing anything:
  powershell -ExecutionPolicy Bypass -File tools\effort\convo-sessions.ps1 -WhatIfOnly
#>
param(
    [string]$ProjectDir   = "$env:USERPROFILE\.claude\projects\c--github-ioSender",
    [string]$OutDir       = "$env:USERPROFILE\Downloads\ClaudeConv",
    [string]$EffortCsv    = "$PSScriptRoot\sessions.csv",
    [string]$OverviewHtml = "$PSScriptRoot\..\..\Overview.html",
    [string]$Repo         = "ioSenderV2/ioSender",
    [string]$GhExe        = "$PSScriptRoot\..\gh.ps1",
    [string]$MirrorPath   = "$PSScriptRoot\sessions.json",   # in-repo copy of the manifest ('' to skip)
    [string]$RepoDir      = "$PSScriptRoot\..\..",
    [int]$GapMarkerMinutes  = 45,   # idle this long is drawn as a break INSIDE the session, not split on
    [int]$MinSessionMinutes = 30,   # ignore a 'start' marker this soon after the last cut
    [int]$EndQuietMinutes   = 20,   # quiet needed after a capture run for it to count as a wrap-up
    [switch]$Once,              # accepted and ignored: kept so the old playbook command still works
    [switch]$Amend,             # extend the most recent session instead of starting a new one
    [switch]$WhatIfOnly,        # report what would be captured, write nothing
    [switch]$NoCommit,          # write the mirror but leave it uncommitted
    [switch]$IncludeThinking    # also include Claude's internal "thinking" blocks (off by default)
)

. "$PSScriptRoot\convo-common.ps1"

$sessionsDir = Join-Path $OutDir 'sessions'
if (-not $WhatIfOnly -and -not (Test-Path $sessionsDir)) { New-Item -ItemType Directory -Path $sessionsDir -Force | Out-Null }

$manifest = Read-Manifest $OutDir
$recorded = @($manifest.sessions)

# ---- decide the window ----------------------------------------------------------------------
$amending = $false
$prior    = $null
if ($Amend) {
    if ($recorded.Count -eq 0) {
        Write-Host "-Amend: nothing recorded yet - capturing as a new session." -ForegroundColor Yellow
    } else {
        $amending = $true
        $prior = $recorded[$recorded.Count - 1]
    }
}

if ($amending) {
    $since      = ([datetime]$prior.start).AddSeconds(-1)
    $knownSizes = @{}    # force a re-read; the mtime filter keeps it cheap
    Write-Host ("Amending session '{0}' (from {1})" -f $prior.name, $prior.start) -ForegroundColor Cyan
} else {
    $since      = Get-CheckpointThrough $manifest
    $knownSizes = Get-CheckpointSizes $manifest
    if ($since -eq [datetime]::MinValue) {
        Write-Host "No checkpoint in sessions.json - run migrate-session-manifest.ps1 first, or this will capture ALL history as one session." -ForegroundColor Yellow
    } else {
        Write-Host ("Capturing everything after {0}" -f $since.ToString('yyyy-MM-dd HH:mm:ss')) -ForegroundColor Cyan
    }
}

# ---- read only what's new -------------------------------------------------------------------
$scan  = Get-NewTurns -ProjectDir $ProjectDir -KnownSizes $knownSizes -Since $since -IncludeThinking:$IncludeThinking
$all   = @($scan.Turns)
$turns = @($all | Where-Object { $_.Who -ne '' })

if ($turns.Count -eq 0) {
    Write-Host "No new conversation turns since the last capture - nothing to write." -ForegroundColor Yellow
    if (-not $WhatIfOnly -and -not $amending) {
        # Still advance the file sizes so the next run doesn't re-open these transcripts.
        $manifest.checkpoint.files = $scan.Files
        Write-Manifest $OutDir $manifest
    }
    return
}

# Normally this is exactly ONE session - the capture run is the boundary. But if a capture was
# missed and you /clear'd in between, the window covers two real sittings; the transcript's own
# markers say where to cut, so split rather than glue them into one misleading file. A long break
# never splits - it is drawn inside the session instead.
$groups = @(Split-TurnsIntoSessions -Turns $all -MinSessionMinutes $MinSessionMinutes -EndQuietMinutes $EndQuietMinutes)
if ($groups.Count -eq 0) { $groups = @(,$turns) }
$end    = $turns[-1].When
$tokens = [int64]0
foreach ($t in $all) { $tokens += [int64]$t.Tokens }

if ($WhatIfOnly) {
    if ($groups.Count -gt 1) { Write-Host ("A capture looks to have been missed - splitting into {0} sessions on the transcript's markers." -f $groups.Count) -ForegroundColor Yellow }
    foreach ($g in $groups) {
        $fp = ($g | Where-Object { $_.Who -eq 'You' } | Select-Object -First 1).Text
        Write-Host ("Would capture: {0} -> {1} ({2}), {3} turns" -f `
            $g[0].When.ToString('yyyy-MM-dd HH:mm'), $g[-1].When.ToString('HH:mm'), (Format-Duration ($g[-1].When - $g[0].When)), $g.Count) -ForegroundColor Green
        Write-Host ("  name: {0}_{1}" -f $g[0].When.ToString('yyyy-MM-dd_HHmm'), (Get-Slug $fp)) -ForegroundColor Green
    }
    Write-Host ("Total {0} tokens across the window." -f (Format-Tokens $tokens)) -ForegroundColor DarkGray
    return
}
if ($groups.Count -gt 1) {
    Write-Host ("A capture looks to have been missed - splitting into {0} sessions on the transcript's markers." -f $groups.Count) -ForegroundColor Yellow
}

# ---- shared metric inputs --------------------------------------------------------------------
$effortRows   = Import-EffortRows $EffortCsv
$tocNumbers   = Import-TocNumbers $OverviewHtml
$releaseTimes = Get-ReleaseTimes $Repo $GhExe

$list = New-Object System.Collections.Generic.List[object]
foreach ($s in $recorded) { if (-not ($amending -and $s.name -eq $prior.name)) { $list.Add($s) } }
$taken = @{}
foreach ($s in $list) { $taken[[string]$s.name] = $true }

$written = New-Object System.Collections.Generic.List[object]
$gi = 0
foreach ($g in $groups) {
    $gi++
    $gStart = $g[0].When
    $gEnd   = $g[-1].When

    # name it - the first group keeps the amended session's filename so its index link doesn't move
    if ($amending -and $gi -eq 1) {
        $name = $prior.name
    } else {
        $firstPrompt = ($g | Where-Object { $_.Who -eq 'You' } | Select-Object -First 1).Text
        $base = "{0}_{1}" -f $gStart.ToString('yyyy-MM-dd_HHmm'), (Get-Slug $firstPrompt)
        $name = $base
        $k = 2
        while ($taken.ContainsKey($name) -or (Test-Path (Join-Path $sessionsDir "$name.html"))) { $name = "$base-$k"; $k++ }
    }
    $taken[$name] = $true

    # tokens belong to the group's own window
    $gTok = [int64]0
    foreach ($t in $all) { if ($t.When -ge $gStart -and $t.When -le $gEnd) { $gTok += [int64]$t.Tokens } }
    $kbdMin = Get-KbdMinutes -Start $gStart -End $gEnd -EffortRows $effortRows
    $hasKbd = ($effortRows.Count -gt 0 -and @($effortRows | Where-Object { $_.End -ge $gStart -and $_.Start -le $gEnd }).Count -gt 0)

    $title = "Conversation - {0}" -f $gStart.ToString('MMM d, HH:mm')
    $out   = Join-Path $sessionsDir ($name + '.html')
    [System.IO.File]::WriteAllText($out, (Build-SessionHtml $g $title $gStart $gEnd $GapMarkerMinutes), (New-Object System.Text.UTF8Encoding($true)))
    $fi = Get-Item $out
    $fi.CreationTime  = $gStart
    $fi.LastWriteTime = $gStart

    $list.Add((New-SessionRecord -Start $gStart -End $gEnd -Name $name -Turns $g.Count `
        -Tokens $gTok -KbdMin $kbdMin -HasKbd $hasKbd `
        -Toc (Get-TocHits $g $tocNumbers) -Release (Get-ReleaseHit $gStart $gEnd $releaseTimes) -Source 'boundary'))
    $written.Add([pscustomobject]@{ Name = $name; Start = $gStart; End = $gEnd; Turns = $g.Count; Tokens = $gTok; Out = $out })
}
$manifest.sessions = $list.ToArray()

$manifest.checkpoint = [pscustomobject]@{
    through = $end.ToString('yyyy-MM-dd HH:mm:ss')
    files   = $scan.Files
}
Write-Manifest $OutDir $manifest $MirrorPath
$idx = Write-IndexHtml $OutDir $manifest

$verb = if ($amending) { 'Amended' } else { 'Captured' }
foreach ($w in $written) {
    Write-Host ("{0}: {1}" -f $verb, $w.Name) -ForegroundColor Green
    Write-Host ("  {0} - {1}  ({2})   {3} turns   {4} tokens" -f `
        $w.Start.ToString('yyyy-MM-dd HH:mm'), $w.End.ToString('HH:mm'), (Format-Duration ($w.End - $w.Start)), `
        $w.Turns, (Format-Tokens $w.Tokens)) -ForegroundColor Green
    Write-Host ("  -> {0}" -f $w.Out) -ForegroundColor DarkGray
    $verb = 'Captured'
}
Write-Host ("  -> {0}  ({1} sessions)" -f $idx, $manifest.sessions.Count) -ForegroundColor DarkGray
if ($MirrorPath) {
    Write-Host ("  -> {0}" -f $MirrorPath) -ForegroundColor DarkGray
    if (-not $NoCommit) { Publish-ManifestMirror -RepoDir $RepoDir -MirrorPath $MirrorPath -SessionName $written[$written.Count-1].Name }
}
