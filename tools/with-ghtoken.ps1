<#
.SYNOPSIS
    Run any command with GH_TOKEN injected from the registry.

.DESCRIPTION
    gh.ps1 wraps gh.exe specifically; but git (fetch/push to the private v2 remote),
    publish-pages.ps1, and other tools also need GH_TOKEN in the environment, which
    harness shells DON'T inherit (it lives at User scope in the registry). Rather than
    re-inlining "$env:GH_TOKEN = [Environment]::GetEnvironmentVariable('GH_TOKEN','User')"
    on every such call (28 times in the last week alone), route the command through here.

    Reads the persisted token, exposes a `gh` function pointing at the portable gh.exe,
    then runs the given command line. The token is never echoed.

.EXAMPLE
    tools\with-ghtoken.ps1 git fetch v2 master

.EXAMPLE
    tools\with-ghtoken.ps1 "git fetch v2 master; git rev-list --left-right --count v2/master...integration"

.EXAMPLE
    tools\with-ghtoken.ps1 gh api repos/ioSenderV2/ioSender --jq .full_name

.EXAMPLE
    tools\with-ghtoken.ps1 docs\manual\publish-pages.ps1
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CommandLine
)

if (-not $CommandLine -or $CommandLine.Count -eq 0) {
    Write-Host "Usage: tools\with-ghtoken.ps1 <command...>   e.g. tools\with-ghtoken.ps1 git fetch v2 master" -ForegroundColor Yellow
    exit 1
}

$token = [Environment]::GetEnvironmentVariable('GH_TOKEN', 'User')
if ([string]::IsNullOrWhiteSpace($token)) {
    Write-Host "ERROR: GH_TOKEN not set at User scope in the registry." -ForegroundColor Red
    exit 1
}
$env:GH_TOKEN = $token   # never echoed

# make `gh` resolve to the portable exe for the duration of the invoked command
$ghExe = 'C:\Users\steve\tools\gh\bin\gh.exe'
function gh { & $ghExe @args }

$cmd = $CommandLine -join ' '
Invoke-Expression $cmd
exit $LASTEXITCODE
