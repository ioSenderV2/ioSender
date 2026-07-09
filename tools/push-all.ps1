<#
.SYNOPSIS
  Push the current work all the way to both remotes.
  Playbook: docs/playbooks/end_of_session_wrapup.md (step 3).

.DESCRIPTION
  Pushes the local branch to origin/<branch> AND to v2/master
  (the V2 product remote tracks integration as its master).
  Prints ahead/behind before pushing and verifies both refs land.

.EXAMPLE
  tools\push-all.ps1
  tools\push-all.ps1 -DryRun
  tools\push-all.ps1 -Branch integration
#>
[CmdletBinding()]
param(
    [string]$Branch = 'integration',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

# Confirm we're on (or can push) the named branch.
$cur = (git rev-parse --abbrev-ref HEAD).Trim()
if ($cur -ne $Branch) {
    Fail "HEAD is '$cur', not '$Branch'. Checkout $Branch or pass -Branch $cur."
}

# Refuse to push a dirty tree (nothing worse than pushing half a change set).
$dirty = git status --porcelain
if ($dirty) { Fail "Working tree not clean - commit or stash first:`n$dirty" }

$refspec = $Branch + ':master'

Write-Host "Local $Branch is at:" -ForegroundColor Cyan
git log -1 --format='  %h %s' $Branch

# origin/<branch>
$originAhead = (git rev-list --count "origin/$Branch..$Branch" 2>$null)
Write-Host "origin/$Branch : $originAhead commit(s) to push" -ForegroundColor Cyan

# v2/master
$v2Ahead = (git rev-list --count "v2/master..$Branch" 2>$null)
Write-Host "v2/master      : $v2Ahead commit(s) to push" -ForegroundColor Cyan

if ($DryRun) {
    Write-Host "`n-DryRun: would run" -ForegroundColor Yellow
    Write-Host "  git push origin $Branch"
    Write-Host "  git push v2 $refspec"
    exit 0
}

Write-Host "`nPushing to origin/$Branch ..." -ForegroundColor Green
git push origin $Branch
if ($LASTEXITCODE -ne 0) { Fail "push to origin/$Branch failed" }

Write-Host "`nPushing to v2/master ($refspec) ..." -ForegroundColor Green
git push v2 $refspec
if ($LASTEXITCODE -ne 0) { Fail "push to v2/master failed" }

# Verify both remotes now point at local HEAD.
git fetch --quiet origin v2
$head = (git rev-parse $Branch).Trim()
$originAt = (git rev-parse "origin/$Branch").Trim()
$v2At = (git rev-parse "v2/master").Trim()

Write-Host ""
if ($originAt -eq $head) { Write-Host "OK  origin/$Branch  @ $($head.Substring(0,7))" -ForegroundColor Green }
else { Fail "origin/$Branch is $($originAt.Substring(0,7)), expected $($head.Substring(0,7))" }
if ($v2At -eq $head) { Write-Host "OK  v2/master       @ $($head.Substring(0,7))" -ForegroundColor Green }
else { Fail "v2/master is $($v2At.Substring(0,7)), expected $($head.Substring(0,7))" }

Write-Host "`nPushed all the way to remote." -ForegroundColor Green
