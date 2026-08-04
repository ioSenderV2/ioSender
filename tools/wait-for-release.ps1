<#
.SYNOPSIS
  Wait for the "Rolling release" GitHub Actions run triggered by the just-pushed commit to finish.
  Playbook: docs/playbooks/end_of_session_wrapup.md (step 3.5).

.DESCRIPTION
  push-all.ps1 pushes to v2/master, which triggers .github/workflows/release.yml on
  ioSenderV2/ioSender (push to master). That build can fail independently of anything checked
  locally (build.ps1 uses -restore against local package caches; the CI runner starts clean).
  Polls `gh run list` for a run matching the given commit until it completes, then reports
  success/failure. Exits 0 on success, 1 on failure/timeout - so the wrap-up can gate on it
  before writing the summary/capturing the log, instead of finding out from an email later.

.EXAMPLE
  tools\wait-for-release.ps1
  tools\wait-for-release.ps1 -Sha c28ae84 -TimeoutSeconds 300
#>
[CmdletBinding()]
param(
    [string]$Sha,
    [string]$Repo = 'ioSenderV2/ioSender',
    [string]$Workflow = 'release.yml',
    [int]$TimeoutSeconds = 300,
    [int]$PollSeconds = 15,
    # The release workflow pushes a changelog-stamp commit to master; this script fast-forwards onto it
    # so the next push isn't rejected. -NoPull opts out (e.g. checking a release from another worktree).
    [switch]$NoPull
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Sha) { $Sha = (git -C $repoRoot rev-parse HEAD).Trim() }

Write-Host "Waiting for '$Workflow' on $Repo for commit $($Sha.Substring(0,7)) ..." -ForegroundColor Cyan

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$run = $null
while ((Get-Date) -lt $deadline) {
    $json = & "$PSScriptRoot\gh.ps1" run list --repo $Repo --workflow $Workflow --limit 10 --json 'databaseId,headSha,status,conclusion,url' 2>$null
    if ($LASTEXITCODE -eq 0 -and $json) {
        $runs = $json | ConvertFrom-Json
        $run = $runs | Where-Object { $_.headSha -eq $Sha } | Select-Object -First 1
        if ($run -and $run.status -eq 'completed') { break }
        $run = $null   # not found yet, or still running - keep polling
    }
    Start-Sleep -Seconds $PollSeconds
}

if (-not $run) {
    Write-Host "TIMEOUT: no completed '$Workflow' run found for $($Sha.Substring(0,7)) within ${TimeoutSeconds}s." -ForegroundColor Yellow
    Write-Host "Check manually: https://github.com/$Repo/actions/workflows/$Workflow" -ForegroundColor Yellow
    exit 1
}

if ($run.conclusion -eq 'success') {
    Write-Host "OK  Rolling release succeeded: $($run.url)" -ForegroundColor Green

    # release.yml's last step commits the changelog version stamps and pushes them straight to master,
    # so the moment this run reports success the local branch is one commit behind v2/master. Pull it
    # here rather than leaving it to whoever is following the wrap-up: forgetting means the NEXT push
    # (the legacyVersion bump) is rejected with "fetch first", which is how v2.36 and v2.38 both ended
    # up needing a recovery merge.
    if (-not $NoPull) {
        Write-Host "Pulling the changelog stamp the release just pushed ..." -ForegroundColor Cyan
        & git -C $repoRoot fetch v2 --quiet 2>&1 | Out-Null

        $behind = (& git -C $repoRoot rev-list --count HEAD..v2/master 2>$null)
        if ($LASTEXITCODE -ne 0) {
            Write-Host "WARN  could not read v2/master - pull it by hand before the version bump." -ForegroundColor Yellow
        }
        elseif ([int]$behind -eq 0) {
            Write-Host "OK  already up to date with v2/master (nothing was stamped)." -ForegroundColor Green
        }
        else {
            # --ff-only on purpose: the stamp commit should be the ONLY thing there. If this refuses,
            # something else pushed to master and that deserves a look, not an automatic merge.
            & git -C $repoRoot merge --ff-only v2/master 2>&1 | Write-Host
            if ($LASTEXITCODE -eq 0) {
                Write-Host "OK  fast-forwarded $behind commit(s) from v2/master." -ForegroundColor Green
            }
            else {
                Write-Host "STOP  v2/master has diverged - it is not just the changelog stamp." -ForegroundColor Red
                Write-Host "      Look at 'git log HEAD..v2/master' and merge deliberately (do NOT rebase:" -ForegroundColor Red
                Write-Host "      your commits are already on origin/integration)." -ForegroundColor Red
                exit 1
            }
        }
    }
    exit 0
}
else {
    Write-Host "FAILED  Rolling release '$($run.conclusion)': $($run.url)" -ForegroundColor Red
    exit 1
}
