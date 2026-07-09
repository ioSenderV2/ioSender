<#
.SYNOPSIS
  Run the portable gh.exe with the GH_TOKEN injected from the registry.
  Playbook: docs/playbooks/github_cli_token.md.

.DESCRIPTION
  gh is installed portable (no PATH entry) and the PAT lives in GH_TOKEN at
  User scope in the registry, which harness shells DON'T inherit. This wrapper
  reads the persisted token and passes all args straight through to gh.exe.

.EXAMPLE
  tools\gh.ps1 auth status
  tools\gh.ps1 repo fork OWNER/REPO --clone=false
  tools\gh.ps1 api /repos/OWNER/REPO
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Args
)

$gh = 'C:\Users\steve\tools\gh\bin\gh.exe'
if (-not (Test-Path $gh)) { Write-Host "ERROR: gh.exe not found at $gh" -ForegroundColor Red; exit 1 }

$token = [Environment]::GetEnvironmentVariable('GH_TOKEN', 'User')
if ([string]::IsNullOrWhiteSpace($token)) {
    Write-Host "ERROR: GH_TOKEN not set at User scope in the registry." -ForegroundColor Red
    exit 1
}
$env:GH_TOKEN = $token   # never echoed

if (-not $Args -or $Args.Count -eq 0) {
    Write-Host "Usage: tools\gh.ps1 <gh args>   e.g. tools\gh.ps1 auth status" -ForegroundColor Yellow
    exit 1
}

& $gh @Args
exit $LASTEXITCODE
