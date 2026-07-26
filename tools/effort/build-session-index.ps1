<#
.SYNOPSIS
  Build an index.html summarizing every detected conversation session: elapsed time, real keyboard/mouse
  active time (if available), turn count, token usage, TOC # entries touched, and whether a release was
  cut during that window - with a link to the per-session HTML convo-sessions.ps1 already wrote.

.DESCRIPTION
  Companion to convo-sessions.ps1 (same session-splitting rule: idle gap >= SessionGapMinutes starts a new
  session). This script re-derives the same sessions from the raw transcripts, then for each one:
    - Turns / elapsed time: from the session's own turn timestamps (Start/End), same as convo-sessions.ps1.
    - Tokens: summed from every assistant transcript entry's own "usage" block (input + output + cache
      creation + cache read) whose timestamp falls inside the session window - independent of whether
      that entry survived convo-sessions.ps1's text-only filtering.
    - Kbd time: summed from tools/effort/sessions.csv (effort-tracker.ps1's real keyboard/mouse activity
      log) - any logged interval that OVERLAPS the session window, clipped to the window. Blank if
      effort-tracker.ps1 wasn't running yet for an older session - there is no way to recover that after
      the fact, so this column is honestly "if available", not always populated.
    - TOC # entries: best-effort text match - scans the session's own turns for "#NNN" where NNN is a
      real entry number that exists in Overview.html right now. This is a heuristic (a stray "#3" in an
      unrelated context would false-positive; an entry discussed without its number would be missed), not
      a guaranteed-accurate link.
    - Release: best-effort timing correlation - true if any GitHub release's published_at timestamp falls
      inside the session window. Also a heuristic (a release published moments after the session's last
      logged turn, e.g. from a CI run that finished after you stopped typing, could be missed).

  Output: OutDir\index.html, newest session first, linking to sessions\<same-name-as-convo-sessions.ps1>.html.

.PARAMETER Repo
  owner/repo to query GitHub Releases against for the "Release" column. Set to '' to skip that column
  entirely (no network call).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\effort\build-session-index.ps1
#>
param(
    [string]$ProjectDir       = "$env:USERPROFILE\.claude\projects\c--github-ioSender",
    [string]$OutDir           = "$env:USERPROFILE\Downloads\ClaudeConv",
    [int]$SessionGapMinutes   = 60,
    [string]$EffortCsv        = "$PSScriptRoot\sessions.csv",
    [string]$OverviewHtml     = "$PSScriptRoot\..\..\Overview.html",
    [string]$Repo             = "ioSenderV2/ioSender",
    [string]$GhExe            = "$PSScriptRoot\..\gh.ps1"
)

$sessionsDir = Join-Path $OutDir 'sessions'
if (-not (Test-Path $sessionsDir)) { New-Item -ItemType Directory -Path $sessionsDir -Force | Out-Null }

function Read-Shared([string]$Path) {
    try {
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try { $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8); return $sr.ReadToEnd() } finally { $fs.Dispose() }
    } catch { return $null }
}

function Format-UserText([string]$t) {
    if ([string]::IsNullOrWhiteSpace($t)) { return $null }
    $t = [regex]::Replace($t, '(?s)<system-reminder>.*?</system-reminder>', '')
    $t = [regex]::Replace($t, '(?s)<ide_selection>.*?</ide_selection>', '')
    $t = [regex]::Replace($t, '(?s)<ide_opened_file>.*?</ide_opened_file>', '')
    $t = [regex]::Replace($t, '(?s)<local-command-[^>]*>.*?</local-command-[^>]*>', '')
    $t = $t.Trim()
    if ($t -eq '') { return $null }
    if ($t -match '^<(command-name|command-message|command-args|local-command)') { return $null }
    return $t
}

function Get-TurnText($entry) {
    $msg = $entry.message
    if ($null -eq $msg) { return $null }
    $content = $msg.content
    if ($null -eq $content) { return $null }
    if ($content -is [string]) { return $content }
    $texts = New-Object System.Collections.Generic.List[string]
    foreach ($b in $content) {
        if ($b.type -eq 'text' -and $b.text) { $texts.Add([string]$b.text) }
        elseif ($b.type -eq 'tool_result') { return $null }
    }
    if ($texts.Count -eq 0) { return $null }
    return ($texts -join "`n")
}

# Derive a filesystem-safe slug - MUST match convo-sessions.ps1's Get-Slug exactly so links resolve.
function Get-Slug([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return 'conversation' }
    $s = $text.ToLowerInvariant()
    $s = [regex]::Replace($s, '(?s)```.*?```', ' ')
    $s = [regex]::Replace($s, '`[^`]*`', ' ')
    $s = [regex]::Replace($s, '[^a-z0-9]+', ' ').Trim()
    if ($s -eq '') { return 'conversation' }
    $words = $s -split '\s+' | Where-Object { $_.Length -gt 1 } | Select-Object -First 8
    if ($words.Count -eq 0) { $words = ($s -split '\s+' | Select-Object -First 8) }
    $slug = ($words -join '-')
    if ($slug.Length -gt 70) { $slug = $slug.Substring(0,70).TrimEnd('-') }
    return $slug
}

# ---- pass 1: every turn (for session splitting + TOC# text scan) + every assistant usage record ----
$files = Get-ChildItem -Path $ProjectDir -Filter *.jsonl -ErrorAction SilentlyContinue
if (-not $files) { Write-Host "No transcripts under $ProjectDir" -ForegroundColor Yellow; return }

$turns = New-Object System.Collections.Generic.List[object]
$usageEvents = New-Object System.Collections.Generic.List[object]

foreach ($f in $files) {
    $text = Read-Shared $f.FullName
    if ($null -eq $text) { continue }
    foreach ($line in ($text -split "`n")) {
        $l = $line.TrimEnd("`r")
        if ($l -eq '') { continue }
        try { $o = $l | ConvertFrom-Json } catch { continue }
        if ($o.type -ne 'user' -and $o.type -ne 'assistant') { continue }
        if (-not $o.timestamp) { continue }
        try { $when = ([datetime]$o.timestamp).ToLocalTime() } catch { continue }

        if ($o.type -eq 'assistant' -and $o.message.usage) {
            $u = $o.message.usage
            $tok = [int64]($u.input_tokens) + [int64]($u.output_tokens) + [int64]($u.cache_creation_input_tokens) + [int64]($u.cache_read_input_tokens)
            $usageEvents.Add([pscustomobject]@{ When = $when; Tokens = $tok })
        }

        if ($o.type -eq 'user' -and ($o.isMeta -eq $true -or $null -ne $o.toolUseResult)) { continue }
        $txt = Get-TurnText $o
        if ($o.type -eq 'user') { $txt = Format-UserText $txt }
        if ($null -eq $txt -or $txt -eq '') { continue }
        $who = if ($o.type -eq 'user') { 'You' } else { 'Claude' }
        $turns.Add([pscustomobject]@{ Who = $who; When = $when; Text = $txt.Trim() })
    }
}
$turns = @($turns | Sort-Object When)
$usageEvents = @($usageEvents | Sort-Object When)
if ($turns.Count -eq 0) { Write-Host "No conversational turns found." -ForegroundColor Yellow; return }

# ---- split into sessions on idle gap (identical rule to convo-sessions.ps1) ----
$sessions = New-Object System.Collections.Generic.List[object]
$cur = New-Object System.Collections.Generic.List[object]
$cur.Add($turns[0])
for ($i=1; $i -lt $turns.Count; $i++) {
    $gap = ($turns[$i].When - $turns[$i-1].When).TotalMinutes
    if ($gap -ge $SessionGapMinutes) { $sessions.Add($cur.ToArray()); $cur = New-Object System.Collections.Generic.List[object] }
    $cur.Add($turns[$i])
}
$sessions.Add($cur.ToArray())

# ---- load effort-tracker's real kbd/mouse activity log (optional - may not exist for old sessions) ----
$effortRows = @()
if (Test-Path $EffortCsv) {
    $effortRows = Import-Csv $EffortCsv | ForEach-Object {
        [pscustomobject]@{ Start = [datetime]$_.start; End = [datetime]$_.end }
    }
}

function Get-KbdMinutes([datetime]$Start, [datetime]$End) {
    $total = 0.0
    foreach ($row in $effortRows) {
        $ovStart = if ($row.Start -gt $Start) { $row.Start } else { $Start }
        $ovEnd   = if ($row.End   -lt $End)   { $row.End }   else { $End }
        if ($ovEnd -gt $ovStart) { $total += ($ovEnd - $ovStart).TotalMinutes }
    }
    return $total
}

# ---- real TOC# entries that exist right now in Overview.html (so text-scan matches are at least valid ids) ----
$validTocNumbers = New-Object System.Collections.Generic.HashSet[string]
if (Test-Path $OverviewHtml) {
    $ovText = Get-Content $OverviewHtml -Raw
    foreach ($m in [regex]::Matches($ovText, 'id="pr(\d+)"')) { [void]$validTocNumbers.Add($m.Groups[1].Value) }
}

function Get-TocHits([object[]]$sessTurns) {
    $hits = New-Object System.Collections.Generic.HashSet[string]
    foreach ($t in $sessTurns) {
        foreach ($m in [regex]::Matches($t.Text, '#(\d{1,4})\b')) {
            if ($validTocNumbers.Contains($m.Groups[1].Value)) { [void]$hits.Add($m.Groups[1].Value) }
        }
    }
    return @($hits | Sort-Object { [int]$_ })
}

# ---- release timestamps (one API call, reused for every session) - skip entirely if -Repo '' ----
$releaseTimes = @()
if ($Repo -and (Test-Path $GhExe)) {
    try {
        $json = & $GhExe api "repos/$Repo/releases" --paginate 2>$null
        $rel = $json | ConvertFrom-Json
        $releaseTimes = @($rel | ForEach-Object { [pscustomobject]@{ Tag = $_.tag_name; When = ([datetime]$_.published_at).ToLocalTime() } })
    } catch { Write-Host "Could not fetch releases for $Repo - Release column will be blank." -ForegroundColor Yellow }
}

function Get-ReleaseHit([datetime]$Start, [datetime]$End) {
    $hit = $releaseTimes | Where-Object { $_.When -ge $Start -and $_.When -le $End } | Select-Object -First 1
    return $hit
}

# ---- build one row per session, newest first ----
$rows = New-Object System.Collections.Generic.List[object]
$usedNames = @{}
foreach ($s in $sessions) {
    $start = $s[0].When
    $end   = $s[-1].When
    $firstPrompt = ($s | Where-Object { $_.Who -eq 'You' } | Select-Object -First 1).Text
    $slug  = Get-Slug $firstPrompt
    $base  = "{0}_{1}" -f $start.ToString('yyyy-MM-dd_HHmm'), $slug
    $name  = $base
    $k = 2; while ($usedNames.ContainsKey($name)) { $name = "$base-$k"; $k++ }
    $usedNames[$name] = $true

    $tokens = ($usageEvents | Where-Object { $_.When -ge $start -and $_.When -le $end } | Measure-Object -Property Tokens -Sum).Sum
    if (-not $tokens) { $tokens = 0 }
    $kbdMin = [math]::Round((Get-KbdMinutes $start $end), 1)
    $toc = Get-TocHits $s
    $rel = Get-ReleaseHit $start $end

    $rows.Add([pscustomobject]@{
        Start    = $start
        End      = $end
        Name     = $name
        Turns    = $s.Count
        Tokens   = $tokens
        KbdMin   = $kbdMin
        HasKbd   = ($effortRows.Count -gt 0 -and ($effortRows | Where-Object { $_.End -ge $start -and $_.Start -le $end }).Count -gt 0)
        Toc      = $toc
        Release  = $rel
    })
}
$rows = @($rows | Sort-Object Start -Descending)

# ---- render index.html ----
function Esc([string]$s) { return $s -replace '&','&amp;' -replace '<','&lt;' -replace '>','&gt;' }

$sb = New-Object System.Text.StringBuilder
[void]$sb.Append(@"
<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>ioSender sessions</title>
<style>
  :root { color-scheme: light dark; }
  * { box-sizing: border-box; }
  body { margin:0; font:14px/1.5 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif; background:#f4f5f7; color:#1c1e21; }
  header { background:#24292f; color:#fff; padding:16px 20px; }
  header h1 { margin:0; font-size:18px; }
  header .sub { font-size:12px; opacity:.8; margin-top:4px; }
  main { max-width:1200px; margin:0 auto; padding:16px; }
  .tablewrap { width:100%; overflow-x:auto; border-radius:8px; }
  table { table-layout:fixed; border-collapse:collapse; background:#fff; }
  th, td { text-align:left; padding:8px 10px; border-bottom:1px solid #e4e6ea; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  th { background:#eef0f3; font-size:11px; text-transform:uppercase; letter-spacing:.03em; color:#57606a; }
  tr:hover td { background:#f6f8fa; }
  td.wrap { white-space:normal; max-width:360px; }
  a { color:#0969da; text-decoration:none; } a:hover { text-decoration:underline; }
  .muted { color:#8a8f98; }
  .yes { color:#1a7f37; font-weight:600; }
  .toc-chip { display:inline-block; background:#eef0f3; border-radius:999px; padding:1px 7px; margin:1px 2px 1px 0; font-size:11px; }
  tr.tot td { font-weight:700; border-top:2px solid #24292f; border-bottom:none; background:#eef0f3; white-space:normal; overflow:visible; text-overflow:clip; }
  tr.tot:hover td { background:#eef0f3; }
  @media (prefers-color-scheme: dark) {
    body { background:#0e0f11; color:#dbdce0; }
    header { background:#161b22; }
    table { background:#191b1f; }
    th { background:#1c2128; color:#9198a1; }
    th, td { border-color:#2a2d33; }
    tr:hover td { background:#20242b; }
    a { color:#4493f8; }
    .toc-chip { background:#2a2d33; }
    .yes { color:#3fb950; }
    tr.tot td { border-top-color:#dbdce0; background:#1c2128; }
    tr.tot:hover td { background:#1c2128; }
  }
</style></head><body>
<header><h1>ioSender working sessions</h1><div class="sub">$($rows.Count) sessions &middot; regenerate: tools\effort\build-session-index.ps1 &middot; Tokens = raw sum of every underlying API call's input+output+cache (a rough proxy, not a billing figure - run /cost in Claude Code for the exact number) &middot; Kbd time / TOC# / Release are all best-effort (blank = not available or not detected, not necessarily zero)</div></header>
<main><div class="tablewrap"><table>
<colgroup>
  <col style="width:240px"><col style="width:80px"><col style="width:80px"><col style="width:60px">
  <col style="width:70px"><col style="width:320px"><col style="width:70px"><col style="width:340px">
</colgroup>
<tr><th>Time Period</th><th>Elapsed</th><th>Kbd time</th><th>Turns</th><th>Tokens</th><th>TOC #</th><th>Release</th><th>Conversation</th></tr>
"@)

function Get-RoundedMinute([datetime]$dt) {
    if ($dt.Second -ge 30) { return $dt.AddSeconds(60 - $dt.Second) }
    return $dt.AddSeconds(-$dt.Second)
}

foreach ($r in $rows) {
    $dur = $r.End - $r.Start
    $durStr = if ($dur.TotalHours -ge 1) { "{0:0}h {1:0}m" -f [math]::Floor($dur.TotalHours), $dur.Minutes } else { "{0:0}m" -f $dur.TotalMinutes }
    $rStart = Get-RoundedMinute $r.Start
    $rEnd   = Get-RoundedMinute $r.End
    $periodStr = "{0} {1} - {2}" -f $rStart.ToString('yyyy-MM-dd'), $rStart.ToString('h:mmtt').ToLower(), $rEnd.ToString('h:mmtt').ToLower()
    $kbdStr = if ($r.HasKbd) { if ($r.KbdMin -ge 60) { "{0:0}h {1:0}m" -f [math]::Floor($r.KbdMin/60), ($r.KbdMin % 60) } else { "{0:0}m" -f $r.KbdMin } } else { '<span class="muted">-</span>' }
    $tocStr = if ($r.Toc.Count -gt 0) { ($r.Toc | ForEach-Object { "<span class=`"toc-chip`">#$_</span>" }) -join '' } else { '<span class="muted">-</span>' }
    $relStr = if ($r.Release) { "<span class=`"yes`">$(Esc $r.Release.Tag)</span>" } else { '<span class="muted">-</span>' }
    $tokStr = if ($r.Tokens -ge 1000000) { "{0:N1}M" -f ($r.Tokens/1000000) }
              elseif ($r.Tokens -ge 1000) { "{0:N1}k" -f ($r.Tokens/1000) }
              else { "$($r.Tokens)" }
    $link = "sessions/$($r.Name).html"
    $title = Esc ($r.Name -replace '^\d{4}-\d{2}-\d{2}_\d{4}_','' -replace '-',' ')

    [void]$sb.Append("<tr>")
    [void]$sb.Append("<td>$periodStr</td>")
    [void]$sb.Append("<td>$durStr</td>")
    [void]$sb.Append("<td>$kbdStr</td>")
    [void]$sb.Append("<td>$($r.Turns)</td>")
    [void]$sb.Append("<td>$tokStr</td>")
    [void]$sb.Append("<td class=`"wrap`">$tocStr</td>")
    [void]$sb.Append("<td>$relStr</td>")
    [void]$sb.Append("<td class=`"wrap`"><a href=`"$link`">$title</a></td>")
    [void]$sb.Append("</tr>`n")
}

function Format-Mins([double]$mins) {
    if ($mins -ge 60) { return "{0:0}h {1:0}m" -f [math]::Floor($mins/60), ($mins % 60) }
    return "{0:0}m" -f $mins
}

$totElapsedMin = ($rows | ForEach-Object { ($_.End - $_.Start).TotalMinutes } | Measure-Object -Sum).Sum
$totKbdMin     = ($rows | Measure-Object -Property KbdMin -Sum).Sum
$totTurns      = ($rows | Measure-Object -Property Turns -Sum).Sum
$totTokens     = ($rows | Measure-Object -Property Tokens -Sum).Sum
$distinctToc   = New-Object System.Collections.Generic.HashSet[string]
foreach ($r in $rows) { foreach ($n in $r.Toc) { [void]$distinctToc.Add($n) } }
$totReleases   = @($rows | Where-Object { $_.Release } | ForEach-Object { $_.Release.Tag } | Select-Object -Unique).Count
$sessionsWithKbd = @($rows | Where-Object { $_.HasKbd }).Count

$totTokStr = if ($totTokens -ge 1000000) { "{0:N1}M" -f ($totTokens/1000000) }
             elseif ($totTokens -ge 1000) { "{0:N1}k" -f ($totTokens/1000) }
             else { "$totTokens" }
$kbdCoverageNote = if ($rows.Count -gt 0) { " ({0} of {1} sessions)" -f $sessionsWithKbd, $rows.Count } else { "" }

[void]$sb.Append("<tr class=`"tot`">")
[void]$sb.Append("<td>Totals ($($rows.Count) sessions)</td>")
[void]$sb.Append("<td>$(Format-Mins $totElapsedMin)</td>")
[void]$sb.Append("<td>$(Format-Mins $totKbdMin)$kbdCoverageNote</td>")
[void]$sb.Append("<td>$totTurns</td>")
[void]$sb.Append("<td>$totTokStr</td>")
[void]$sb.Append("<td>$($distinctToc.Count) distinct</td>")
[void]$sb.Append("<td>$totReleases</td>")
[void]$sb.Append("<td></td>")
[void]$sb.Append("</tr>`n")

[void]$sb.Append("</table></div></main></body></html>`n")

$out = Join-Path $OutDir 'index.html'
Set-Content -Path $out -Value $sb.ToString() -Encoding utf8
Write-Host "==> wrote $out ($($rows.Count) sessions)" -ForegroundColor Green
