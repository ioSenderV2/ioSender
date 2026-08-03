# PreToolUse (Bash|PowerShell): the two hard denials - -testserver, and push/release inside a turn.
. (Join-Path $PSScriptRoot 'turn-lib.ps1')

$in = Read-HookInput
if (-not $in) { exit 0 }

$cmd = ''
if ($in.tool_input -and $in.tool_input.command) { $cmd = [string]$in.tool_input.command }
if ([string]::IsNullOrWhiteSpace($cmd)) { exit 0 }

# Match against the CODE, not the DATA it carries. A commit message that merely mentions
# "-testserver" or "git push" is prose, not an invocation - stripping heredoc bodies and -m/-F
# message arguments keeps the gates from firing on their own documentation.
function Get-CommandCode([string]$Command) {
    $c = $Command
    $c = [regex]::Replace($c, "(?s)<<-?\s*'?([A-Za-z_]\w*)'?\r?\n.*?\r?\n\1\b", ' ')  # heredoc bodies
    $c = [regex]::Replace($c, "(?s)-m\s+'[^']*'", ' -m ')                             # -m '...'
    $c = [regex]::Replace($c, '(?s)-m\s+"[^"]*"', ' -m ')                             # -m "..."
    return $c
}
$code = Get-CommandCode $cmd

$state  = Get-TurnState
$prompt = [string]$state.prompt

# --- Gate 1: the UI test server. Two incidents, one of them mid-homing on the real machine. ---
# Only fires on something that can actually launch the app. -testserver is an ioSender.exe startup
# arg: it reaches the exe via build.ps1's -Launch passthrough, or by running the exe directly.
# Without a launch it drives nothing, so a bare mention is not worth blocking.
if (($code -match '(?i)-testserver' -and $code -match '(?i)-Launch|ioSender|Start-Process') -or
    $code -match '(?i)IOSENDER_TESTSERVER\s*=') {
    if ($prompt -notmatch '(?i)testserver|test server|ui automation') {
        Deny-Tool @'
BLOCKED: -testserver was not requested in this turn's prompt.

The rule (docs/playbooks/turn_workflow_loop.md, Hard rules): never reach for -testserver on your own
initiative, only when the user asks for it in THIS turn, and even then only against -simulator. On
2026-07-21 an unrequested testserver run sent MDI commands to the real controller mid-homing.

Build and hand off instead - the user tests. If you believe you need it, ask.
'@
        exit 0
    }
    if ($code -notmatch '(?i)-simulator') {
        Deny-Tool @'
BLOCKED: -testserver without -simulator.

Even when requested, the test server may only ever drive the simulator - never the real controller.
Add -simulator, and confirm the target with GET /state/lbl_connectionTarget before any MDI, motion,
homing or reset call.
'@
        exit 0
    }
}

# --- Gate 2: push / release. Pushing v2/master fires the release CI. ---
if ($code -match '(?i)\bgit\s+push\b|push-all\.ps1|\bgh\s+release\s+create\b') {
    if ($prompt -notmatch '(?i)\bpush\b|\brelease\b|\bship\b|\bpublish\b|wrap.?up|end.?of.?session') {
        Deny-Tool @'
BLOCKED: push/release is not part of a turn.

The rule (docs/playbooks/turn_workflow_loop.md, Hard rules): pushing to v2/master fires the release
CI. That belongs to end_of_session_wrapup.md at actual session end, not to finishing a work item.

Commit locally and hand off. The user will say when it is time to push.
'@
        exit 0
    }
}

exit 0
