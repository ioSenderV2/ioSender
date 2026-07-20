<#
.SYNOPSIS
  Compute the next release version + changelog delta for the rolling-release workflow.
  Playbook: .github/workflows/release.yml (only caller - not meant to be run by hand,
  though it's safe to dry-run locally with -DryRun).

.DESCRIPTION
  Every push to master is a release. This script:
    1. Looks up the previous published release (plain REST GET - releases/latest is public
       on a public repo, no auth needed, which also sidesteps a gh-CLI-specific "Bad
       credentials" 401 seen in CI even with a token that ncipollo/release-action accepts
       fine for the actual publish) to find its version and the "changelog-through:N"
       marker embedded (as an HTML comment) in its body.
    2. Parses Overview.html's at-a-glance tables for every #N entry (description, tag),
       and picks out the ones numbered higher than the previous release's marker.
    3. Computes the next version (2.1, 2.2, ... - seeds at 2.1 if there's no prior release).
    4. Writes release notes (Markdown) listing the new entries, ending with the new
       "changelog-through:N" marker for the *next* run to read back.
    5. Stamps the TOC "ver" column in Overview.html for those newly-included entries.
  Does not commit/push - the workflow does that as a separate step so it can also embed
  BuildInfo.cs first.

.PARAMETER Repo
  GitHub "owner/repo" to look up the previous release against.

.PARAMETER Html
  Path to Overview.html (repo root by default).

.PARAMETER NotesOut
  Path to write the release-notes Markdown to.

.PARAMETER DryRun
  Skip the gh api lookup (assume no previous release / previousThrough=0) and skip
  writing to Overview.html - just print what would happen. For local testing.
#>
[CmdletBinding()]
param(
    [string]$Repo = 'ioSenderV2/ioSender',
    [string]$Html = 'Overview.html',
    [string]$NotesOut = 'release-notes.md',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Fail($m) { Write-Host "ERROR: $m" -ForegroundColor Red; exit 1 }

# --- 1. Previous release: version + changelog-through marker ---------------
$prevVersion = $null
$previousThrough = 0
if (-not $DryRun) {
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    try {
        $prev = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ 'User-Agent' = 'ioSender-cut-release' }
        $tag = $prev.tag_name -replace '^v', ''
        # Only trust it as a real version if it's "major.minor" - guards against the old
        # unversioned rolling "latest" release (tag literally "latest") still being the
        # newest release the very first time this runs.
        if ($tag -match '^\d+\.\d+$') {
            $prevVersion = $tag
            $m = [regex]::Match($prev.body, 'changelog-through:(\d+)')
            if ($m.Success) { $previousThrough = [int]$m.Groups[1].Value }
        } else {
            Write-Host "Previous release tag '$($prev.tag_name)' isn't a version (probably the old rolling 'latest') - treating as no previous release." -ForegroundColor Yellow
        }
    } catch {
        # A 404 (no releases yet) is an expected outcome, not a bug - the very first run hits this.
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) {
            Write-Host "No previous release found (expected on the very first run)." -ForegroundColor Yellow
        } else {
            Write-Host "WARN: releases/latest lookup failed unexpectedly: $($_.Exception.Message) - treating as no previous release." -ForegroundColor Yellow
        }
    }
}

# --- 2. Next version ---------------------------------------------------------
if ($prevVersion) {
    $parts = $prevVersion.Split('.')
    $newVersion = "$($parts[0]).$([int]$parts[1] + 1)"
} else {
    $newVersion = '2.1'   # seed - first versioned release
}

# --- 3. Parse Overview.html: TOC rows are the authoritative "which entries exist" list -----
# (every entry gets a TOC row; a handful have no at-a-glance summary row, e.g. #38-40 -
# those still need to be picked up for TOC version-stamping even without a description).
$content = [System.IO.File]::ReadAllText($Html)
$tocRe = '<tr><td class="n">(\d+)</td><td><a href="#pr\1">(.*?)</a></td>'
$entries = @{}
foreach ($m in [regex]::Matches($content, $tocRe)) {
    $n = [int]$m.Groups[1].Value
    $entries[$n] = [PSCustomObject]@{ N = $n; Tag = 'CHG'; Desc = $m.Groups[2].Value }
}
if ($entries.Count -eq 0) { Fail "no TOC rows found in $Html - wrong file or format changed?" }
$currentMax = ($entries.Keys | Measure-Object -Maximum).Maximum

# Enrich with the at-a-glance row's tag (NEW/CHG/FIX) and fuller description, where one exists.
$glanceRe = '<tr><td>(.*?)</td><td class="tag"><span class="t-(\w+)">\w+</span></td><td class="prs"><a href="#pr(\d+)">\3</a></td></tr>'
foreach ($m in [regex]::Matches($content, $glanceRe)) {
    $n = [int]$m.Groups[3].Value
    if ($entries.ContainsKey($n)) {
        $entries[$n].Tag = $m.Groups[2].Value.ToUpper()
        $entries[$n].Desc = $m.Groups[1].Value
    }
}

$newEntries = $entries.Values | Where-Object { $_.N -gt $previousThrough } | Sort-Object N

# --- 4. Release notes --------------------------------------------------------
$lines = @("## ioSender $newVersion", "")
if ($newEntries.Count -eq 0) {
    $lines += "No changelog entries in this build (see [Overview.html](https://github.com/$Repo/blob/master/Overview.html#features-and-fixes) for the full history)."
} else {
    foreach ($e in $newEntries) {
        # Strip inner HTML tags for a plain-text release note line.
        $plain = [regex]::Replace($e.Desc, '<[^>]+>', '')
        $lines += "- **[$($e.Tag)] #$($e.N)** $plain"
    }
}
$lines += ""
$lines += "<!-- changelog-through:$currentMax -->"
$notes = ($lines -join "`n")
[System.IO.File]::WriteAllText($NotesOut, $notes)

$prevLabel = if ($prevVersion) { $prevVersion } else { '<none>' }
Write-Host "Version: $newVersion  (previous: $prevLabel, through #$previousThrough -> #$currentMax, $($newEntries.Count) new entries)" -ForegroundColor Green

# --- 5. Stamp the TOC "ver" column for newly-included entries --------------
if (-not $DryRun -and $newEntries.Count -gt 0) {
    foreach ($e in $newEntries) {
        $existingRe = "<tr><td class=""n"">$($e.N)</td>.*?<td class=""ver"">([^<]*)</td></tr>"
        $existing = [regex]::Match($content, $existingRe)
        if (-not $existing.Success) {
            Write-Host "WARN: no TOC row found for #$($e.N)" -ForegroundColor Yellow
            continue
        }
        $already = $existing.Groups[1].Value
        if ($already -eq $newVersion) { continue }          # already correctly stamped - nothing to do
        if ($already -ne '') {
            Write-Host "WARN: #$($e.N) TOC ver cell already says '$already', not overwriting with '$newVersion'" -ForegroundColor Yellow
            continue
        }
        $pattern = "(<tr><td class=""n"">$($e.N)</td>.*?<td class=""ver"">)(</td>)"
        $content = [regex]::Replace($content, $pattern, { param($m) $m.Groups[1].Value + $newVersion + $m.Groups[2].Value }, 1)
    }
    [System.IO.File]::WriteAllText($Html, $content)
}

# --- outputs for the workflow ------------------------------------------------
if ($env:GITHUB_OUTPUT) {
    Add-Content $env:GITHUB_OUTPUT "version=$newVersion"
    Add-Content $env:GITHUB_OUTPUT "notesFile=$NotesOut"
    Add-Content $env:GITHUB_OUTPUT "hasChanges=$(($newEntries.Count -gt 0).ToString().ToLower())"
}
