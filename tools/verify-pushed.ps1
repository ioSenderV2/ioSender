<#
.SYNOPSIS
  Verify the working tree is clean and fully pushed to BOTH remotes. Read-only (never pushes).
  Playbook: docs/playbooks/capture_conversation_log.md (step 0 - run before capturing/clearing).

.DESCRIPTION
  A safety gate for the end-of-session capture: you don't want to /clear on top of uncommitted or
  unpushed work. Mirrors push-all.ps1's remote model (origin/<branch> AND v2/master, which tracks
  integration as its master) but only CHECKS - it commits and pushes nothing.

  Fetches both remotes (quiet, best-effort) so the ahead-counts reflect true remote state, not stale
  tracking refs. Reports every problem it finds, then exits 1 if anything is uncommitted/unpushed,
  0 if clean and in sync. Suggests the fix (commit / tools\push-all.ps1) rather than doing it.

.EXAMPLE
  tools\verify-pushed.ps1
  tools\verify-pushed.ps1 -Branch integration
#>
[CmdletBinding()]
param(
    [string]$Branch = 'integration'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$problems = @()

# 1. On the expected branch?
$cur = (git rev-parse --abbrev-ref HEAD).Trim()
if ($cur -ne $Branch) {
    $problems += "HEAD is '$cur', not '$Branch' (pass -Branch $cur if that's intended)."
}

# 2. Clean working tree?
$dirty = git status --porcelain
if ($dirty) {
    $problems += "Working tree not clean - commit or stash first:`n$dirty"
}

# 3. Refresh remote state (best-effort; offline just means we compare against tracking refs).
try { git fetch --quiet origin 2>$null } catch { Write-Host "warn: could not fetch origin (offline?)" -ForegroundColor Yellow }
try { git fetch --quiet v2 2>$null }     catch { Write-Host "warn: could not fetch v2 (offline?)" -ForegroundColor Yellow }

# 4. Anything unpushed to either remote?
$originAhead = [int]((git rev-list --count "origin/$Branch..$Branch" 2>$null))
if ($originAhead -gt 0) { $problems += "$originAhead commit(s) not pushed to origin/$Branch." }

$v2Ahead = [int]((git rev-list --count "v2/master..$Branch" 2>$null))
if ($v2Ahead -gt 0) { $problems += "$v2Ahead commit(s) not pushed to v2/master." }

Write-Host "Local $Branch is at:" -ForegroundColor Cyan
git log -1 --format='  %h %s' $Branch

if ($problems.Count -gt 0) {
    Write-Host "`nNOT ready to capture/clear:" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Red }
    Write-Host "`nFix: commit outstanding changes, then run tools\push-all.ps1" -ForegroundColor Yellow
    exit 1
}

$head = (git rev-parse $Branch).Trim().Substring(0, 7)
Write-Host "`nOK  clean + pushed: origin/$Branch and v2/master both @ $head" -ForegroundColor Green
Write-Host "Ready to capture the session." -ForegroundColor Green
exit 0
