<#
.SYNOPSIS
  Guard for the version decision in cut-release.ps1 - that a declared major bump actually gets cut,
  and that a stale declaration cannot hold a release back.

.DESCRIPTION
  cut-release.ps1 normally computes the next version as minor+1 off the last published release, and
  that alone can never express a major bump: with v2.41 out, it can only ever produce 2.42. So
  MainWindow's legacyVersion gets a one-directional vote - it wins only if it is strictly HIGHER.

  Two ways that can silently go wrong, and both ship a wrongly-numbered release rather than failing:
    - a STRING compare instead of a numeric one ("3.0" sorts below "2.42" on the second character,
      and "2.9" sorts above "2.10"), so the bump is quietly ignored;
    - a >= instead of >, which would let a stale legacyVersion pin the version forever.

  This extracts Compare-Ver from the REAL cut-release.ps1 text rather than restating it, so the test
  cannot drift away from what ships - if the function is renamed or reshaped, this fails loudly.

  Run it by hand after touching the version block: pwsh -File tools\test-release-version.ps1
#>

$ErrorActionPreference = 'Stop'

$cutRelease = Join-Path $PSScriptRoot 'cut-release.ps1'
$src = [System.IO.File]::ReadAllText($cutRelease)

$m = [regex]::Match($src, '(?s)function Compare-Ver.*?\n        \}')
if (-not $m.Success) {
    Write-Host "ERROR: Compare-Ver not found in $cutRelease - the version block was renamed or reshaped, and this guard is now looking at the wrong text. Fix the guard, do not delete it." -ForegroundColor Red
    exit 1
}
Invoke-Expression $m.Value

# The decision exactly as cut-release.ps1 makes it: legacyVersion wins only if strictly higher.
function Resolve-Version($declared, $rolling) {
    if ((Compare-Ver $declared $rolling) -gt 0) { return $declared } else { return $rolling }
}

$cases = @(
    @{ declared = '3.0';  rolling = '2.42'; expect = '3.0';  why = 'THE BUMP: 3.0 must beat the rolling 2.42 off v2.41' }
    @{ declared = '3.0';  rolling = '3.1';  expect = '3.1';  why = 'SELF-HEAL: once 3.0 is out, rolling 3.1 outranks a stale legacyVersion' }
    @{ declared = '3.0';  rolling = '3.0';  expect = '3.0';  why = 'EQUAL: no change either way' }
    @{ declared = '2.42'; rolling = '2.42'; expect = '2.42'; why = 'NORMAL: legacyVersion tracking the rolling number is a no-op' }
    @{ declared = '2.41'; rolling = '2.42'; expect = '2.42'; why = 'ONE-DIRECTIONAL: a stale legacyVersion can never hold the version back' }
    @{ declared = '2.9';  rolling = '2.10'; expect = '2.10'; why = 'NUMERIC not string: 9 < 10, though "2.9" > "2.10" as text' }
    @{ declared = '10.0'; rolling = '9.9';  expect = '10.0'; why = 'NUMERIC not string: 10 > 9, though "10.0" < "9.9" as text' }
)

$fail = 0
foreach ($c in $cases) {
    $got  = Resolve-Version $c.declared $c.rolling
    $ok   = $got -eq $c.expect
    if (-not $ok) { $fail++ }
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    $col  = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0}  legacyVersion={1,-5} rolling={2,-5} -> {3,-5} (expected {4,-5})  {5}" -f
                $mark, $c.declared, $c.rolling, $got, $c.expect, $c.why) -ForegroundColor $col
}

Write-Host ""
if ($fail) { Write-Host "$fail of $($cases.Count) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "all $($cases.Count) passed" -ForegroundColor Green
