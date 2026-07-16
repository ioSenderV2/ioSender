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
    [int]$PollSeconds = 15
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
    exit 0
}
else {
    Write-Host "FAILED  Rolling release '$($run.conclusion)': $($run.url)" -ForegroundColor Red
    exit 1
}
