<#
.SYNOPSIS
  Shared helpers for the conversation-session archive: transcript parsing, session HTML rendering,
  the sessions.json manifest, and the index.html renderer.

.DESCRIPTION
  Dot-source this from convo-sessions.ps1 / build-session-index.ps1 / migrate-session-manifest.ps1.
  It carries no top-level side effects.

  THE MODEL (changed 2026-08-02, replacing the 60-minute idle-gap heuristic):

    * A session ENDS when the end-of-session capture runs. That run is the boundary - we do not
      guess one from idle time. Everything logged after the previous capture is the next session.
    * sessions.json is the DURABLE record. Claude Code deletes transcripts after cleanupPeriodDays
      (30 by default), so anything derived only-on-demand from transcripts is lost on that schedule.
      The manifest is append-only; a session recorded once is never recomputed and never falls off.
    * Capture is INCREMENTAL. The checkpoint stores each transcript's size at capture time, so an
      untouched transcript is skipped without being opened. Only files that grew get parsed.
#>

Set-StrictMode -Off

# ---------------------------------------------------------------- transcript reading

# Read a file Claude Code may have open for writing (share read+write, never lock it).
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

function Get-TurnText($entry, [bool]$IncludeThinking = $false) {
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

# Pasted images live on genuine user turns as base64 content blocks. Return them as ready-to-embed
# data: URIs so the HTML stays self-contained. Images Claude viewed via Read are toolUseResult turns,
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

# One transcript line -> @{ Who; When; Ts; Text; Images; Tokens } or $null.
# Tokens is the assistant entry's own usage total (0 for user turns); kept on the turn so callers
# never need a second pass over the same file just to total usage.
function ConvertFrom-Line([string]$line, [bool]$IncludeThinking = $false) {
    $line = $line.TrimEnd("`r")
    if ($line -eq '') { return $null }
    try { $o = $line | ConvertFrom-Json } catch { return $null }
    $t = $o.type
    if ($t -ne 'user' -and $t -ne 'assistant') { return $null }
    if (-not $o.timestamp) { return $null }
    try { $when = ([datetime]$o.timestamp).ToLocalTime() } catch { return $null }

    $tokens = 0
    if ($t -eq 'assistant' -and $o.message -and $o.message.usage) {
        $u = $o.message.usage
        $tokens = [int64]($u.input_tokens) + [int64]($u.output_tokens) + `
                  [int64]($u.cache_creation_input_tokens) + [int64]($u.cache_read_input_tokens)
    }

    # A turn that carries no prose still carries tokens - emit it as a token-only record so usage
    # totals stay honest, and let callers drop Who -eq '' when rendering.
    $isNoise = ($t -eq 'user' -and ($o.isMeta -eq $true -or $null -ne $o.toolUseResult))

    $text = $null
    $images = @()
    if (-not $isNoise) {
        $text = Get-TurnText $o $IncludeThinking
        if ($t -eq 'user') {
            $images = Get-TurnImages $o
            if ($null -ne $text) { $text = Format-UserText $text }
            if (($null -eq $text -or $text -eq '') -and $images.Count -eq 0) { $text = $null }
            elseif ($null -eq $text) { $text = '' }
        } else {
            if ($null -ne $text) { $text = $text.Trim(); if ($text -eq '') { $text = $null } }
        }
    }

    $who = ''
    if ($null -ne $text) { $who = if ($t -eq 'user') { 'You' } else { 'Claude' } }
    if ($who -eq '' -and $tokens -eq 0) { return $null }

    return [pscustomobject]@{
        Who    = $who
        When   = $when
        Ts     = $when.ToString('yyyy-MM-dd HH:mm:ss')
        Text   = $(if ($null -eq $text) { '' } else { $text })
        Images = $images
        Tokens = $tokens
    }
}

<#
.SYNOPSIS
  Parse the transcripts that changed since the checkpoint, returning only turns after it.

.DESCRIPTION
  This is the whole point of the rewrite. A transcript whose recorded size still matches is not
  opened at all, so a capture reads the handful of files the current sitting touched instead of
  every transcript in the project folder.

  Returns @{ Turns = <turn[]>; Files = <name/size pairs for EVERY transcript present now> }.
  The Files list becomes the next checkpoint, so files deleted by transcript cleanup drop out of
  the checkpoint naturally and never resurrect.
#>
function Get-NewTurns {
    param(
        [string]$ProjectDir,
        [hashtable]$KnownSizes = @{},   # name -> size at last capture
        [datetime]$Since = [datetime]::MinValue,
        [bool]$IncludeThinking = $false,
        [switch]$Quiet
    )
    $files = @(Get-ChildItem -Path $ProjectDir -Filter *.jsonl -ErrorAction SilentlyContinue)
    $turns = New-Object System.Collections.Generic.List[object]
    $sizes = New-Object System.Collections.Generic.List[object]
    $scanned = 0
    $skipped = 0

    foreach ($f in $files) {
        $sizes.Add([pscustomobject]@{ name = $f.Name; size = $f.Length })
        if ($KnownSizes.ContainsKey($f.Name) -and [int64]$KnownSizes[$f.Name] -eq [int64]$f.Length) {
            $skipped++
            continue    # byte-identical since the last capture => nothing new in it
        }
        # A transcript last written BEFORE the cutoff cannot hold a turn after it. This is what makes
        # -Amend cheap (no recorded sizes to compare against) rather than a full 500 MB rescan.
        if ($Since -gt [datetime]::MinValue -and $f.LastWriteTime -lt $Since) {
            $skipped++
            continue
        }
        $text = Read-Shared $f.FullName
        if ($null -eq $text) { continue }
        $scanned++
        foreach ($line in ($text -split "`n")) {
            $turn = ConvertFrom-Line $line $IncludeThinking
            if ($turn -and $turn.When -gt $Since) { $turns.Add($turn) }
        }
    }
    if (-not $Quiet) {
        Write-Host ("Transcripts: {0} scanned, {1} skipped (unchanged since checkpoint)" -f $scanned, $skipped) -ForegroundColor DarkGray
    }
    return @{ Turns = @($turns | Sort-Object When); Files = $sizes.ToArray() }
}

# ---------------------------------------------------------------- session HTML

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

# Filesystem-safe slug from a session's first user prompt (max ~8 words).
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

function Format-Duration([timespan]$d) {
    if ($d.TotalHours -ge 1) { return "{0:0}h {1:0}m" -f [math]::Floor($d.TotalHours), $d.Minutes }
    return "{0:0}m" -f $d.TotalMinutes
}

function Build-SessionHtml([object[]]$turns, [string]$Title, [datetime]$Start, [datetime]$End) {
    $durStr   = Format-Duration ($End - $Start)
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

# ---------------------------------------------------------------- manifest

function Get-ManifestPath([string]$OutDir) { return (Join-Path $OutDir 'sessions.json') }

function New-Manifest {
    return [pscustomobject]@{
        version    = 1
        updated    = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        checkpoint = [pscustomobject]@{ through = $null; files = @() }
        sessions   = @()
    }
}

function Read-Manifest([string]$OutDir) {
    $path = Get-ManifestPath $OutDir
    if (-not (Test-Path $path)) { return New-Manifest }
    # ReadAllText honours the BOM; Get-Content -Raw would mis-decode BOM-less UTF-8 as ANSI.
    $json = [System.IO.File]::ReadAllText($path)
    if ([string]::IsNullOrWhiteSpace($json)) { return New-Manifest }
    $m = $json | ConvertFrom-Json
    if ($null -eq $m.sessions)   { $m | Add-Member -NotePropertyName sessions   -NotePropertyValue @() -Force }
    if ($null -eq $m.checkpoint) { $m | Add-Member -NotePropertyName checkpoint -NotePropertyValue ([pscustomobject]@{ through = $null; files = @() }) -Force }
    $m.sessions = @($m.sessions)
    return $m
}

function Write-Manifest([string]$OutDir, $Manifest, [string]$MirrorPath = $null) {
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
    $Manifest.updated = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $Manifest.sessions = @($Manifest.sessions | Sort-Object { [datetime]$_.start })
    $path = Get-ManifestPath $OutDir
    $json = $Manifest | ConvertTo-Json -Depth 8
    # Keep a single rolling backup: the manifest is the durable record once transcripts age out.
    if (Test-Path $path) { Copy-Item $path "$path.bak" -Force }
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($true)))
    # ...and a mirror inside the repo, so the one irreplaceable file is versioned and off this disk.
    # OutDir lives under Downloads, which is backed up by nothing.
    if ($MirrorPath) { [System.IO.File]::WriteAllText($MirrorPath, $json, (New-Object System.Text.UTF8Encoding($true))) }
}

<#
.SYNOPSIS
  Commit the in-repo manifest mirror. Path-scoped, so it can never sweep up unrelated work.

.DESCRIPTION
  Deliberately does NOT push: the capture is the last thing before /clear, and the wrap-up's own
  push-all (step 3) carries this commit at the START of the next session - by the time the next
  capture's verify-pushed gate runs, the tree is clean and in sync again.
#>
function Publish-ManifestMirror {
    param([string]$RepoDir, [string]$MirrorPath, [string]$SessionName)
    if (-not $MirrorPath -or -not (Test-Path $MirrorPath)) { return }
    try {
        $status = & git -C $RepoDir status --porcelain -- $MirrorPath 2>$null
        if (-not $status) { Write-Host "  mirror: unchanged, nothing to commit" -ForegroundColor DarkGray; return }
        # Stage first: on the very first run the mirror is untracked, and a path-scoped commit
        # silently matches nothing for a file git has never seen.
        & git -C $RepoDir add -- $MirrorPath 2>&1 | Out-Null
        $msg = "chore: session log $SessionName [skip release]"
        & git -C $RepoDir commit -m $msg -- $MirrorPath 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $sha = (& git -C $RepoDir rev-parse --short HEAD 2>$null)
            Write-Host ("  mirror: committed {0} (push happens with the next wrap-up)" -f $sha) -ForegroundColor DarkGray
        } else {
            Write-Host "  mirror: written but NOT committed - commit tools\effort\sessions.json by hand" -ForegroundColor Yellow
        }
    } catch {
        Write-Host ("  mirror: commit failed ({0}) - the file is written, commit it by hand" -f $_.Exception.Message) -ForegroundColor Yellow
    }
}

function Get-CheckpointSizes($Manifest) {
    $h = @{}
    if ($Manifest.checkpoint -and $Manifest.checkpoint.files) {
        foreach ($f in $Manifest.checkpoint.files) { $h[[string]$f.name] = [int64]$f.size }
    }
    return $h
}

function Get-CheckpointThrough($Manifest) {
    if ($Manifest.checkpoint -and $Manifest.checkpoint.through) {
        try { return [datetime]$Manifest.checkpoint.through } catch { }
    }
    return [datetime]::MinValue
}

function New-SessionRecord {
    param(
        [datetime]$Start, [datetime]$End, [string]$Name, [int]$Turns,
        [int64]$Tokens = 0, [double]$KbdMin = 0, [bool]$HasKbd = $false,
        [string[]]$Toc = @(), [string]$Release = $null, [string]$Source = 'boundary'
    )
    return [pscustomobject]@{
        start   = $Start.ToString('yyyy-MM-dd HH:mm:ss')
        end     = $End.ToString('yyyy-MM-dd HH:mm:ss')
        name    = $Name
        turns   = $Turns
        tokens  = $Tokens
        kbdMin  = [math]::Round($KbdMin, 1)
        hasKbd  = $HasKbd
        toc     = @($Toc)
        release = $Release
        source  = $Source
    }
}

# ---------------------------------------------------------------- per-session metrics

function Get-KbdMinutes {
    param([datetime]$Start, [datetime]$End, [object[]]$EffortRows)
    $total = 0.0
    foreach ($row in $EffortRows) {
        $ovStart = if ($row.Start -gt $Start) { $row.Start } else { $Start }
        $ovEnd   = if ($row.End   -lt $End)   { $row.End }   else { $End }
        if ($ovEnd -gt $ovStart) { $total += ($ovEnd - $ovStart).TotalMinutes }
    }
    return $total
}

function Import-EffortRows([string]$EffortCsv) {
    if (-not (Test-Path $EffortCsv)) { return @() }
    return @(Import-Csv $EffortCsv | ForEach-Object {
        [pscustomobject]@{ Start = [datetime]$_.start; End = [datetime]$_.end }
    })
}

function Import-TocNumbers([string]$OverviewHtml) {
    $set = New-Object System.Collections.Generic.HashSet[string]
    if (Test-Path $OverviewHtml) {
        $ovText = [System.IO.File]::ReadAllText((Resolve-Path $OverviewHtml))
        foreach ($m in [regex]::Matches($ovText, 'id="pr(\d+)"')) { [void]$set.Add($m.Groups[1].Value) }
    }
    return $set
}

# Best-effort text scan: "#NNN" where NNN is an entry id that exists in Overview.html right now.
function Get-TocHits([object[]]$Turns, $ValidTocNumbers) {
    $hits = New-Object System.Collections.Generic.HashSet[string]
    foreach ($t in $Turns) {
        if (-not $t.Text) { continue }
        foreach ($m in [regex]::Matches($t.Text, '#(\d{1,4})\b')) {
            if ($ValidTocNumbers.Contains($m.Groups[1].Value)) { [void]$hits.Add($m.Groups[1].Value) }
        }
    }
    return @($hits | Sort-Object { [int]$_ })
}

# Best-effort timing correlation: a release published inside the session window.
function Get-ReleaseTimes([string]$Repo, [string]$GhExe) {
    if (-not $Repo -or -not (Test-Path $GhExe)) { return @() }
    try {
        $json = & $GhExe api "repos/$Repo/releases" --paginate 2>$null
        $rel = $json | ConvertFrom-Json
        return @($rel | ForEach-Object { [pscustomobject]@{ Tag = $_.tag_name; When = ([datetime]$_.published_at).ToLocalTime() } })
    } catch {
        Write-Host "Could not fetch releases for $Repo - Release column will be blank." -ForegroundColor Yellow
        return @()
    }
}

function Get-ReleaseHit([datetime]$Start, [datetime]$End, [object[]]$ReleaseTimes) {
    $hit = $ReleaseTimes | Where-Object { $_.When -ge $Start -and $_.When -le $End } | Select-Object -First 1
    if ($hit) { return $hit.Tag }
    return $null
}

# ---------------------------------------------------------------- index.html (pure manifest render)

function Format-Tokens([int64]$n) {
    if ($n -ge 1000000) { return "{0:N1}M" -f ($n/1000000) }
    if ($n -ge 1000)    { return "{0:N1}k" -f ($n/1000) }
    return "$n"
}

function Get-RoundedMinute([datetime]$dt) {
    if ($dt.Second -ge 30) { return $dt.AddSeconds(60 - $dt.Second) }
    return $dt.AddSeconds(-$dt.Second)
}

function Build-IndexHtml($Manifest) {
    function Esc([string]$s) { if ($null -eq $s) { return '' }; return $s -replace '&','&amp;' -replace '<','&lt;' -replace '>','&gt;' }
    $dash = '<span class="muted">-</span>'

    $rows = @($Manifest.sessions | Sort-Object { [datetime]$_.start } -Descending)

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
<header><h1>ioSender working sessions</h1><div class="sub">$($rows.Count) sessions &middot; sessions are cut where the end-of-session capture ran (tools\effort\convo-sessions.ps1), not by an idle-time guess &middot; this page is a pure render of sessions.json - rebuild it any time with tools\effort\build-session-index.ps1 &middot; Tokens = raw sum of every underlying API call's input+output+cache (a rough proxy, not a billing figure - run /cost in Claude Code for the exact number) &middot; Kbd time / TOC# / Release are all best-effort (blank = not available or not detected, not necessarily zero)</div></header>
<main><div class="tablewrap"><table>
<colgroup>
  <col style="width:240px"><col style="width:80px"><col style="width:80px"><col style="width:60px">
  <col style="width:70px"><col style="width:320px"><col style="width:70px"><col style="width:340px">
</colgroup>
<tr><th>Time Period</th><th>Elapsed</th><th>Kbd time</th><th>Turns</th><th>Tokens</th><th>TOC #</th><th>Release</th><th>Conversation</th></tr>
"@)

    $totElapsedMin = 0.0; $totKbdMin = 0.0; $totTurns = 0; $totTokens = [int64]0
    $distinctToc = New-Object System.Collections.Generic.HashSet[string]
    $relTags = New-Object System.Collections.Generic.HashSet[string]
    $sessionsWithKbd = 0

    foreach ($r in $rows) {
        $start = [datetime]$r.start
        $end   = [datetime]$r.end
        $dur   = $end - $start
        $totElapsedMin += $dur.TotalMinutes
        $totTurns += [int]$r.turns
        $totTokens += [int64]$r.tokens
        if ($r.hasKbd) { $sessionsWithKbd++; $totKbdMin += [double]$r.kbdMin }
        foreach ($n in @($r.toc)) { [void]$distinctToc.Add([string]$n) }
        if ($r.release) { [void]$relTags.Add([string]$r.release) }

        $rStart = Get-RoundedMinute $start
        $rEnd   = Get-RoundedMinute $end
        $periodStr = "{0} {1} - {2}" -f $rStart.ToString('yyyy-MM-dd'), $rStart.ToString('h:mmtt').ToLower(), $rEnd.ToString('h:mmtt').ToLower()

        $kbdStr = $dash
        if ($r.hasKbd) {
            $k = [double]$r.kbdMin
            $kbdStr = if ($k -ge 60) { "{0:0}h {1:0}m" -f [math]::Floor($k/60), ($k % 60) } else { "{0:0}m" -f $k }
        }
        $tocArr = @($r.toc)
        $tocStr = if ($tocArr.Count -gt 0) { ($tocArr | ForEach-Object { "<span class=`"toc-chip`">#$_</span>" }) -join '' } else { $dash }
        $relStr = if ($r.release) { "<span class=`"yes`">$(Esc $r.release)</span>" } else { $dash }
        $tokStr = if ([int64]$r.tokens -gt 0) { Format-Tokens ([int64]$r.tokens) } else { $dash }
        $link   = "sessions/$($r.name).html"
        $title  = Esc ($r.name -replace '^\d{4}-\d{2}-\d{2}_\d{4}_','' -replace '-',' ')

        [void]$sb.Append("<tr>")
        [void]$sb.Append("<td>$periodStr</td>")
        [void]$sb.Append("<td>$(Format-Duration $dur)</td>")
        [void]$sb.Append("<td>$kbdStr</td>")
        [void]$sb.Append("<td>$($r.turns)</td>")
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
    $kbdCoverageNote = if ($rows.Count -gt 0) { " ({0} of {1} sessions)" -f $sessionsWithKbd, $rows.Count } else { "" }

    [void]$sb.Append("<tr class=`"tot`">")
    [void]$sb.Append("<td>Totals ($($rows.Count) sessions)</td>")
    [void]$sb.Append("<td>$(Format-Mins $totElapsedMin)</td>")
    [void]$sb.Append("<td>$(Format-Mins $totKbdMin)$kbdCoverageNote</td>")
    [void]$sb.Append("<td>$totTurns</td>")
    [void]$sb.Append("<td>$(Format-Tokens $totTokens)</td>")
    [void]$sb.Append("<td>$($distinctToc.Count) distinct</td>")
    [void]$sb.Append("<td>$($relTags.Count)</td>")
    [void]$sb.Append("<td></td>")
    [void]$sb.Append("</tr>`n")
    [void]$sb.Append("</table></div></main></body></html>`n")
    return $sb.ToString()
}

function Write-IndexHtml([string]$OutDir, $Manifest) {
    $html = Build-IndexHtml $Manifest
    $out = Join-Path $OutDir 'index.html'
    [System.IO.File]::WriteAllText($out, $html, (New-Object System.Text.UTF8Encoding($true)))
    return $out
}
