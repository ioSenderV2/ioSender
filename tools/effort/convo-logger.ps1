<#
.SYNOPSIS
  Logs the Claude Code CONVERSATION (your prompts + Claude's prose replies) to a per-session HTML file,
  stripping all the noise - tool calls, command output, file diffs, and Claude's internal thinking.

.DESCRIPTION
  Companion to effort-tracker.ps1. Where that logs your active HOURS, this logs what was actually SAID.
  The source is the Claude Code session transcript JSONL that the CLI writes under
  %USERPROFILE%\.claude\projects\<project>\<session-guid>.jsonl. Each transcript line is one entry; this
  keeps only:
    - real user prompts        (type=user, not isMeta, not a tool result)
    - Claude's text responses  (type=assistant, the "text" content blocks)
  and drops tool_use / tool_result (command output + diffs), "thinking" blocks, and the injected
  system-reminder / slash-command wrapper noise.

  Output is a self-contained, styled .html file per session (markdown lightly rendered: fenced code
  blocks, inline code, bold, headings). Each run regenerates the target session's HTML in full from the
  transcript, so it is always idempotent.

  Two modes:
    ONE-SHOT (-Once, default use) - regenerate the CURRENT (newest) session's .html, then exit. This is the
                       end-of-session step (transcript is on disk, so a one-shot catches everything).
    BATCH    (-All)  - regenerate every existing *.jsonl in the project to its own .html, then exit.
    FOLLOW   (no switch) - regenerate the current session every -PollSeconds; leave running in the background.

.EXAMPLE
  # Regenerate the current session (end-of-session capture):
  powershell -ExecutionPolicy Bypass -File tools\effort\convo-logger.ps1 -Once

.EXAMPLE
  # Convert every past transcript once:
  powershell -ExecutionPolicy Bypass -File tools\effort\convo-logger.ps1 -All
#>
param(
    [string]$ProjectDir = "$env:USERPROFILE\.claude\projects\c--github-ioSender",
    [string]$OutDir     = "$env:USERPROFILE\Downloads\ClaudeConv",
    [int]$PollSeconds   = 5,
    [switch]$All,                 # regenerate every existing transcript, then exit
    [switch]$Once,                # regenerate the CURRENT session, then exit (end-of-session / maintenance)
    [switch]$IncludeThinking      # also include Claude's internal "thinking" blocks (off by default)
)

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# --- read a file that Claude Code may have open for writing (share read+write, never lock it) ---
function Read-Shared([string]$Path) {
    try {
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
            return $sr.ReadToEnd()
        } finally { $fs.Dispose() }
    } catch { return $null }
}

function Format-UserText([string]$t) {
    if ([string]::IsNullOrWhiteSpace($t)) { return $null }
    $t = [regex]::Replace($t, '(?s)<system-reminder>.*?</system-reminder>', '')
    $t = [regex]::Replace($t, '(?s)<ide_selection>.*?</ide_selection>', '')
    $t = [regex]::Replace($t, '(?s)<local-command-[^>]*>.*?</local-command-[^>]*>', '')
    $t = $t.Trim()
    if ($t -eq '') { return $null }
    if ($t -match '^<(command-name|command-message|command-args|local-command)') { return $null }
    return $t
}

# Extract the conversational text for one entry, or $null if it carries none we want.
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

# One transcript line -> a turn object { Who; Ts; Text } or $null.
function ConvertFrom-Line([string]$line) {
    $line = $line.TrimEnd("`r")
    if ($line -eq '') { return $null }
    try { $o = $line | ConvertFrom-Json } catch { return $null }
    $t = $o.type
    if ($t -ne 'user' -and $t -ne 'assistant') { return $null }
    if ($t -eq 'user' -and ($o.isMeta -eq $true -or $null -ne $o.toolUseResult)) { return $null }

    $text = Get-TurnText $o
    if ($null -eq $text) { return $null }

    if ($t -eq 'user') {
        $text = Format-UserText $text
        if ($null -eq $text) { return $null }
        $who = 'You'
    } else {
        $text = $text.Trim()
        if ($text -eq '') { return $null }
        $who = 'Claude'
    }
    $ts = ''
    if ($o.timestamp) { try { $ts = ([datetime]$o.timestamp).ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss') } catch { } }
    return [pscustomobject]@{ Who = $who; Ts = $ts; Text = $text }
}

function Protect-Html([string]$s) {
    return $s.Replace('&','&amp;').Replace('<','&lt;').Replace('>','&gt;')
}

# Light markdown -> HTML for a turn body: fenced code blocks, inline code, bold, headings; other text is
# rendered inside a white-space:pre-wrap block so lists/indentation/line breaks survive as written.
function ConvertTo-TurnHtml([string]$md) {
    $blocks = New-Object System.Collections.Generic.List[string]
    # pull fenced code blocks out first so their contents are never markdown-processed
    $body = [regex]::Replace($md, '(?s)```[^\n]*\n(.*?)```', {
        param($m)
        $i = $blocks.Count
        $blocks.Add($m.Groups[1].Value.TrimEnd("`r","`n"))
        "@@CODEBLOCK${i}@@"
    })
    $body = Protect-Html $body
    # headings -> bold lines
    $body = [regex]::Replace($body, '(?m)^\s{0,3}#{1,6}\s+(.*)$', '<strong>$1</strong>')
    # inline code + bold
    $body = [regex]::Replace($body, '`([^`]+)`', '<code>$1</code>')
    $body = [regex]::Replace($body, '\*\*([^*]+)\*\*', '<strong>$1</strong>')
    # re-insert code blocks
    for ($i = 0; $i -lt $blocks.Count; $i++) {
        $code = Protect-Html $blocks[$i]
        $body = $body.Replace("@@CODEBLOCK${i}@@", "</div><pre class=`"code`">$code</pre><div class=`"content`">")
    }
    return "<div class=`"content`">$body</div>"
}

function Build-Html([object[]]$turns, [string]$SessionId, [string]$SourceName) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append(@"
<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Conversation $SessionId</title>
<style>
  :root { color-scheme: light dark; }
  * { box-sizing: border-box; }
  body { margin:0; font:15px/1.55 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;
         background:#f4f5f7; color:#1c1e21; }
  header { position:sticky; top:0; background:#24292f; color:#fff; padding:12px 20px; }
  header h1 { margin:0; font-size:16px; font-weight:600; }
  header .meta { font-size:12px; opacity:.75; margin-top:2px; }
  main { max-width:900px; margin:0 auto; padding:20px 16px 80px; }
  .turn { border-radius:10px; padding:12px 16px; margin:14px 0; border:1px solid #dfe1e5; background:#fff; }
  .turn.you { background:#eef4ff; border-color:#c9dbff; }
  .turnhead { display:flex; justify-content:space-between; align-items:baseline; margin-bottom:6px; }
  .who { font-weight:700; font-size:13px; }
  .you .who { color:#1a56db; }
  .claude .who { color:#6b21a8; }
  .ts { font-size:11px; color:#8a8f98; font-variant-numeric:tabular-nums; }
  .content { white-space:pre-wrap; word-wrap:break-word; }
  code { background:#eaecef; border-radius:4px; padding:1px 5px; font:13px/1.4 Consolas,Menlo,monospace; }
  pre.code { background:#0d1117; color:#e6edf3; padding:12px 14px; border-radius:8px; overflow-x:auto;
             white-space:pre; margin:8px 0; font:13px/1.45 Consolas,Menlo,monospace; }
  pre.code code { background:none; padding:0; color:inherit; }
  @media (prefers-color-scheme: dark) {
    body { background:#0e0f11; color:#dbdce0; }
    .turn { background:#191b1f; border-color:#2a2d33; }
    .turn.you { background:#16233b; border-color:#274472; }
    .you .who { color:#7aa7ff; } .claude .who { color:#d0a3ff; }
    code { background:#2a2d33; }
  }
</style></head><body>
<header><h1>Conversation log</h1><div class="meta">session $SessionId &middot; $($turns.Count) turns &middot; $SourceName</div></header>
<main>
"@)
    foreach ($t in $turns) {
        $cls = if ($t.Who -eq 'You') { 'you' } else { 'claude' }
        [void]$sb.Append("<section class=`"turn $cls`"><div class=`"turnhead`"><span class=`"who`">$($t.Who)</span><span class=`"ts`">$($t.Ts)</span></div>")
        [void]$sb.Append((ConvertTo-TurnHtml $t.Text))
        [void]$sb.Append("</section>`n")
    }
    [void]$sb.Append("</main></body></html>`n")
    return $sb.ToString()
}

# Regenerate one transcript file -> its .html. Returns the turn count.
function Convert-Transcript([string]$Path) {
    $id  = [System.IO.Path]::GetFileNameWithoutExtension($Path)
    $out = Join-Path $OutDir ($id + '.html')
    $text = Read-Shared $Path
    if ($null -eq $text) { return 0 }
    $turns = New-Object System.Collections.Generic.List[object]
    foreach ($line in ($text -split "`n")) {
        $turn = ConvertFrom-Line $line
        if ($turn) { $turns.Add($turn) }
    }
    $html = Build-Html $turns.ToArray() $id ([System.IO.Path]::GetFileName($Path))
    Set-Content -Path $out -Value $html -Encoding utf8
    return $turns.Count
}

function Get-Newest { Get-ChildItem -Path $ProjectDir -Filter *.jsonl -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 }

# ---- BATCH ----
if ($All) {
    $files = Get-ChildItem -Path $ProjectDir -Filter *.jsonl -ErrorAction SilentlyContinue | Sort-Object LastWriteTime
    if (-not $files) { Write-Host "No transcripts under $ProjectDir" -ForegroundColor Yellow; return }
    Write-Host ("Converting {0} transcript(s) -> {1}" -f $files.Count, $OutDir) -ForegroundColor Cyan
    foreach ($f in $files) {
        $n = Convert-Transcript $f.FullName
        Write-Host ("  {0}  {1,6} turns" -f $f.Name, $n) -ForegroundColor Green
    }
    Write-Host "Done." -ForegroundColor Cyan
    return
}

# ---- ONE-SHOT ----
if ($Once) {
    $newest = Get-Newest
    if ($newest) {
        $n = Convert-Transcript $newest.FullName
        Write-Host ("{0}: {1} turns -> {2}.html" -f (Get-Date -Format HH:mm:ss), $n, $newest.BaseName) -ForegroundColor Green
    }
    return
}

# ---- FOLLOW ----
Write-Host ("convo-logger: following newest session in $ProjectDir  (poll ${PollSeconds}s)") -ForegroundColor Cyan
Write-Host "Leave running. Ctrl+C to stop." -ForegroundColor DarkGray
while ($true) {
    $newest = Get-Newest
    if ($newest) { [void](Convert-Transcript $newest.FullName) }
    Start-Sleep -Seconds $PollSeconds
}
