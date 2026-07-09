<#
.SYNOPSIS
  Merges EVERY Claude Code transcript for this project into one chronological stream, then re-splits it into
  "sessions" wherever the idle gap between consecutive entries exceeds a threshold. Each detected session is
  written to its own descriptively-named, self-contained HTML file (start/stop time in the header and footer).

.DESCRIPTION
  Companion to convo-logger.ps1. That script maps one transcript file -> one .html (the CLI's own session
  boundaries). This script ignores those boundaries: it pools all kept turns (your prompts + Claude's prose)
  across every *.jsonl transcript, sorts them by timestamp, and cuts a new session whenever the time since the
  previous entry is >= -SessionGapMinutes. So a /clear that started a fresh transcript 3 minutes later stays in
  ONE session, and a single transcript left open across an overnight break splits into TWO.

  Gap default (60 min) was chosen from the observed distribution: 99% of inter-entry gaps are under ~9 min, so
  any idle of an hour+ reliably marks a return to a fresh sitting. Run -Analyze to reprint that distribution.

  Each output file is named  <yyyy-MM-dd_HHmm>_<slug>.html  where the slug is derived from the session's first
  real user prompt, e.g.  2026-07-08_1518_merge-all-conversation-transcripts.html

.EXAMPLE
  # Rebuild all per-time-gap session files (default 60-min boundary):
  powershell -ExecutionPolicy Bypass -File tools\effort\convo-sessions.ps1

.EXAMPLE
  # End-of-session capture: regenerate ONLY the current (most-recent) session, always up to date:
  powershell -ExecutionPolicy Bypass -File tools\effort\convo-sessions.ps1 -Once

.EXAMPLE
  # Tighter boundary + just reprint the gap stats to help pick a threshold:
  powershell -ExecutionPolicy Bypass -File tools\effort\convo-sessions.ps1 -SessionGapMinutes 30
  powershell -ExecutionPolicy Bypass -File tools\effort\convo-sessions.ps1 -Analyze
#>
param(
    [string]$ProjectDir       = "$env:USERPROFILE\.claude\projects\c--github-ioSender",
    [string]$OutDir           = "$env:USERPROFILE\Downloads\ClaudeConv\sessions",
    [int]$SessionGapMinutes   = 60,     # idle gap (minutes) that starts a new session
    [switch]$Once,                      # write ONLY the most-recent detected session (end-of-session capture, always current)
    [switch]$Analyze,                   # print the inter-entry gap distribution and exit (no files written)
    [switch]$IncludeThinking            # also include Claude's internal "thinking" blocks (off by default)
)

if (-not $Analyze -and -not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# --- read a file that Claude Code may have open for writing (share read+write, never lock it) ---
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
        switch ($b.type) {
            'text'        { if ($b.text)     { $texts.Add([string]$b.text) } }
            'thinking'    { if ($IncludeThinking -and $b.thinking) { $texts.Add("(thinking)`n" + [string]$b.thinking) } }
            'tool_result' { return $null }
            default       { }
        }
    }
    if ($texts.Count -eq 0) { return $null }
    return ($texts -join "`n")
}

# Pasted images live on genuine user turns as base64 content blocks
# ({type:image, source:{type:base64, media_type, data}}). Return them as ready-to-embed data: URIs so the
# HTML is self-contained (no image folder). Images Claude viewed via the Read tool are toolUseResult turns,
# filtered out before this runs - so only what the user actually pasted is captured.
function Get-TurnImages($entry) {
    $msg = $entry.message
    if ($null -eq $msg) { return @() }
    $content = $msg.content
    if ($null -eq $content -or $content -is [string]) { return @() }
    $imgs = New-Object System.Collections.Generic.List[string]
    foreach ($b in $content) {
        if ($b.type -eq 'image' -and $b.source -and $b.source.type -eq 'base64' -and $b.source.data) {
            $mt = if ($b.source.media_type) { [string]$b.source.media_type } else { 'image/png' }
            $imgs.Add("data:$mt;base64," + [string]$b.source.data)
        }
    }
    return $imgs.ToArray()
}

# One transcript line -> a turn object { Who; When(datetime); Ts(string); Text } or $null.
function ConvertFrom-Line([string]$line) {
    $line = $line.TrimEnd("`r")
    if ($line -eq '') { return $null }
    try { $o = $line | ConvertFrom-Json } catch { return $null }
    $t = $o.type
    if ($t -ne 'user' -and $t -ne 'assistant') { return $null }
    if ($t -eq 'user' -and ($o.isMeta -eq $true -or $null -ne $o.toolUseResult)) { return $null }

    $text = Get-TurnText $o
    $images = if ($t -eq 'user') { Get-TurnImages $o } else { @() }

    if ($t -eq 'user') {
        if ($null -ne $text) { $text = Format-UserText $text }
        # Keep a paste that is images-only (no surviving text) as long as it carries at least one image.
        if (($null -eq $text -or $text -eq '') -and $images.Count -eq 0) { return $null }
        if ($null -eq $text) { $text = '' }
        $who = 'You'
    } else {
        if ($null -eq $text) { return $null }
        $text = $text.Trim()
        if ($text -eq '') { return $null }
        $who = 'Claude'
    }
    if (-not $o.timestamp) { return $null }
    try { $when = ([datetime]$o.timestamp).ToLocalTime() } catch { return $null }
    return [pscustomobject]@{ Who = $who; When = $when; Ts = $when.ToString('yyyy-MM-dd HH:mm:ss'); Text = $text; Images = $images }
}

function Protect-Html([string]$s) { return $s.Replace('&','&amp;').Replace('<','&lt;').Replace('>','&gt;') }

function ConvertTo-TurnHtml([string]$md) {
    $blocks = New-Object System.Collections.Generic.List[string]
    $body = [regex]::Replace($md, '(?s)```[^\n]*\n(.*?)```', {
        param($m)
        $i = $blocks.Count
        $blocks.Add($m.Groups[1].Value.TrimEnd("`r","`n"))
        "@@CODEBLOCK${i}@@"
    })
    $body = Protect-Html $body
    $body = [regex]::Replace($body, '(?m)^\s{0,3}#{1,6}\s+(.*)$', '<strong>$1</strong>')
    $body = [regex]::Replace($body, '`([^`]+)`', '<code>$1</code>')
    $body = [regex]::Replace($body, '\*\*([^*]+)\*\*', '<strong>$1</strong>')
    for ($i = 0; $i -lt $blocks.Count; $i++) {
        $code = Protect-Html $blocks[$i]
        $body = $body.Replace("@@CODEBLOCK${i}@@", "</div><pre class=`"code`">$code</pre><div class=`"content`">")
    }
    return "<div class=`"content`">$body</div>"
}

# Derive a filesystem-safe slug from a session's first user prompt (max ~8 words).
function Get-Slug([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return 'conversation' }
    $s = $text.ToLowerInvariant()
    $s = [regex]::Replace($s, '(?s)```.*?```', ' ')      # drop code blocks
    $s = [regex]::Replace($s, '`[^`]*`', ' ')            # drop inline code
    $s = [regex]::Replace($s, '[^a-z0-9]+', ' ').Trim()
    if ($s -eq '') { return 'conversation' }
    $words = $s -split '\s+' | Where-Object { $_.Length -gt 1 } | Select-Object -First 8
    if ($words.Count -eq 0) { $words = ($s -split '\s+' | Select-Object -First 8) }
    $slug = ($words -join '-')
    if ($slug.Length -gt 70) { $slug = $slug.Substring(0,70).TrimEnd('-') }
    return $slug
}

function Build-Html([object[]]$turns, [string]$Title, [datetime]$Start, [datetime]$End) {
    $dur = $End - $Start
    $durStr = if ($dur.TotalHours -ge 1) { "{0:0}h {1:0}m" -f [math]::Floor($dur.TotalHours), $dur.Minutes } else { "{0:0}m" -f $dur.TotalMinutes }
    $startStr = $Start.ToString('dddd, MMMM d yyyy  HH:mm:ss')
    $endStr   = $End.ToString('dddd, MMMM d yyyy  HH:mm:ss')
    $meta = "Started $startStr &middot; Ended $endStr &middot; $durStr &middot; $($turns.Count) turns"
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append(@"
<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$Title</title>
<style>
  :root { color-scheme: light dark; }
  * { box-sizing: border-box; }
  body { margin:0; font:15px/1.55 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;
         background:#f4f5f7; color:#1c1e21; }
  header { position:sticky; top:0; background:#24292f; color:#fff; padding:12px 20px; z-index:1; }
  header h1 { margin:0; font-size:16px; font-weight:600; }
  header .meta { font-size:12px; opacity:.8; margin-top:3px; }
  main { max-width:900px; margin:0 auto; padding:20px 16px 40px; }
  footer { max-width:900px; margin:0 auto; padding:16px; color:#8a8f98; font-size:12px;
           border-top:1px solid #dfe1e5; text-align:center; }
  .turn { border-radius:10px; padding:12px 16px; margin:14px 0; border:1px solid #dfe1e5; background:#fff; }
  .turn.you { background:#eef4ff; border-color:#c9dbff; }
  .turnhead { display:flex; justify-content:space-between; align-items:baseline; margin-bottom:6px; }
  .who { font-weight:700; font-size:13px; }
  .you .who { color:#1a56db; }
  .claude .who { color:#6b21a8; }
  .ts { font-size:11px; color:#8a8f98; font-variant-numeric:tabular-nums; }
  .content { white-space:pre-wrap; word-wrap:break-word; }
  .paste { display:block; max-width:100%; height:auto; margin:10px 0 2px; border-radius:8px; border:1px solid #dfe1e5; }
  code { background:#eaecef; border-radius:4px; padding:1px 5px; font:13px/1.4 Consolas,Menlo,monospace; }
  pre.code { background:#0d1117; color:#e6edf3; padding:12px 14px; border-radius:8px; overflow-x:auto;
             white-space:pre; margin:8px 0; font:13px/1.45 Consolas,Menlo,monospace; }
  pre.code code { background:none; padding:0; color:inherit; }
  @media (prefers-color-scheme: dark) {
    body { background:#0e0f11; color:#dbdce0; }
    footer { border-color:#2a2d33; }
    .turn { background:#191b1f; border-color:#2a2d33; }
    .turn.you { background:#16233b; border-color:#274472; }
    .paste { border-color:#2a2d33; }
    .you .who { color:#7aa7ff; } .claude .who { color:#d0a3ff; }
    code { background:#2a2d33; }
  }
</style></head><body>
<header><h1>$Title</h1><div class="meta">$meta</div></header>
<main>
"@)
    foreach ($t in $turns) {
        $cls = if ($t.Who -eq 'You') { 'you' } else { 'claude' }
        [void]$sb.Append("<section class=`"turn $cls`"><div class=`"turnhead`"><span class=`"who`">$($t.Who)</span><span class=`"ts`">$($t.Ts)</span></div>")
        [void]$sb.Append((ConvertTo-TurnHtml $t.Text))
        if ($t.Images -and $t.Images.Count -gt 0) {
            foreach ($src in $t.Images) { [void]$sb.Append("<img class=`"paste`" src=`"$src`" alt=`"pasted image`">") }
        }
        [void]$sb.Append("</section>`n")
    }
    [void]$sb.Append("</main>`n<footer>Session started <strong>$startStr</strong> &middot; ended <strong>$endStr</strong> &middot; duration $durStr &middot; $($turns.Count) turns</footer>`n</body></html>`n")
    return $sb.ToString()
}

# ---- gather every turn across every transcript, sorted chronologically ----
$files = Get-ChildItem -Path $ProjectDir -Filter *.jsonl -ErrorAction SilentlyContinue
if (-not $files) { Write-Host "No transcripts under $ProjectDir" -ForegroundColor Yellow; return }

$all = New-Object System.Collections.Generic.List[object]
foreach ($f in $files) {
    $text = Read-Shared $f.FullName
    if ($null -eq $text) { continue }
    foreach ($line in ($text -split "`n")) {
        $turn = ConvertFrom-Line $line
        if ($turn) { $all.Add($turn) }
    }
}
$turns = @($all | Sort-Object When)
if ($turns.Count -eq 0) { Write-Host "No conversational turns found." -ForegroundColor Yellow; return }

# ---- ANALYZE: print gap distribution and exit ----
if ($Analyze) {
    $gaps = for ($i=1; $i -lt $turns.Count; $i++) { ($turns[$i].When - $turns[$i-1].When).TotalMinutes }
    $g = @($gaps | Sort-Object)
    Write-Host ("Entries: {0}   Range: {1} -> {2}" -f $turns.Count, $turns[0].Ts, $turns[-1].Ts) -ForegroundColor Cyan
    Write-Host "`nInter-entry gap percentiles (minutes):"
    foreach ($p in 50,75,90,95,99) { $idx=[int][math]::Floor(($p/100.0)*($g.Count-1)); Write-Host ("  p{0,-3}: {1,8:N2}" -f $p, $g[$idx]) }
    Write-Host "`nSessions detected at various thresholds:"
    foreach ($thr in 15,30,45,60,90,120,240) {
        $n = 1; foreach ($gap in $gaps) { if ($gap -ge $thr) { $n++ } }
        Write-Host ("  gap >= {0,4} min -> {1,4} sessions" -f $thr, $n)
    }
    return
}

# ---- split into sessions on idle gap, write one HTML each ----
$sessions = New-Object System.Collections.Generic.List[object]
$cur = New-Object System.Collections.Generic.List[object]
$cur.Add($turns[0])
for ($i=1; $i -lt $turns.Count; $i++) {
    $gap = ($turns[$i].When - $turns[$i-1].When).TotalMinutes
    if ($gap -ge $SessionGapMinutes) { $sessions.Add($cur.ToArray()); $cur = New-Object System.Collections.Generic.List[object] }
    $cur.Add($turns[$i])
}
$sessions.Add($cur.ToArray())

# -Once (end-of-session capture): keep only the most-recent session so the current sitting is always current.
# (Build an explicit list-of-sessions; each session is itself an array, so never let it get flattened.)
$toWrite = New-Object System.Collections.Generic.List[object]
if ($Once) { $toWrite.Add($sessions[$sessions.Count - 1]) }
else       { foreach ($sess in $sessions) { $toWrite.Add($sess) } }

Write-Host ("Merged {0} turns from {1} transcript(s) -> {2} session(s) at a {3}-min boundary{4}" -f `
    $turns.Count, $files.Count, $sessions.Count, $SessionGapMinutes, `
    $(if ($Once) { " (writing the current one only)" } else { "" })) -ForegroundColor Cyan

$usedNames = @{}
foreach ($s in $toWrite) {
    $start = $s[0].When
    $end   = $s[-1].When
    $firstPrompt = ($s | Where-Object { $_.Who -eq 'You' } | Select-Object -First 1).Text
    $slug  = Get-Slug $firstPrompt
    $base  = "{0}_{1}" -f $start.ToString('yyyy-MM-dd_HHmm'), $slug
    $name  = $base
    $k = 2; while ($usedNames.ContainsKey($name)) { $name = "$base-$k"; $k++ }
    $usedNames[$name] = $true

    $title = "Conversation - {0}" -f $start.ToString('MMM d, HH:mm')
    $html  = Build-Html $s $title $start $end
    $out   = Join-Path $OutDir ($name + '.html')
    Set-Content -Path $out -Value $html -Encoding utf8
    # stamp the file's create/modify times to the session start so Explorer sorts by when it happened
    $fi = Get-Item $out
    $fi.CreationTime  = $start
    $fi.LastWriteTime = $start
    Write-Host ("  {0,-55}  {1,4} turns  {2}" -f ($name.Substring(0,[math]::Min(55,$name.Length))), $s.Count, $start.ToString('MM-dd HH:mm')) -ForegroundColor Green
}
Write-Host ("Done -> {0}" -f $OutDir) -ForegroundColor Cyan
