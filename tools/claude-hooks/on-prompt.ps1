# UserPromptSubmit: start a fresh turn, remember what was asked, and put the loop in front of Claude.
. (Join-Path $PSScriptRoot 'turn-lib.ps1')

$in = Read-HookInput

$state = New-TurnState
if ($in -and $in.prompt) {
    $p = [string]$in.prompt
    $state.prompt = $p.Substring(0, [Math]::Min(2000, $p.Length))
}
Set-TurnState $state

$checklist = @'
== TURN LOOP (docs/playbooks/turn_workflow_loop.md - canonical, follow all of it) ==
1. PLAN FIRST. Before the first Edit/Write, state in your visible response what you understood and
   how you intend to do it. A plain question ("can we...", "what if...") is a DISCUSSION - answer it,
   do not implement. Record the plan: .\tools\turn.ps1 plan "<restated ask + approach>"
2. IMPLEMENT. One AskUserQuestion call at a time. New UI control -> inline x:Uid as you create it.
   On a rename, grep the old identifier repo-wide BEFORE the first build.
3. INTERIM BUILDS: .\build.ps1 -Scratch, and only when you must compile-check while a live instance
   is mid-test. Not reflexively before every -Launch.
4. COMMIT AS YOU GO - each piece the moment it is verified. Never save commits up.
5. FINAL BUILD, after the last commit, is the only one with -Launch:
   .\build.ps1 -Launch -message="what we're testing"
6. LOCALE, while the user tests: $env:PYTHONIOENCODING="utf-8"; python tools/locadd.py
7. HAND OFF - say plainly what to test. The turn ends here.

HARD RULES (hook-enforced, do not attempt): no -testserver unless asked THIS turn and only against
-simulator; no git push / release inside a turn (that is end-of-session); push remote is v2, not origin.
'@

Write-HookResult ([pscustomobject]@{
    hookSpecificOutput = [pscustomobject]@{
        hookEventName     = 'UserPromptSubmit'
        additionalContext = $checklist
    }
})
