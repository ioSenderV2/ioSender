<#
.SYNOPSIS
    Headless build/launch for ioSender XL - no Visual Studio GUI needed.

.DESCRIPTION
    Kills any running ioSender.exe (so the DLL copy step can't fail with a
    file lock), builds the solution with MSBuild, and optionally launches the
    built app. MSBuild is discovered via vswhere so it survives a VS edition
    or version change.

    This is the same loop the build/commit workflow uses; run it from the
    VS Code terminal or via Terminal -> Run Task.

.PARAMETER Configuration
    Debug (default), Release, or Both. "Both" builds Debug then Release and
    fails if either fails - use it as the pre-commit verify.

.PARAMETER Launch
    After a successful build, start the built ioSender.exe. Ignored for
    "Both" (ambiguous which to launch); combine with -Configuration Debug.

.PARAMETER NoKill
    Skip killing a running ioSender.exe first.

.PARAMETER Headless
    Launch with IOSENDER_HEADLESS=1 so an unhandled exception dumps to
    %AppData%\ioSender\ioSender.crash.log and exits with 0xFA11 (64017)
    instead of blocking on a modal error dialog. Use for unattended runs;
    omit for interactive testing so you still see the crash dialog.

.EXAMPLE
    .\build.ps1 -Launch
    Kill, Debug build, launch - the standard "go test it" step.

.EXAMPLE
    .\build.ps1 -Configuration Both
    Verify Debug + Release both build clean before committing.

.EXAMPLE
    .\build.ps1 -Launch -forgetnetwork -demomarker
    Debug build, then launch with those flags forwarded to ioSender.exe (open the
    connect dialog + arm the demo-video markers). Any trailing tokens pass through.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release', 'Both')]
    [string]$Configuration = 'Debug',
    [switch]$Launch,
    [switch]$NoKill,
    [switch]$Headless,
    # Any trailing tokens are forwarded verbatim to the launched ioSender.exe, e.g.
    #   .\build.ps1 -Launch -forgetnetwork -demomarker
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$solution = Join-Path $root 'ioSender XL\ioSender XL.sln'
$exeRel = 'ioSender XL\ioSender XL\bin\{0}\ioSender.exe'

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }
    # Fallback: the known Enterprise 2022 path on this box.
    $fallback = 'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path $fallback) { return $fallback }
    throw "MSBuild not found (vswhere and fallback both missed). Is Visual Studio / Build Tools installed?"
}

function Invoke-Build {
    param([string]$Config)
    Write-Host "==> Building $Config ..." -ForegroundColor Cyan
    & $msbuild $solution -t:Build "-p:Configuration=$Config" -m -nologo -v:minimal -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) {
        Write-Host "==> $Config build FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "==> $Config build OK" -ForegroundColor Green
}

if (-not (Test-Path $solution)) { throw "Solution not found: $solution" }
$msbuild = Find-MSBuild

if (-not $NoKill) {
    Get-Process ioSender -ErrorAction SilentlyContinue | Stop-Process -Force
}

switch ($Configuration) {
    'Both'  { Invoke-Build 'Debug'; Invoke-Build 'Release' }
    default { Invoke-Build $Configuration }
}

if ($Launch) {
    if ($Configuration -eq 'Both') {
        Write-Host "==> -Launch ignored for 'Both' (pass -Configuration Debug to launch)." -ForegroundColor Yellow
    }
    else {
        $exe = Join-Path $root ($exeRel -f $Configuration)
        if (Test-Path $exe) {
            $argMsg = if ($AppArgs) { " $($AppArgs -join ' ')" } else { '' }
            Write-Host "==> Launching $Configuration ioSender$argMsg ..." -ForegroundColor Cyan
            if ($Headless) { $env:IOSENDER_HEADLESS = '1' } else { Remove-Item Env:\IOSENDER_HEADLESS -ErrorAction SilentlyContinue }
            if ($AppArgs) { Start-Process $exe -ArgumentList $AppArgs } else { Start-Process $exe }
        }
        else {
            Write-Host "==> Built exe not found: $exe" -ForegroundColor Red
            exit 1
        }
    }
}
