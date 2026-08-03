# Stop: the turn does not end until the checkable steps are actually done.
# Checks facts only - uncommitted work, missing final build. The plan step is audited, not enforced.
. (Join-Path $PSScriptRoot 'turn-lib.ps1')

$in    = Read-HookInput
$state = Get-TurnState

$edits = @($state.edits)
if ($edits.Count -eq 0) { exit 0 }   # nothing was changed this turn; nothing to check

# Loop-breaker: never wedge the session on a check that cannot be satisfied.
if ([int]$state.stopBlocks -ge 3) {
    Write-HookResult ([pscustomobject]@{
        systemMessage = "Turn-loop gate gave up after 3 blocks and let the turn end. Something in tools/claude-hooks/on-stop.ps1 is likely wrong - check it rather than working around it."
    })
    exit 0
}

# --- Which of this turn's edited files are still uncommitted? ---
$dirty = @()
Push-Location (Get-RepoRoot)
try {
    $porcelain = @(& git status --porcelain 2>$null)
} finally { Pop-Location }

$dirtySet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($line in $porcelain) {
    if ($line.Length -lt 4) { continue }
    $p = $line.Substring(3).Trim()
    if ($p -match '\s->\s') { $p = ($p -split '\s->\s')[-1] }   # rename: take the destination
    $p = $p.Trim('"')
    [void]$dirtySet.Add($p)
}
foreach ($e in $edits) { if ($dirtySet.Contains($e)) { $dirty += $e } }

$problems = @()

if ($dirty.Count -gt 0) {
    $list = ($dirty | Select-Object -First 12) -join "`n  - "
    $problems += @"
UNCOMMITTED WORK. These files were edited this turn and are not committed:
  - $list

Step 4 of the loop: commit as you go, each piece the moment it is verified - never saved up for the
end. Commit them now, then finish the turn.
"@
}

$buildRelevant = @($edits | Where-Object { Test-IsBuildRelevant $_ })
if ($buildRelevant.Count -gt 0 -and -not $state.launchBuild) {
    $problems += @"
NO FINAL BUILD. Source files changed this turn ($($buildRelevant.Count) of them) but no
build.ps1 -Launch was run, so the user has nothing to test.

Step 5 of the loop - after the last commit:
  .\build.ps1 -Launch -message="what we're testing"
"@
}

if ($problems.Count -gt 0) {
    $state.stopBlocks = [int]$state.stopBlocks + 1
    Set-TurnState $state
    $reason = "The turn is not finished (docs/playbooks/turn_workflow_loop.md):`n`n" +
              (($problems | ForEach-Object { $_ }) -join "`n`n")
    Write-HookResult ([pscustomobject]@{ decision = 'block'; reason = $reason })
    exit 0
}

# --- Everything checkable passed. Audit the plan step for the user's benefit. ---
if ([string]::IsNullOrWhiteSpace([string]$state.plan)) {
    Write-HookResult ([pscustomobject]@{
        systemMessage = "Turn-loop audit: $($edits.Count) file(s) changed with no plan recorded. Step 1 (state understanding before editing) was skipped this turn."
    })
}
exit 0
