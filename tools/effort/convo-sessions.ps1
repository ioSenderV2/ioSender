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
    [switch]$Once,              # accepted and ignored: kept so the old playbook command still works
    [switch]$Amend,             # extend the most recent session instead of starting a new one
    [switch]$WhatIfOnly,        # report what would be captured, write nothing
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

$start  = $turns[0].When
$end    = $turns[-1].When
$tokens = [int64]0
foreach ($t in $all) { $tokens += [int64]$t.Tokens }

if ($WhatIfOnly) {
    $firstPrompt = ($turns | Where-Object { $_.Who -eq 'You' } | Select-Object -First 1).Text
    Write-Host ("Would capture: {0} -> {1} ({2}), {3} turns, {4} tokens" -f `
        $start.ToString('yyyy-MM-dd HH:mm'), $end.ToString('HH:mm'), (Format-Duration ($end - $start)), $turns.Count, (Format-Tokens $tokens)) -ForegroundColor Green
    Write-Host ("Name would be: {0}_{1}" -f $start.ToString('yyyy-MM-dd_HHmm'), (Get-Slug $firstPrompt)) -ForegroundColor Green
    return
}

# ---- name it --------------------------------------------------------------------------------
if ($amending) {
    $name = $prior.name       # keep the existing filename so the index link doesn't move
} else {
    $firstPrompt = ($turns | Where-Object { $_.Who -eq 'You' } | Select-Object -First 1).Text
    $base = "{0}_{1}" -f $start.ToString('yyyy-MM-dd_HHmm'), (Get-Slug $firstPrompt)
    $taken = @{}
    foreach ($s in $recorded) { $taken[[string]$s.name] = $true }
    $name = $base
    $k = 2
    while ($taken.ContainsKey($name) -or (Test-Path (Join-Path $sessionsDir "$name.html"))) { $name = "$base-$k"; $k++ }
}

# ---- metrics --------------------------------------------------------------------------------
$effortRows = Import-EffortRows $EffortCsv
$kbdMin     = Get-KbdMinutes -Start $start -End $end -EffortRows $effortRows
$hasKbd     = ($effortRows.Count -gt 0 -and @($effortRows | Where-Object { $_.End -ge $start -and $_.Start -le $end }).Count -gt 0)
$toc        = Get-TocHits $turns (Import-TocNumbers $OverviewHtml)
$release    = Get-ReleaseHit $start $end (Get-ReleaseTimes $Repo $GhExe)

# ---- write the session HTML -----------------------------------------------------------------
$title = "Conversation - {0}" -f $start.ToString('MMM d, HH:mm')
$html  = Build-SessionHtml $turns $title $start $end
$out   = Join-Path $sessionsDir ($name + '.html')
[System.IO.File]::WriteAllText($out, $html, (New-Object System.Text.UTF8Encoding($true)))
$fi = Get-Item $out
$fi.CreationTime  = $start
$fi.LastWriteTime = $start

# ---- record it ------------------------------------------------------------------------------
$record = New-SessionRecord -Start $start -End $end -Name $name -Turns $turns.Count `
                            -Tokens $tokens -KbdMin $kbdMin -HasKbd $hasKbd `
                            -Toc $toc -Release $release -Source 'boundary'

$list = New-Object System.Collections.Generic.List[object]
foreach ($s in $recorded) { if (-not ($amending -and $s.name -eq $prior.name)) { $list.Add($s) } }
$list.Add($record)
$manifest.sessions = $list.ToArray()

$manifest.checkpoint = [pscustomobject]@{
    through = $end.ToString('yyyy-MM-dd HH:mm:ss')
    files   = $scan.Files
}
Write-Manifest $OutDir $manifest
$idx = Write-IndexHtml $OutDir $manifest

$verb = if ($amending) { 'Amended' } else { 'Captured' }
Write-Host ("{0}: {1}" -f $verb, $name) -ForegroundColor Green
Write-Host ("  {0} - {1}  ({2})   {3} turns   {4} tokens{5}{6}" -f `
    $start.ToString('yyyy-MM-dd HH:mm'), $end.ToString('HH:mm'), (Format-Duration ($end - $start)), `
    $turns.Count, (Format-Tokens $tokens), `
    $(if ($toc.Count -gt 0) { "   TOC " + (($toc | ForEach-Object { "#$_" }) -join ' ') } else { '' }), `
    $(if ($release) { "   release $release" } else { '' })) -ForegroundColor Green
Write-Host ("  -> {0}" -f $out) -ForegroundColor DarkGray
Write-Host ("  -> {0}  ({1} sessions)" -f $idx, $manifest.sessions.Count) -ForegroundColor DarkGray
