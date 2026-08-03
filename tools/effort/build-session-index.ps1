<#
.SYNOPSIS
  Re-render index.html from sessions.json. Reads no transcripts - it is a pure view of the manifest,
  so it finishes instantly.

.DESCRIPTION
  convo-sessions.ps1 already re-renders the index on every capture, so you only need this when you
  have changed the table's styling/columns and want the existing rows redrawn, or after hand-editing
  sessions.json.

  It used to re-derive every session from every transcript on each run, which had two costs: minutes
  of parsing, and silent DATA LOSS - Claude Code deletes transcripts after cleanupPeriodDays (30 by
  default), so each rebuild quietly dropped the sessions that had aged out. That is how the index got
  down to 157 rows against 197 session HTMLs on disk. The manifest is append-only; rows never vanish.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\effort\build-session-index.ps1
#>
param(
    [string]$OutDir = "$env:USERPROFILE\Downloads\ClaudeConv"
)

. "$PSScriptRoot\convo-common.ps1"

$manifestPath = Get-ManifestPath $OutDir
if (-not (Test-Path $manifestPath)) {
    Write-Host "No sessions.json in $OutDir - run migrate-session-manifest.ps1 once to seed it." -ForegroundColor Yellow
    return
}

$manifest = Read-Manifest $OutDir
$out = Write-IndexHtml $OutDir $manifest
Write-Host ("==> wrote {0} ({1} sessions, from sessions.json - no transcripts read)" -f $out, @($manifest.sessions).Count) -ForegroundColor Green
