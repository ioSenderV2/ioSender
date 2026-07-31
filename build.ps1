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
    Skip killing a running ioSender.exe first. Only skips the kill step - it can't release the
    OS file lock a running instance holds on its own DLLs/EXE, so this alone still fails
    (MSB3027/MSB3021 "file is locked") the moment there's an actual rebuild to do. Prefer
    -Scratch for an interim/verification build that must not disturb a running test instance.

.PARAMETER Scratch
    Build into a side output folder (bin\<Configuration>.scratch\, via MSBuild's OutDir
    override) instead of the live bin\<Configuration>\ tree, so the rebuild can never collide
    with a running instance's file lock in the first place - no kill needed, nothing disturbed.
    This is what an interim/verification build should use. Implies no launch and no kill: a
    scratch build only proves the code compiles, it isn't the build you're meant to test.

.PARAMETER Headless
    Launch with -headless forwarded to ioSender.exe, so an unhandled exception dumps to
    %AppData%\ioSender\ioSender.crash.log and exits with 0xFA11 (64017) instead of blocking
    on a modal error dialog. Use for unattended runs; omit for interactive testing so you
    still see the crash dialog.

.PARAMETER Clean
    Delete every project's bin\ and obj\ folders before building. MSBuild's own -t:Clean only
    removes files it tracked THIS build - it doesn't fix a stale incremental cache (confirmed
    2026-07-31: obj\Debug's MarkupCompile cache still listed a just-deleted .xaml file, and the
    project failed to build with "Could not find file ...xaml" pointing at an unrelated view,
    since Page-list caching is keyed on the whole project, not the one file that changed). A
    real delete-and-rebuild is the only fix once that happens; use -Clean when a build fails
    referencing a file you know is gone, or after deleting/renaming any .xaml/.cs file.

.DESCRIPTION (no env vars)
    ioSender itself reads NO environment variables (2026-07-25) - every launch-time behavior
    is a real CLI flag, and every persistent setting (OBS demo-recording config, AI review
    key, etc.) lives in Settings > App or the registry instead of hidden env state. The exact
    command line being launched is always printed.

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

.EXAMPLE
    .\build.ps1 -Launch '-debuglog=comms-tx,comms-rx'
    A pass-through arg containing "=value,value" MUST be quoted - that's PowerShell's own
    command-line parser (it tries to build an array at the comma), not this script; the
    error surfaces before build.ps1 even starts running. Once quoted, PositionalBinding is
    off (see CmdletBinding below) so it can't be mis-bound to -Configuration either.

.EXAMPLE
    .\build.ps1 -Scratch
    Verify-only build into bin\Debug.scratch\ - doesn't touch bin\Debug\, so a running test
    instance launched from there keeps running untouched.

.EXAMPLE
    .\build.ps1 -Clean -Launch
    Wipe every project's bin\/obj\ first, then build and launch - use after a build fails
    referencing a file that was just deleted/renamed (stale incremental cache), not routinely.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('Debug', 'Release', 'Both')]
    [string]$Configuration = 'Debug',
    [switch]$Launch,
    [switch]$NoKill,
    [switch]$Scratch,
    [switch]$Headless,
    [switch]$Clean,
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
    param([string]$Config, [string]$OutDir)
    Write-Host "==> Building $Config$(if ($OutDir) { ' (scratch)' }) ..." -ForegroundColor Cyan
    # -restore so PackageReference deps (e.g. WpfUiTestServer from NuGet) resolve before Build.
    $props = @("-p:Configuration=$Config")
    # OutDir is a global MSBuild property: passed on the command line, it overrides every
    # project's own (per-project-relative) OutputPath and consolidates the whole solution's
    # output into ONE folder - same shape as the normal bin\<Config>\ tree, just elsewhere, so
    # a scratch build can never collide with the live tree's file lock.
    if ($OutDir) { $props += "-p:OutDir=$OutDir" }
    & $msbuild $solution -restore -t:Build @props -m -nologo -v:minimal -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) {
        Write-Host "==> $Config build FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "==> $Config build OK" -ForegroundColor Green
}

function ScratchOutDir([string]$Config) {
    # MSBuild wants OutDir to end in a separator, but a SINGLE trailing backslash right before the
    # closing quote .NET adds around this arg (it contains a space - "ioSender XL") gets read by
    # Win32's own argv parser as an ESCAPED quote, not an end-of-argument marker - the quote never
    # closes and everything after silently gets absorbed into this one value (confirmed: every
    # following project's restore step failed with garbage combining this path with "-m -nologo
    # ..."). Doubling it (\\) escapes to one literal backslash instead, which is what OutDir wants.
    (Join-Path $root ("ioSender XL\ioSender XL\bin\{0}.scratch" -f $Config)) + '\\'
}

if (-not (Test-Path $solution)) { throw "Solution not found: $solution" }
$msbuild = Find-MSBuild

# A scratch build never touches bin\<Config>\ at all, so there's normally nothing to kill - but
# -Clean deletes the LIVE bin\ tree too (not just scratch's side folder), so a running instance's
# locked DLLs must go first regardless of -Scratch, or the delete below fails with Access to the
# path ... is denied (confirmed 2026-07-31).
if (-not $NoKill -and (-not $Scratch -or $Clean)) {
    Get-Process ioSender -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($Clean) {
    # MSBuild's own -m (node reuse) leaves worker processes alive after this script exits, holding
    # file locks on satellite resource DLLs (e.g. bin\Debug\en-US\*.resources.dll) even though no
    # ioSender.exe is running - confirmed 2026-07-31: Remove-Item failed Access to the path ... is
    # denied on one of these with zero ioSender processes present. Killing ioSender alone (above)
    # isn't enough; these must go too before a clean delete can succeed.
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '^(MSBuild|VBCSCompiler)$' } | Stop-Process -Force
    Write-Host "==> -Clean: removing bin\/obj\ for every project ..." -ForegroundColor Cyan
    Get-ChildItem -Path $root -Filter '*.csproj' -Recurse -File | ForEach-Object {
        $projDir = $_.DirectoryName
        foreach ($sub in 'bin', 'obj') {
            $p = Join-Path $projDir $sub
            if (Test-Path $p) {
                Remove-Item $p -Recurse -Force
                Write-Host "    removed $p"
            }
        }
    }
}

switch ($Configuration) {
    'Both'  {
        Invoke-Build 'Debug' $(if ($Scratch) { ScratchOutDir 'Debug' })
        Invoke-Build 'Release' $(if ($Scratch) { ScratchOutDir 'Release' })
    }
    default { Invoke-Build $Configuration $(if ($Scratch) { ScratchOutDir $Configuration }) }
}

if ($Launch) {
    if ($Scratch) {
        Write-Host "==> -Launch ignored with -Scratch (a scratch build is verify-only)." -ForegroundColor Yellow
    }
    elseif ($Configuration -eq 'Both') {
        Write-Host "==> -Launch ignored for 'Both' (pass -Configuration Debug to launch)." -ForegroundColor Yellow
    }
    else {
        $exe = Join-Path $root ($exeRel -f $Configuration)
        if (Test-Path $exe) {
            $finalArgs = @($AppArgs)
            if ($Headless -and -not ($finalArgs -contains '-headless')) { $finalArgs += '-headless' }

            # Start-Process -ArgumentList joins array elements with a bare space and does NOT re-quote ones
            # that contain whitespace - so a multi-word value (e.g. -message="two words") silently split back
            # into separate argv entries on the CHILD side, and ioSender only ever saw the first word attached
            # to -message=. Confirmed on real hardware 2026-07-30 (user: "never seen ioSender display more
            # than one word"). Quote any element containing whitespace before joining so it survives as one
            # argv token - the quotes are just command-line syntax (stripped by the OS's own argv parser),
            # not literal content, so ioSender's own arg.StartsWith("-message=") still sees the raw text.
            $quotedArgs = $finalArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }

            $cmdLine = if ($finalArgs) { "$exe $($quotedArgs -join ' ')" } else { $exe }
            Write-Host "==> Launching: $cmdLine" -ForegroundColor Cyan

            if ($finalArgs) { Start-Process $exe -ArgumentList ($quotedArgs -join ' ') } else { Start-Process $exe }
        }
        else {
            Write-Host "==> Built exe not found: $exe" -ForegroundColor Red
            exit 1
        }
    }
}
