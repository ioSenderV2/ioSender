<#
.SYNOPSIS
  Guard for the release-notes narrative splice in cut-release.ps1: present, absent and empty.

.DESCRIPTION
  cut-release.ps1 splices docs\release-notes\<version>.md into the release body when that file
  exists. Two things have to hold, and neither is visible until a release is already published:

    1. WITH the file: the narrative appears, exactly once, ahead of the generated list, and the list
       gains its "All changes in this release" heading so it does not read as a continuation of the
       prose.
    2. WITHOUT it: the body is what it has always been. An ordinary minor cut must not start
       carrying an empty section, a stray heading, or the word "null". This is the case that runs on
       every push and the one nobody will be watching.

  An EMPTY or whitespace-only file counts as absent - someone creating the file to fill in later
  must not silently retitle the list and then say nothing under the new heading.

  This runs the REAL cut-release.ps1 with -DryRun against a fixture Overview.html, so it tests the
  shipped code path rather than a restatement of it. -DryRun skips the API lookup (no previous
  release, so every fixture entry is "new") and skips writing to the HTML.

  Companion to test-release-version.ps1, which guards the version decision.

.EXAMPLE
  pwsh -File tools\test-release-notes-splice.ps1
#>

$ErrorActionPreference = 'Stop'

$repo       = Split-Path $PSScriptRoot -Parent
$cutRelease = Join-Path $PSScriptRoot 'cut-release.ps1'

# The real version the cutter will compute here, so the fixture file is named the way a real one is.
# Read from MainWindow the same way cut-release.ps1 does: with no previous release it seeds 2.1, and
# legacyVersion outranks that, so legacyVersion is the version under -DryRun.
$mwPath = Join-Path $repo 'ioSender XL\ioSender XL\MainWindow.xaml.cs'
$m = [regex]::Match([System.IO.File]::ReadAllText($mwPath), 'legacyVersion\s*=\s*"([^"]+)"')
if (-not $m.Success) {
    Write-Host "ERROR: legacyVersion not found in MainWindow.xaml.cs - cannot predict the version under test." -ForegroundColor Red
    exit 1
}
$version = $m.Groups[1].Value

$notesDir  = Join-Path $repo 'docs\release-notes'
$realNotes = Join-Path $notesDir "$version.md"

# A fixture Overview.html with two entries, in the shape cut-release.ps1's own regexes expect.
# Kept in temp: the real Overview.html is 880KB and -DryRun would still parse all 420 entries.
$tmp         = Join-Path ([System.IO.Path]::GetTempPath()) ("relnotes-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$fixtureHtml = Join-Path $tmp 'Overview.html'
$notesOut    = Join-Path $tmp 'release-notes.md'
New-Item -ItemType Directory -Force $tmp | Out-Null

$html = @'
<table>
<tr><td class="n">1</td><td><a href="#pr1">First entry title</a></td><td>x</td></tr>
<tr><td class="n">2</td><td><a href="#pr2">Second entry title</a></td><td>x</td></tr>
</table>
<table>
<tr><td>A real description of the first thing</td><td class="tag"><span class="t-NEW">NEW</span></td><td class="prs"><a href="#pr1">1</a></td></tr>
<tr><td>A real description of the second thing</td><td class="tag"><span class="t-FIX">FIX</span></td><td class="prs"><a href="#pr2">2</a></td></tr>
</table>
'@
[System.IO.File]::WriteAllText($fixtureHtml, $html)

# The real notes file is MOVED aside, never deleted - this test must not be able to destroy the
# narrative someone spent a session writing, even if it throws halfway through.
$stashed = $null
if (Test-Path $realNotes) {
    $stashed = Join-Path $tmp 'stashed-real-notes.md'
    Move-Item $realNotes $stashed
}

function Invoke-Cutter {
    # -DryRun: no API lookup, no write to the fixture HTML. Output is discarded; the file is the test.
    & pwsh -NoProfile -File $cutRelease -Html $fixtureHtml -NotesOut $notesOut -DryRun *> $null
    return [System.IO.File]::ReadAllText($notesOut)
}

$fail = 0
function Check($name, $cond, $detail) {
    if ($cond) {
        Write-Host ("  PASS  {0}" -f $name) -ForegroundColor DarkGray
    } else {
        Write-Host ("  FAIL  {0}" -f $name) -ForegroundColor Red
        Write-Host ("        {0}" -f $detail) -ForegroundColor DarkGray
        $script:fail++
    }
}

try {
    Write-Host ""
    Write-Host "release-notes splice (version under test: $version)" -ForegroundColor Cyan
    Write-Host ""

    # --- 1. ABSENT: the case that runs on every ordinary push ---------------------------------
    New-Item -ItemType Directory -Force $notesDir | Out-Null
    $absent = Invoke-Cutter

    Write-Host "no narrative file:" -ForegroundColor Yellow
    Check 'the entries are still listed' `
        ($absent -match '\*\*\[NEW\] #1\*\* A real description of the first thing') `
        'the generated list is missing or malformed'
    Check 'no "All changes" heading appears' `
        ($absent -notmatch 'All changes in this release') `
        'the heading leaked into a body with no narrative above it to need naming'
    # Adjacency, not absence-of-pattern. The first version of this check asserted "no blank line
    # followed by ###", whose alternation had an empty branch, so it reduced to "a blank line before
    # any heading" - which the install heading satisfies in a perfectly correct body. It failed on
    # the real output and the real output was right. Assert the shape instead: the install block's
    # closing fence, ONE blank line, then the first bullet, with nothing whatsoever between them.
    Check 'nothing inserted between the install block and the list' `
        ($absent -match '(?m)^```\r?\n\r?\n- \*\*\[NEW\] #1\*\*') `
        'something (an empty section, a stray heading, a literal null) was emitted where the narrative would go'
    Check 'the changelog-through marker is present' `
        ($absent -match 'changelog-through:2') `
        'the marker the NEXT run reads back is missing'

    # --- 2. PRESENT ---------------------------------------------------------------------------
    $narrative = "## What's new in $version`n`nA sentence that says what this release means.`n`n### A themed heading`n`nMore prose."
    [System.IO.File]::WriteAllText($realNotes, $narrative)
    $present = Invoke-Cutter

    Write-Host ""
    Write-Host "with a narrative file:" -ForegroundColor Yellow
    Check 'the narrative appears' `
        ($present -match 'A sentence that says what this release means\.') `
        'the narrative was not spliced in'
    Check 'it appears exactly ONCE' `
        (([regex]::Matches($present, 'A sentence that says what this release means')).Count -eq 1) `
        'spliced more than once - a reader sees the release stutter'
    Check 'the list gains its heading' `
        ($present -match 'All changes in this release') `
        'the list reads as a continuation of the prose'
    Check 'the narrative comes BEFORE the list' `
        ($present.IndexOf('A sentence that says') -lt $present.IndexOf('**[NEW] #1**')) `
        'the narrative landed after the 203 bullets, where nobody will read it'
    Check 'the install block still comes first' `
        ($present.IndexOf('### Install / update') -lt $present.IndexOf('A sentence that says')) `
        'the actionable part got pushed below the prose'
    Check 'the entries are still all listed' `
        (($present -match '#1\*\* A real description') -and ($present -match '#2\*\* A real description')) `
        'splicing the narrative dropped entries from the list'
    Check 'the marker survives the splice' `
        ($present -match 'changelog-through:2') `
        'the marker was lost, so the NEXT release would re-list everything'

    # --- 3. EMPTY counts as absent ------------------------------------------------------------
    [System.IO.File]::WriteAllText($realNotes, "   `n`n  `n")
    $empty = Invoke-Cutter

    Write-Host ""
    Write-Host "with an empty narrative file:" -ForegroundColor Yellow
    Check 'treated as absent - no heading' `
        ($empty -notmatch 'All changes in this release') `
        'a whitespace-only file retitled the list and then said nothing under the new heading'
    Check 'the entries are still listed' `
        ($empty -match '\*\*\[NEW\] #1\*\*') `
        'the generated list was lost'
}
finally {
    # Put the real narrative back before anything else, including on a throw.
    if (Test-Path $realNotes) { Remove-Item $realNotes -Force }
    if ($stashed -and (Test-Path $stashed)) { Move-Item $stashed $realNotes -Force }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

Write-Host ""
if ($fail) { Write-Host "$fail check(s) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "OK  all checks passed" -ForegroundColor Green
exit 0
