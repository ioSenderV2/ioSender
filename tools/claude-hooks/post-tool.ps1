# PostToolUse: record what actually happened this turn, so the Stop gate can check facts, not vibes.
. (Join-Path $PSScriptRoot 'turn-lib.ps1')

$in = Read-HookInput
if (-not $in) { exit 0 }

$state   = Get-TurnState
$changed = $false

switch -Regex ([string]$in.tool_name) {
    '^(Edit|Write|NotebookEdit)$' {
        $path = $null
        if ($in.tool_input -and $in.tool_input.file_path) { $path = [string]$in.tool_input.file_path }
        $rel = ConvertTo-RepoRelative $path
        if ($rel -and ($state.edits -notcontains $rel)) {
            $state.edits = @($state.edits) + $rel
            $changed = $true
        }
    }
    '^(Bash|PowerShell)$' {
        $cmd = ''
        if ($in.tool_input -and $in.tool_input.command) { $cmd = [string]$in.tool_input.command }
        if ($cmd -match '(?i)\bgit\s+commit\b') {
            $state.commits = [int]$state.commits + 1
            $changed = $true
        }
        if ($cmd -match '(?i)build\.ps1' -and $cmd -match '(?i)-Launch') {
            $state.launchBuild = $true
            $changed = $true
        }
    }
}

if ($changed) { Set-TurnState $state }
exit 0
