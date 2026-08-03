<#
.SYNOPSIS
  Regression test for the turn-loop hook gates (docs/playbooks/turn_workflow_loop.md).

  Run it after changing any hook script:  .\tools\claude-hooks\test-gates.ps1
  Note it cannot be inlined into a shell command - the test cases contain the very strings the
  gates match on, so an inline version blocks itself. That is the gates working, not a bug.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$here  = $PSScriptRoot
$ps    = 'powershell.exe'
$flags = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File')

# Never touch the running turn's state file - point the hooks at a scratch one instead.
$env:IOSENDER_TURN_STATE = Join-Path ([System.IO.Path]::GetTempPath()) "iosender-turn-test-$PID.json"

function Invoke-Hook([string]$Script, [hashtable]$Payload) {
    ($Payload | ConvertTo-Json -Compress -Depth 6) | & $ps @flags (Join-Path $here $Script)
}
function Set-Prompt([string]$Text) {
    Invoke-Hook 'on-prompt.ps1' @{ prompt = $Text } | Out-Null
}

$tsFlag  = '-test' + 'server'          # split so this file's own commands never match the gate
$results = @()
function Check([string]$Name, [string]$Expect, [string]$Got) {
    $ok = if ($Expect -eq $Got) { 'PASS' } else { 'FAIL' }
    $script:results += [pscustomobject]@{ Result = $ok; Expected = $Expect; Got = $Got; Case = $Name }
}
function Gate([string]$Command) {
    $out = Invoke-Hook 'pre-shell.ps1' @{ tool_name = 'Bash'; tool_input = @{ command = $Command } }
    if ($out) { 'DENY' } else { 'allow' }
}

# ---------------------------------------------------------------- prompt did NOT ask for anything
Set-Prompt 'add a Foo column to the job list'

Check 'commit message mentioning the flag (heredoc)' 'allow' `
      (Gate "git commit -F- <<'EOF'`ngate denies $tsFlag and git push now`nEOF")
Check 'commit -m mentioning the flag'                'allow' (Gate "git commit -m `"denies $tsFlag`"")
Check 'commit -m mentioning git push'                'allow' (Gate 'git commit -m "no git push in a turn"')
Check 'real launch with the flag'                    'DENY'  (Gate ".\build.ps1 -Launch $tsFlag")
Check 'flag with no launch (drives nothing)'         'allow' (Gate ".\build.ps1 -Scratch $tsFlag")
Check 'real exe with the flag'                       'DENY'  (Gate ".\ioSender.exe $tsFlag")
Check 'env-var form'                                 'DENY'  (Gate '$env:IOSENDER_TESTSERVER=1; .\build.ps1')
Check 'real git push'                                'DENY'  (Gate 'git push v2 integration')
Check 'push-all script'                              'DENY'  (Gate '.\tools\push-all.ps1')
Check 'ordinary interim build'                       'allow' (Gate '.\build.ps1 -Scratch')
Check 'ordinary launch build'                        'allow' (Gate '.\build.ps1 -Launch -message="x"')

# ---------------------------------------------------------------- prompt DID ask for the harness
Set-Prompt "drive it with the $tsFlag against the simulator"
Check 'requested + simulator'                        'allow' (Gate ".\build.ps1 -Launch -simulator $tsFlag")
Check 'requested but NO simulator'                   'DENY'  (Gate ".\build.ps1 -Launch $tsFlag")

# ---------------------------------------------------------------- prompt DID ask to push
Set-Prompt 'wrap up the session and push'
Check 'push when asked'                              'allow' (Gate 'git push v2 integration')

Set-Prompt 'Create the repo as private for now.'
Check 'push when asked to create a repo'             'allow' (Gate 'git push -u origin main')

Set-Prompt 'add a Foo column to the job list'
Check 'push still blocked on an unrelated ask'       'DENY'  (Gate 'git push -u origin main')

# ---------------------------------------------------------------- Stop gate
Set-Prompt 'some change'
Check 'stop: nothing edited'                         'allow' `
      $(if (Invoke-Hook 'on-stop.ps1' @{}) { 'DENY' } else { 'allow' })

Invoke-Hook 'post-tool.ps1' @{ tool_name = 'Edit'
                               tool_input = @{ file_path = (Join-Path $here 'turn-lib.ps1') } } | Out-Null
$stop = Invoke-Hook 'on-stop.ps1' @{}
Check 'stop: edited file left uncommitted'           'DENY'  $(if ($stop) { 'DENY' } else { 'allow' })

# ---------------------------------------------------------------- clean up the scratch state
if (Test-Path $env:IOSENDER_TURN_STATE) { Remove-Item $env:IOSENDER_TURN_STATE -Force }

$results | Format-Table -AutoSize
$failed = @($results | Where-Object Result -eq 'FAIL').Count
if ($failed) { Write-Host "$failed FAILED" -ForegroundColor Red; exit 1 }
Write-Host "All $($results.Count) gate checks passed." -ForegroundColor Green
