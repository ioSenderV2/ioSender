# Shared helpers for the turn-loop hooks (see docs/playbooks/turn_workflow_loop.md).
# Dot-sourced by every hook script. Never run directly.

$ErrorActionPreference = 'Stop'

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# IOSENDER_TURN_STATE redirects state to a scratch file so test-gates.ps1 can exercise the hooks
# without destroying the running turn's own record (it did exactly that once - a backup/restore
# dance around the live file is not good enough, the tests must never touch it at all).
$script:StatePath = if ($env:IOSENDER_TURN_STATE) { $env:IOSENDER_TURN_STATE }
                    else { Join-Path $script:RepoRoot '.claude\turn-state.json' }

function Get-RepoRoot { $script:RepoRoot }

# Read the hook payload Claude Code writes to stdin. Returns $null if there isn't one.
function Read-HookInput {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    try { return $raw | ConvertFrom-Json } catch { return $null }
}

function New-TurnState {
    [pscustomobject]@{
        started      = (Get-Date).ToString('o')
        prompt       = ''
        plan         = ''
        edits        = @()
        commits      = 0
        launchBuild  = $false
        stopBlocks   = 0
    }
}

function Get-TurnState {
    if (-not (Test-Path $script:StatePath)) { return (New-TurnState) }
    try {
        $s = Get-Content $script:StatePath -Raw | ConvertFrom-Json
        # ConvertFrom-Json gives back a single object where an array had one element.
        if ($null -eq $s.edits) { $s | Add-Member -Force NoteProperty edits @() }
        $s.edits = @($s.edits)
        return $s
    } catch { return (New-TurnState) }
}

function Set-TurnState($State) {
    $dir = Split-Path $script:StatePath -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $json = $State | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($script:StatePath, $json, (New-Object System.Text.UTF8Encoding($false)))
}

# Emit the hook's JSON result on stdout. Anything else printed is treated as plain output.
function Write-HookResult($Result) {
    if ($null -eq $Result) { return }
    [Console]::Out.Write(($Result | ConvertTo-Json -Depth 10 -Compress))
}

function Deny-Tool([string]$Reason) {
    Write-HookResult ([pscustomobject]@{
        hookSpecificOutput = [pscustomobject]@{
            hookEventName            = 'PreToolUse'
            permissionDecision       = 'deny'
            permissionDecisionReason = $Reason
        }
    })
}

# Absolute Windows path -> repo-relative, forward slashes, for comparing against `git status`.
function ConvertTo-RepoRelative([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try { $full = [System.IO.Path]::GetFullPath($Path) } catch { return $null }
    if (-not $full.StartsWith($script:RepoRoot, [StringComparison]::OrdinalIgnoreCase)) { return $null }
    $rel = $full.Substring($script:RepoRoot.Length).TrimStart('\', '/')
    return $rel -replace '\\', '/'
}

# Files whose change means the solution should be rebuilt before handing the turn back.
function Test-IsBuildRelevant([string]$RelPath) {
    if (-not $RelPath) { return $false }
    return $RelPath -match '\.(cs|xaml|csproj|resx|config)$'
}
