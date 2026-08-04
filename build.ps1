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

.PARAMETER DefaultConfig
    Run this build against a brand-new config instead of yours. Your
    %AppData%\ioSender\App.config is moved aside, the app is launched, and the script WAITS -
    quitting ioSender is what ends the session. Your own config is then moved back, and what
    the session produced is left at %AppData%\ioSender\App.config.default-session for you (or
    Claude) to look at and, if it's worth keeping, copy into the repo's Default-App.config.

    The script never copies the template in: with no App.config present, ioSender's own
    first-run path (AppConfig.SeedUserConfigDir) seeds one from the shipped Default-App.config,
    so the run really is what a new install gets - right for default-matching screenshots, and
    for arranging a layout by doing it rather than describing it. Implies -Launch.

.EXAMPLE
    .\build.ps1 -Clean -Launch
    Wipe every project's bin\/obj\ first, then build and launch - use after a build fails
    referencing a file that was just deleted/renamed (stale incremental cache), not routinely.

.PARAMETER Shot
    Manual-screenshot capture, for use with -DefaultConfig: name the target file in
    docs\manual\img and, once you quit the app, the newest capture from the Snipping Tool folder
    is filed there (via tools\copy-latest-screenshot.ps1). Only a capture taken AFTER the launch
    counts - forget to shoot one and it says so rather than re-filing an older image. Pair it with
    -message so the app itself tells you which screen to shoot.

.EXAMPLE
    .\build.ps1 -DefaultConfig
    Build, launch a first-run-clean ioSender, and wait. Arrange the layout, quit the app, and
    your own settings are back by the time the prompt returns.

.EXAMPLE
    .\build.ps1 -default-config -Shot main-window-tools-menu -message="Open the Tools menu, then shoot"
    Same, but on a default config for a screenshot that matches a fresh install - and the capture
    is filed as docs\manual\img\main-window-tools-menu.png when you quit.
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
    # Spelled -default-config on the command line to match ioSender's own flags (-self-relaunch,
    # -forgetnetwork); -DefaultConfig works too, and the alias keeps the variable readable in here.
    [Alias('default-config')]
    [switch]$DefaultConfig,
    # Target name in docs\manual\img for the screenshot taken during a -DefaultConfig session.
    [string]$Shot,
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

# ---------------------------------------------------------------------------------------------
# Default-config session (-DefaultConfig). Move your own App.config aside, run, put it back when
# the app exits. This script NEVER copies the template into %AppData% - with no App.config there,
# ioSender's own first-run path (AppConfig.SeedUserConfigDir) seeds one from Default-App.config,
# so the run exercises the real fresh-install behaviour instead of imitating it. What the session
# produced is parked next to your config; deciding whether any of it belongs in the shipped
# template is a judgement call, so it happens outside this script.
# ---------------------------------------------------------------------------------------------
$userCfgDir = Join-Path $env:APPDATA 'ioSender'
$liveCfg    = Join-Path $userCfgDir 'App.config'
$stashedCfg = Join-Path $userCfgDir 'App.config.mine'            # yours, parked for the session
$sessionCfg = Join-Path $userCfgDir 'App.config.default-session' # what the session produced

function Stop-IoSenderAndWait {
    # A running instance rewrites App.config as it exits, so a swap has to wait for the process to
    # be GONE, not merely signalled - otherwise its dying write lands on the file we just moved in.
    $procs = @(Get-Process ioSender -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }
    $procs | Stop-Process -Force
    try { $procs | Wait-Process -Timeout 15 -ErrorAction Stop } catch { }
}

# Newest capture in the Snipping Tool folder BEFORE the app starts, so -Shot can tell a screenshot
# taken during the session from one that was already sitting there. Without this, quitting without
# shooting anything would quietly re-file the previous screen as the new one.
$screenshotsDir = 'C:\Users\steve\OneDrive\Pictures\Screenshots'
function Get-NewestScreenshotTime {
    $f = Get-ChildItem -Path $screenshotsDir -Filter '*.png' -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($f) { $f.LastWriteTime } else { [datetime]::MinValue }
}

if ($Shot -and -not $DefaultConfig) { throw "-Shot files the capture when a session ends, so it needs -DefaultConfig (that is the run that waits for you to quit)." }

if ($DefaultConfig) {
    if ($Scratch) { throw "-DefaultConfig runs the app; -Scratch is a verify-only build. Pass one." }
    if ($Shot) { $shotBaseline = Get-NewestScreenshotTime }
    # Refuse rather than overwrite: a stash present here means an earlier session died before its
    # restore, and that file is the only copy of the real settings.
    if (Test-Path $stashedCfg) { throw "$stashedCfg already exists - an earlier session did not restore. Move it back to App.config yourself, then re-run." }
    Stop-IoSenderAndWait
    if (Test-Path $liveCfg) {
        Move-Item $liveCfg $stashedCfg -Force
        Write-Host "==> Your App.config stashed as App.config.mine; ioSender will seed a fresh one from Default-App.config." -ForegroundColor Cyan
    }
    else { Write-Host "==> No App.config to stash - already starting clean." -ForegroundColor Yellow }
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

try {

if ($Launch -or $DefaultConfig) {
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

            $proc = if ($finalArgs) { Start-Process $exe -ArgumentList ($quotedArgs -join ' ') -PassThru } else { Start-Process $exe -PassThru }

            if ($DefaultConfig) {
                # Quitting the app IS the end of the session - block here so the restore below runs
                # the moment ioSender exits, with no second command to remember.
                Write-Host "==> Running on a default config. Quit ioSender to end the session and get your own settings back." -ForegroundColor Cyan
                $proc.WaitForExit()

                # Settings' "Restart" is a SELF-relaunch (GrblConfigView.DoRestart): the app starts a
                # fresh ioSender.exe -self-relaunch and shuts the current one down. So the process we
                # launched exiting does NOT mean the session ended - without following the successor,
                # the restore below kills the instance the user just asked to come back, and a layout
                # change (which prompts for exactly that restart) looks like it did nothing.
                while ($true) {
                    $successor = $null
                    for ($i = 0; $i -lt 20 -and -not $successor; $i++) {
                        Start-Sleep -Milliseconds 250
                        $successor = @(Get-Process ioSender -ErrorAction SilentlyContinue) | Select-Object -First 1
                    }
                    if (-not $successor) { break }
                    Write-Host "==> ioSender relaunched itself - still in the session (quit it to end)." -ForegroundColor Cyan
                    try { $successor | Wait-Process -ErrorAction Stop } catch { }
                }
            }
        }
        else {
            Write-Host "==> Built exe not found: $exe" -ForegroundColor Red
            exit 1
        }
    }
}

}
finally {
    # Always - a failed build, a crash, or Ctrl-C must not leave the real settings parked under
    # another name (PowerShell runs finally on 'exit' too, which is how Invoke-Build ends a
    # failed build). Waiting for the process matters: ioSender writes App.config as it exits.
    if ($DefaultConfig -and (Test-Path $stashedCfg)) {
        Stop-IoSenderAndWait
        if (Test-Path $liveCfg) {
            Move-Item $liveCfg $sessionCfg -Force
            # A session that connected records what it connected TO, and what it found there. This
            # file exists to be copied into Default-App.config, which ships in every release, so
            # blank that here rather than rely on spotting it later. LastMachine matters most: it is
            # the answer to Machine Setup's "start from your machine", and shipping it would
            # pre-answer that wizard with someone else's mill on every new install.
            try {
                $doc = New-Object System.Xml.XmlDocument
                $doc.PreserveWhitespace = $true
                $doc.Load($sessionCfg)
                $scrubbed = $false
                foreach ($tag in 'NetworkHost', 'PortParams', 'LastMachine', 'LastFirmwareBuild') {
                    foreach ($n in $doc.SelectNodes("//$tag")) {
                        if ($n.InnerText) {
                            Write-Host "==> Scrubbed $tag ($($n.InnerText)) from the session config." -ForegroundColor Cyan
                            $n.InnerText = ''
                            $scrubbed = $true
                        }
                    }
                }
                if ($scrubbed) { $doc.Save($sessionCfg) }
            }
            catch { Write-Host "==> Scrub failed ($($_.Exception.Message)) - check this file for your own machine/address before copying it anywhere." -ForegroundColor Yellow }
        }
        Move-Item $stashedCfg $liveCfg -Force
        Write-Host "==> Your App.config is back." -ForegroundColor Green
        if (Test-Path $sessionCfg) { Write-Host "==> The session's config: $sessionCfg" -ForegroundColor Cyan }

        # File the capture only if one was actually taken during the session - see -Shot's help.
        if ($Shot) {
            if ((Get-NewestScreenshotTime) -gt $shotBaseline) {
                & (Join-Path $root 'tools\copy-latest-screenshot.ps1') -Name $Shot
            }
            else {
                Write-Host "==> No screenshot taken this session - docs\manual\img\$Shot is unchanged." -ForegroundColor Yellow
            }
        }
    }
}
