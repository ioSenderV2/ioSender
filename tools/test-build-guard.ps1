<#
.SYNOPSIS
    Checks build.ps1's wasted-pair warning without running a build.

.DESCRIPTION
    The warning fires when a -Launch build follows a -Scratch build over byte-identical sources
    (added 2026-10-26). Exercising it for real needs two builds, and a build kills whatever
    instance the user is testing with - so this checks the parts that can be checked cheaply:

      1. build.ps1 still parses (the warning block is inside it, so a typo there breaks everything).
      2. Get-SourceFingerprint is pulled out of build.ps1's OWN AST and run here - the real function
         body, not a copy of it - and must return a stable, non-null value on repeated calls. If it
         were unstable, the warning would never fire; if it threw, it would fail open by contract.
      3. The state file round-trips through ConvertTo-Json / ConvertFrom-Json with the three fields
         the comparison reads, and a commit (which changes HEAD but no file) leaves the fingerprint
         equal - which is the exact case the warning exists to catch.

    What this does NOT cover: the inline comparison and the Write-Host block in build.ps1. Those run
    for real on the next -Scratch-then--Launch pair.

    Run:  pwsh -File tools\test-build-guard.ps1          (exit 0 = all checks passed)
#>

$ErrorActionPreference = 'Stop'
$failures = 0

function Check([bool]$ok, [string]$what, [string]$detail = '') {
    $mark = if ($ok) { '  ok    ' } else { '  FAIL  ' }
    $line = $mark + $what
    if ($detail) { $line += "   [$detail]" }
    Write-Host $line -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    if (-not $ok) { $script:failures++ }
}

$repo = Split-Path $PSScriptRoot -Parent
$buildScript = Join-Path $repo 'build.ps1'

Write-Host "test-build-guard: build.ps1's wasted-pair warning`n"

# ---- 1. it still parses -------------------------------------------------------------------------
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($buildScript, [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'build.ps1 parses' $(if ($errors.Count) { $errors[0].Message } else { "$($tokens.Count) tokens" })
if ($errors.Count) { exit 1 }

# ---- 2. the REAL Get-SourceFingerprint, lifted from the AST --------------------------------------
$fn = $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                     $args[0].Name -eq 'Get-SourceFingerprint' }, $true) | Select-Object -First 1
Check ($null -ne $fn) 'Get-SourceFingerprint found in build.ps1'
if (-not $fn) { exit 1 }

# $root is what the function scans; build.ps1 sets it to its own folder.
$root = $repo
. ([scriptblock]::Create($fn.Extent.Text))

$f1 = Get-SourceFingerprint
Check ($null -ne $f1) 'returns a fingerprint for this repo' $f1

$f2 = Get-SourceFingerprint
Check ($f1 -eq $f2) 'stable across calls with no edits (or the warning could never fire)' "$f1 vs $f2"

$parts = "$f1" -split '\|'
Check ($parts.Count -eq 2 -and [int]$parts[0] -gt 100) 'counted a plausible number of source files' "count=$($parts[0])"

Check ($f1 -notmatch '\\(bin|obj)\\') 'fingerprint carries no build-output path' $f1

# It must move when a source file does. A real file, touched and restored - not a synthetic one,
# because a new file would also change the COUNT and so pass for the wrong reason.
$victim = Get-ChildItem -Path $repo -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules|\.git|\.vs)\\' } |
            Select-Object -First 1
if ($victim) {
    $was = $victim.LastWriteTimeUtc
    try {
        $victim.LastWriteTimeUtc = (Get-Date).ToUniversalTime().AddSeconds(5)
        $f3 = Get-SourceFingerprint
        Check ($f3 -ne $f1) 'fingerprint MOVES when a source file is touched' "$f1 -> $f3"
    }
    finally {
        $victim.LastWriteTimeUtc = $was
    }
    $f4 = Get-SourceFingerprint
    Check ($f4 -eq $f1) 'and returns to its old value once the timestamp is restored' "$f4"
}
else {
    Write-Host '  SKIP  fingerprint MOVES when a source file is touched   [no .cs file found]' -ForegroundColor Yellow
}

# ---- 3. the state file the comparison reads ------------------------------------------------------
$tmp = Join-Path $env:TEMP ('ioSender-build-guard-test-{0}.json' -f [guid]::NewGuid().ToString('N'))
try {
    @{ Scratch = $true; TimeUtc = (Get-Date).ToUniversalTime().ToString('o'); Fingerprint = $f1 } |
        ConvertTo-Json | Set-Content -Path $tmp -Encoding UTF8
    $back = Get-Content $tmp -Raw | ConvertFrom-Json

    Check ($back.Scratch -eq $true) 'state file round-trips the Scratch flag' "Scratch=$($back.Scratch)"
    Check ($back.Fingerprint -eq $f1) 'state file round-trips the fingerprint unchanged'
    Check ($null -ne [datetime]::Parse($back.TimeUtc)) 'TimeUtc parses back (the warning prints an age from it)'

    # The case being caught: a commit changes HEAD, no file changes, so the fingerprint must match.
    Check ($back.Scratch -and $back.Fingerprint -eq (Get-SourceFingerprint)) `
          'scratch state + unchanged sources = the condition that warns' 'this is the 2026-10-26 case'
}
finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failures -eq 0) {
    Write-Host 'all checks passed' -ForegroundColor Green
    exit 0
}
Write-Host "$failures CHECK(S) FAILED" -ForegroundColor Red
exit 1
