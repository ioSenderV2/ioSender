<#
.SYNOPSIS
  Check that a release narrative's stated counts still match what the cutter will actually publish.

.DESCRIPTION
  docs\release-notes\<version>.md opens by stating how many changes the release carries and how they
  split across NEW/FIX/CHG. Those numbers were true the day they were written and go wrong silently
  the moment another changelog entry lands - and they go wrong on the one page where a reader has no
  way to tell. The release would say 203 changes above a list of 207.

  Nothing else notices: the narrative is prose, the list is generated, and they are only brought
  together inside the release body at cut time, on a CI runner, after the push.

  So this compares the two. It recomputes the delta exactly the way cut-release.ps1 does - the same
  TOC and at-a-glance regexes over Overview.html, against the changelog-through marker on the
  currently published release - and fails with the corrected sentence if they disagree.

  A narrative that states no counts passes: there is nothing to go stale.

  Run it before the release push, and after adding any changelog entry while a narrative is pending.

.PARAMETER Through
  Override the changelog-through marker instead of reading it from the published release. For
  offline use; the lookup is a public unauthenticated GET otherwise.

.EXAMPLE
  pwsh -File tools\check-release-notes-counts.ps1
  pwsh -File tools\check-release-notes-counts.ps1 -Through 217
#>
[CmdletBinding()]
param(
    [string]$Repo = 'ioSenderV2/ioSender',
    [string]$Html = '',
    [int]$Through = -1
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Html) { $Html = Join-Path $repoRoot 'Overview.html' }

# --- which version, the same way cut-release.ps1 decides it under a declared bump --------------
$mwPath = Join-Path $repoRoot 'ioSender XL\ioSender XL\MainWindow.xaml.cs'
$m = [regex]::Match([System.IO.File]::ReadAllText($mwPath), 'legacyVersion\s*=\s*"([^"]+)"')
if (-not $m.Success) { Write-Host "ERROR: legacyVersion not found in MainWindow.xaml.cs" -ForegroundColor Red; exit 1 }
$version = $m.Groups[1].Value

$notesPath = Join-Path $repoRoot "docs\release-notes\$version.md"
if (-not (Test-Path $notesPath)) {
    Write-Host "No docs\release-notes\$version.md - nothing to check." -ForegroundColor DarkGray
    exit 0
}
$notes = [System.IO.File]::ReadAllText($notesPath)

# --- the marker on the published release -------------------------------------------------------
if ($Through -lt 0) {
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    try {
        $prev = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" `
                                  -Headers @{ 'User-Agent' = 'ioSender-check-notes' }
        $mk = [regex]::Match($prev.body, 'changelog-through:(\d+)')
        if (-not $mk.Success) { throw "no changelog-through marker in $($prev.tag_name)'s body" }
        $Through = [int]$mk.Groups[1].Value
        Write-Host "Published release $($prev.tag_name) carries changelog-through:$Through" -ForegroundColor DarkGray
    } catch {
        # Do NOT fall back to a guess: a wrong marker silently produces a wrong "expected" count,
        # which is the exact failure this script exists to catch, now wearing the check's authority.
        Write-Host "ERROR: could not read the published release's marker - $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "       Pass -Through <N> to check offline." -ForegroundColor Yellow
        exit 1
    }
}

# --- recompute the delta, with cut-release.ps1's own regexes -----------------------------------
$content = [System.IO.File]::ReadAllText($Html)
$entries = @{}
foreach ($x in [regex]::Matches($content, '<tr><td class="n">(\d+)</td><td><a href="#pr\1">(.*?)</a></td>')) {
    $entries[[int]$x.Groups[1].Value] = 'CHG'
}
foreach ($x in [regex]::Matches($content, '<tr><td>(.*?)</td><td class="tag"><span class="t-(\w+)">\w+</span></td><td class="prs"><a href="#pr(\d+)">\3</a></td></tr>')) {
    $n = [int]$x.Groups[3].Value
    if ($entries.ContainsKey($n)) { $entries[$n] = $x.Groups[2].Value.ToUpper() }
}
$new = @($entries.GetEnumerator() | Where-Object { $_.Key -gt $Through })
$total = $new.Count
$byTag = @{ NEW = 0; FIX = 0; CHG = 0 }
foreach ($e in $new) { $byTag[$e.Value]++ }

Write-Host ("Overview.html beyond #{0}: {1} entries - {2} NEW, {3} FIX, {4} CHG" -f `
            $Through, $total, $byTag.NEW, $byTag.FIX, $byTag.CHG) -ForegroundColor DarkGray

# --- what the narrative claims -----------------------------------------------------------------
# Deliberately tolerant of the sentence's wording and of thousands separators - it is prose and will
# be reworded. If no claim is found at all, say so loudly rather than passing silently: a reworded
# sentence that this stops matching would otherwise turn the check off without anyone noticing.
#
# EVERY GAP BETWEEN WORDS IS \s+, NOT A LITERAL SPACE. The narrative is hard-wrapped at 100 columns,
# so "63 new features" is "63 new\nfeatures" on disk - a pattern with a literal space in "new
# features" matches nothing, and the first version of this check was defeated by exactly that: the
# total compared fine while the NEW/FIX/CHG split was never examined at all. Found only by running
# the failure path on purpose and noticing the FIX mismatch it printed was absent from its own
# verdict. A check that silently examines half of what it claims to is worse than no check.
$claimTotal = [regex]::Match($notes, 'carries\s+([\d,]+)\s+changes')
$claimSplit = [regex]::Match($notes, '([\d,]+)\s+new\s+features,\s*([\d,]+)\s+fixes\s+and\s*([\d,]+)\s+revisions')

if (-not $claimTotal.Success -and -not $claimSplit.Success) {
    Write-Host ""
    Write-Host "WARN: $version.md states no counts this script recognises." -ForegroundColor Yellow
    Write-Host "      Either the narrative genuinely claims no numbers (fine - nothing can go stale)," -ForegroundColor Yellow
    Write-Host "      or the sentence was reworded and this check is now looking at nothing. Read it." -ForegroundColor Yellow
    exit 0
}

# A PARTIAL match is the dangerous state, not a harmless one: the half that still matches reports
# green and carries the whole check's authority, while the half that no longer matches is simply not
# examined. Fail on it rather than warn - the whole point of this script is that nobody is watching
# the page where the stale number lands.
if ($claimTotal.Success -ne $claimSplit.Success) {
    $found   = if ($claimTotal.Success) { 'the total' } else { 'the NEW/FIX/CHG split' }
    $missing = if ($claimTotal.Success) { 'the NEW/FIX/CHG split' } else { 'the total' }
    Write-Host ""
    Write-Host "ERROR: $version.md states $found but this script cannot find $missing." -ForegroundColor Red
    Write-Host "       Half a check reads as a pass. Either restore the wording this matches, or" -ForegroundColor Red
    Write-Host "       drop the remaining claim from the narrative so there is nothing to go stale." -ForegroundColor Red
    exit 1
}

function AsInt($s) { [int]($s -replace ',', '') }
$bad = @()
if ($claimTotal.Success) {
    $c = AsInt $claimTotal.Groups[1].Value
    if ($c -ne $total) { $bad += "total: narrative says $c, Overview.html has $total" }
}
if ($claimSplit.Success) {
    $cn = AsInt $claimSplit.Groups[1].Value
    $cf = AsInt $claimSplit.Groups[2].Value
    $cc = AsInt $claimSplit.Groups[3].Value
    if ($cn -ne $byTag.NEW) { $bad += "NEW: narrative says $cn, Overview.html has $($byTag.NEW)" }
    if ($cf -ne $byTag.FIX) { $bad += "FIX: narrative says $cf, Overview.html has $($byTag.FIX)" }
    if ($cc -ne $byTag.CHG) { $bad += "CHG: narrative says $cc, Overview.html has $($byTag.CHG)" }
}

Write-Host ""
if ($bad.Count -eq 0) {
    Write-Host "OK  $version.md's counts match the $total entries beyond #$Through" -ForegroundColor Green
    exit 0
}
Write-Host "$version.md is out of date with Overview.html:" -ForegroundColor Red
foreach ($b in $bad) { Write-Host "  $b" -ForegroundColor Red }
Write-Host ""
Write-Host "The corrected sentence:" -ForegroundColor Yellow
Write-Host ("  it carries {0} changes - {1} new features, {2} fixes and {3} revisions" -f `
            $total, $byTag.NEW, $byTag.FIX, $byTag.CHG) -ForegroundColor Yellow
exit 1
