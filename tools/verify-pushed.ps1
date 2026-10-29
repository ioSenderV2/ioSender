<#
.SYNOPSIS
  Verify the working tree is clean and fully pushed. Read-only (never pushes).
  Playbook: docs/playbooks/capture_conversation_log.md (step 0 - run before capturing/clearing).

.DESCRIPTION
  A safety gate for the end-of-session capture: you don't want to /clear on top of uncommitted or
  unpushed work. It reports every problem it finds, then exits 1 if anything is uncommitted or
  unpushed, 0 if clean and in sync. It suggests the fix rather than doing it.

  REWRITTEN 2026-10-29. It was written for the integration -> v2/master model and could not pass on
  a pr/* branch, which is where all the work has been since PR 20. Measured 2026-10-28 at the end of
  a pr/portable-core beta session: exit 1 with two complaints that were both structural rather than
  real, so the documented gate before capture had to be done by hand instead. Three changes:

    1. The v2 comparison targets v2/<branch> when that ref exists and falls back to v2/master only
       when it does not. It used to compare v2/master..<branch> unconditionally, which reads 838
       commits "not pushed to v2/master" for a branch fully in sync with v2/pr/portable-core.

    2. UNTRACKED FILES NO LONGER FAIL IT - they are reported as a note. It used to use a bare
       `git status --porcelain`, which counts untracked, so this repo can never be clean while web/
       exists, and web/ must never be committed. Tracked modifications still fail; the distinction
       is the whole point, because "I forgot to commit" and "there is a build tree in here on
       purpose" are different facts and only one of them should stop a capture.

    3. A MISSING REMOTE REF IS SAID OUT LOUD, not silently passed. `git rev-list --count
       origin/pr/foo..pr/foo` against a remote with no such branch fails, and [int] of its empty
       output is 0 - which read exactly like "nothing unpushed". origin is the frozen pre-V2 archive
       and has no pr/* branches at all, so on this branch that check was answering a question it had
       never asked. Now it prints which remotes were compared and which were skipped and why: a
       comparison that could not be made is not a pass.

  -Branch defaults to the CURRENT branch rather than to 'integration', so the command in the
  playbook works unchanged on whatever is checked out. Pass it explicitly to assert what you expect
  to be on - that is the one case where "HEAD is not <branch>" is worth failing over.

.EXAMPLE
  tools\verify-pushed.ps1
  tools\verify-pushed.ps1 -Branch integration
#>
[CmdletBinding()]
param(
    # Omitted = whatever is checked out. Given = assert HEAD is on it.
    [string]$Branch = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$problems = @()
$notes = @()

$cur = (git rev-parse --abbrev-ref HEAD).Trim()
if (-not $Branch) {
    $Branch = $cur
} elseif ($cur -ne $Branch) {
    $problems += "HEAD is '$cur', not the asserted '$Branch'."
}

# --- 1. Clean working tree? Tracked changes only. ---------------------------------------------
# Untracked is a NOTE, not a failure - see the remarks. Deliberately-untracked trees (web/,
# simulator binaries, scratch macros) are a standing condition of this repo, not outstanding work.
$dirty = git status --porcelain --untracked-files=no
if ($dirty) {
    $problems += "Tracked changes not committed:`n$dirty"
}

$untracked = git ls-files --others --exclude-standard --directory
if ($untracked) {
    $notes += "untracked (not a blocker, and NOT swept into a commit - check nothing here is owed):`n    " +
              (($untracked | Select-Object -First 12) -join "`n    ") +
              $(if (@($untracked).Count -gt 12) { "`n    ... and $(@($untracked).Count - 12) more" } else { '' })
}

# --- 2. Refresh remote state (best-effort; offline compares against tracking refs). -----------
foreach ($r in (git remote)) {
    try { git fetch --quiet $r 2>$null } catch { $notes += "could not fetch $r (offline?) - counts are against stale tracking refs" }
}

# --- 3. Anything unpushed? ---------------------------------------------------------------------
# One row per remote, each saying which ref it compared against - or that it could not compare.
function Test-Ref($ref) {
    git rev-parse --verify --quiet "$ref" *> $null
    return $LASTEXITCODE -eq 0
}

$checked = @()
foreach ($r in (git remote)) {
    # Prefer the same-named branch on that remote. v2/master is a deliberate fallback only: it
    # tracks integration as its master, so on an integration session that IS the right target.
    $ref = $null
    if (Test-Ref "$r/$Branch") {
        $ref = "$r/$Branch"
    } elseif ($Branch -eq 'integration' -and (Test-Ref "$r/master")) {
        $ref = "$r/master"
    }

    if (-not $ref) {
        $notes += "$r has no '$Branch' (and no master fallback for this branch) - NOT CHECKED, so this says nothing about $r"
        continue
    }

    $ahead = [int](git rev-list --count "$ref..$Branch")
    if ($ahead -gt 0) { $problems += "$ahead commit(s) not pushed to $ref." }
    else { $checked += $ref }
}

if ($checked.Count -eq 0 -and $problems.Count -eq 0) {
    $problems += "no remote could be compared - nothing here proves the work is pushed."
}

# --- 4. Report ---------------------------------------------------------------------------------
Write-Host "Local $Branch is at:" -ForegroundColor Cyan
git log -1 --format='  %h %s' $Branch

foreach ($n in $notes) { Write-Host "note: $n" -ForegroundColor DarkYellow }

if ($problems.Count -gt 0) {
    Write-Host "`nNOT ready to capture/clear:" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Red }
    $fix = if ($Branch -like 'pr/*') { "commit outstanding changes, then git push v2 $Branch" }
           else { "commit outstanding changes, then run tools\push-all.ps1" }
    Write-Host "`nFix: $fix" -ForegroundColor Yellow
    exit 1
}

$head = (git rev-parse $Branch).Trim().Substring(0, 7)
Write-Host "`nOK  clean + pushed: $($checked -join ' and ') @ $head" -ForegroundColor Green
Write-Host "Ready to capture the session." -ForegroundColor Green
exit 0
