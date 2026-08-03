<#
.SYNOPSIS
  Inspect or annotate the current turn (see docs/playbooks/turn_workflow_loop.md).

.EXAMPLE
  .\tools\turn.ps1 plan "Add a Foo column to the job list; touches JobView.xaml + JobModel.cs"
  .\tools\turn.ps1 status
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][ValidateSet('plan', 'status')][string]$Action = 'status',
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Text
)

. (Join-Path $PSScriptRoot 'claude-hooks\turn-lib.ps1')

$state = Get-TurnState

switch ($Action) {
    'plan' {
        $p = ($Text -join ' ').Trim()
        if ([string]::IsNullOrWhiteSpace($p)) { Write-Error "Nothing to record. Pass the plan as text."; exit 1 }
        $state.plan = $p
        Set-TurnState $state
        Write-Host "Plan recorded for this turn."
    }
    'status' {
        $edits = @($state.edits)
        Write-Host ""
        Write-Host "Turn started : $($state.started)"
        Write-Host "Prompt       : $(if ($state.prompt) { ($state.prompt -replace '\s+',' ').Substring(0, [Math]::Min(90, ($state.prompt -replace '\s+',' ').Length)) } else { '(none)' })"
        Write-Host "Plan         : $(if ($state.plan) { $state.plan } else { '(NOT RECORDED)' })"
        Write-Host "Files edited : $($edits.Count)"
        foreach ($e in ($edits | Select-Object -First 20)) { Write-Host "               $e" }
        Write-Host "Commits      : $($state.commits)"
        Write-Host "-Launch build: $($state.launchBuild)"
        Write-Host ""
    }
}
