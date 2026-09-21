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

    This is also what opts the build OUT of scratch (see -Scratch): with -Launch the build goes to
    the live bin\<Configuration>\ tree and a running instance is asked to close first, because that
    is the instance you are about to replace. Without it, nothing running is disturbed.

.PARAMETER NoKill
    Skip killing a running ioSender.exe first. Only skips the kill step - it can't release the
    OS file lock a running instance holds on its own DLLs/EXE, so this alone still fails
    (MSB3027/MSB3021 "file is locked") the moment there's an actual rebuild to do. Prefer
    -Scratch for an interim/verification build that must not disturb a running test instance.

.PARAMETER Scratch
    THE DEFAULT - you rarely need to type this. Build into a side output folder
    (bin\<Configuration>.scratch\, via MSBuild's OutDir override) instead of the live
    bin\<Configuration>\ tree, so the rebuild can never collide with a running instance's file
    lock in the first place - no kill needed, nothing disturbed. A scratch build only proves the
    code compiles; it isn't the build you're meant to test.

    Scratch is assumed unless the invocation is going to RUN the app (-Launch, -Shot,
    -ReviewConfig, -DefaultConfig/-adoptConfig) or -Clean (which deletes the live bin\ tree and
    would otherwise leave it empty). Typing -Scratch explicitly still works, and is the only way
    to get a conflict error out of combining it with one of those - the default never conflicts,
    it just steps aside.

    It used to be opt-in, and the asymmetry is why that changed: forgetting -Scratch on an
    interim compile-check killed the operator's running instance and replaced the binaries
    underneath the run being diagnosed, so the next log came from a different build than the
    symptom did. Forgetting -Launch merely costs a re-run.

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
    .\build.ps1
    Verify-only build into bin\Debug.scratch\ - doesn't touch bin\Debug\, so a running test
    instance launched from there keeps running untouched. This is the default; -Scratch is
    only worth typing to say so out loud.

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

.PARAMETER AdoptConfig
    With -DefaultConfig (which it implies): when you quit, write what the session produced straight
    over the repo's ioSender XL\ioSender XL\Default-App.config instead of just parking it. Two things
    that a plain copy gets wrong are handled - the XML comments ioSender drops (it composes the
    document from scratch on save) are read back out of the existing template and re-injected, and the
    UTF-8 BOM + CRLF it writes are normalised to the repo's BOM-less LF. The template is a tracked
    file, so the printed diff is the review and 'git checkout' is the undo.

.EXAMPLE
    .\build.ps1 -adoptConfig -forgetnetwork -message="Arrange the default layout"
    Launch a first-run-clean ioSender, arrange the layout, quit - and the shipped default template is
    updated, with a diff --stat to show what moved.

.PARAMETER ReviewConfig
    Look at the shipped Default-App.config as the app renders it, after a rebuild. A throwaway folder
    is wiped and seeded with the template, and ioSender is pointed at it with -configpath, so the run
    reads AND writes there - your own %AppData%\ioSender\App.config is never stashed, moved or touched,
    and there is no session to end (quit whenever). -forgetnetwork is added automatically: the template
    carries no connection target, but PreferNetwork would otherwise upgrade the link and connect to the
    real machine. Implies -Launch; cannot be combined with -DefaultConfig/-adoptConfig, which are the
    editing side of the same loop.

.EXAMPLE
    .\build.ps1 -reviewConfig
    Rebuild and see exactly what a fresh install gets. The pair to -adoptConfig: adopt, then review.

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
    # Adopt the session's result straight into the repo's Default-App.config instead of parking it.
    # Implies -DefaultConfig.
    [Alias('adopt-config')]
    [switch]$AdoptConfig,
    # Launch on a throwaway copy of the SHIPPED template, to look at what -adoptConfig produced.
    # Implies -Launch; never touches your own config (see the -configpath seeding below).
    [Alias('review-config')]
    [switch]$ReviewConfig,
    # Target name in docs\manual\img for the screenshot taken during a -DefaultConfig session.
    [string]$Shot,
    # Any trailing tokens are forwarded verbatim to the launched ioSender.exe, e.g.
    #   .\build.ps1 -Launch -forgetnetwork -demomarker
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs
)

$ErrorActionPreference = 'Stop'

# Whether -Scratch was TYPED, as opposed to defaulted on below. The conflict guards further down
# ("-Shot runs the app; -Scratch is a verify-only build") must only fire on a real contradiction the
# caller wrote, never on the default this script applied for them.
$scratchExplicit = $PSBoundParameters.ContainsKey('Scratch')

$root = $PSScriptRoot
$solution = Join-Path $root 'ioSender XL\ioSender XL.sln'
$exeRel = 'ioSender XL\ioSender XL\bin\{0}\ioSender.exe'

function Find-MSBuild {
    # NOTE (learned 2026-08-07): every project in the .sln must stay legacy-style. Neither MSBuild on
    # this box can build an SDK-style net8.0 project (Build Tools lacks the Microsoft.DotNet
    # SdkResolver entirely; Enterprise is 17.6, which caps at .NET SDK 7). SDK-style projects live
    # OUTSIDE the solution and build with dotnet - the CNC.Core.net8 / CNC.Contracts.net8 probe pattern.
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

$templateCfg = Join-Path $root 'ioSender XL\ioSender XL\Default-App.config'

# Adopt a session's config as the shipped template (-AdoptConfig). Two things stand between the file
# ioSender writes and the file the repo wants, and both are silent if you just copy it:
#
#   1. The XML COMMENTS. ioSender composes the document from scratch on every save (ConfigStore.
#      WriteDocument), so it cannot preserve a comment it never read. They are read back out of the
#      EXISTING template here rather than hardcoded in this script - edit a comment in the template and
#      the next adoption keeps your edit. Each is anchored to the line that follows it (e.g.
#      '<section key="CustomTools">'), which is unique by construction; an anchor that has disappeared
#      is reported, never silently dropped.
#   2. ENCODING. ioSender writes UTF-8 WITH a BOM and CRLF; the repo is BOM-less LF (.gitattributes
#      'eol=lf'). Copy it raw and every line reads as changed, which buries whatever really changed.
function Convert-SessionConfigToTemplate {
    param([string]$SessionPath, [string]$TemplatePath)

    # ReadAllText detects and strips a BOM; normalise CRLF here so the whole comparison below is LF.
    $text = [System.IO.File]::ReadAllText($SessionPath) -replace "`r`n", "`n"
    $lines = [System.Collections.Generic.List[string]]($text -split "`n")

    $blocks = @()
    if (Test-Path $TemplatePath) {
        $tmplLines = ([System.IO.File]::ReadAllText($TemplatePath) -replace "`r`n", "`n") -split "`n"
        for ($i = 0; $i -lt $tmplLines.Count; $i++) {
            if ($tmplLines[$i] -notmatch '<!--') { continue }
            $start = $i
            while ($i -lt $tmplLines.Count -and $tmplLines[$i] -notmatch '-->') { $i++ }
            $end = [Math]::Min($i, $tmplLines.Count - 1)
            # Anchor: the next line with content. Comments are re-inserted BEFORE it.
            $j = $end + 1
            while ($j -lt $tmplLines.Count -and -not $tmplLines[$j].Trim()) { $j++ }
            if ($j -ge $tmplLines.Count) { continue }
            $blocks += [pscustomobject]@{ Anchor = $tmplLines[$j].Trim(); Lines = @($tmplLines[$start..$end]) }
        }
    }

    # Bottom-up: inserting shifts every later index, and the anchors are found by value anyway, but
    # re-finding after each insert keeps this correct regardless of order.
    $missed = @()
    foreach ($b in $blocks) {
        $at = -1
        for ($k = 0; $k -lt $lines.Count; $k++) {
            if ($lines[$k].Trim() -eq $b.Anchor) { $at = $k; break }
        }
        if ($at -lt 0) { $missed += $b.Anchor; continue }
        $lines.InsertRange($at, [string[]]$b.Lines)
    }

    $out = ($lines -join "`n")
    [System.IO.File]::WriteAllText($TemplatePath, $out, (New-Object System.Text.UTF8Encoding($false)))

    [pscustomobject]@{ Adopted = $blocks.Count - $missed.Count; Missed = $missed }
}

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

if ($Shot -and $Scratch) { throw "-Shot runs the app; -Scratch is a verify-only build. Pass one." }
if ($Shot) { $shotBaseline = Get-NewestScreenshotTime }

if ($AdoptConfig) { $DefaultConfig = $true }   # adopting the result presupposes producing one

# -ReviewConfig: seed a throwaway config folder from the SHIPPED template and point ioSender at it with
# -configpath. Deliberately NOT a -DefaultConfig session: that one stashes the real App.config and has to
# put it back, which is a risk worth taking to EDIT the default but pointless just to LOOK at it. Here the
# real config is never moved at all, so there is nothing to restore and nothing to lose if this dies.
$reviewCfgDir = Join-Path $env:TEMP 'ioSender-default-review'
if ($ReviewConfig) {
    if ($DefaultConfig) { throw "-ReviewConfig looks at the template; -DefaultConfig/-adoptConfig edit it. Pass one." }
    if ($Scratch)       { throw "-ReviewConfig runs the app; -Scratch is a verify-only build. Pass one." }
    if (-not (Test-Path $templateCfg)) { throw "No template to review: $templateCfg" }

    $Launch = $true
    New-Item -ItemType Directory -Force -Path $reviewCfgDir | Out-Null
    # Wipe first: a leftover App.config from a previous review would be loaded INSTEAD of the template
    # (ioSender only seeds when none is present), so the run would quietly show you the last review's
    # edits and call them the default.
    Get-ChildItem $reviewCfgDir -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Copy-Item $templateCfg (Join-Path $reviewCfgDir 'App.config') -Force

    if ($AppArgs -notcontains '-forgetnetwork') { $AppArgs += '-forgetnetwork' }
    $AppArgs += '-configpath'
    $AppArgs += $reviewCfgDir
    Write-Host "==> Reviewing the shipped template on a throwaway config: $reviewCfgDir" -ForegroundColor Cyan
}

if ($DefaultConfig) {
    if ($Scratch) { throw "-DefaultConfig runs the app; -Scratch is a verify-only build. Pass one." }
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

# Scratch is the DEFAULT. Only a build that is going to RUN the app (or -Clean, which deletes the live
# bin\ tree and would otherwise leave it empty) touches bin\<Configuration>\ and kills what's running.
#
# It used to be opt-in, and the failure mode was entirely one-sided: forgetting -Scratch on an interim
# compile-check silently killed the operator's running instance and replaced the binaries underneath the
# very run being diagnosed - so the next log came from a different build than the symptom did. Forgetting
# -Launch, by contrast, costs a re-run. Defaulting to the harmless one makes the dangerous case the
# explicit one. Resolved HERE, after -ReviewConfig/-AdoptConfig have applied their implications
# (-Launch / -DefaultConfig respectively), so those are seen.
if (-not $scratchExplicit) {
    $Scratch = -not ($Launch -or $DefaultConfig -or $Shot -or $Clean)
    if ($Scratch) {
        Write-Host "==> Verify-only build (no -Launch): building to bin\$Configuration.scratch\ - a running instance is left alone." -ForegroundColor DarkGray
        Write-Host "    Pass -Launch to build bin\$Configuration\ and start it." -ForegroundColor DarkGray
    }
}

# Ask a running instance to close itself over the single-instance pipe (PipeServer.ShutdownRequested,
# added 2026-08-08) before ever force-killing it - a blind Stop-Process is exactly how a rebuild killed
# a live job out from under the operator mid-jog-test the same session this was added in. Idle: closes
# in well under a second. Busy: the app itself watches for the job to finish and closes then, up to
# TimeoutSeconds - it NEVER force-closes past that on its own, so if it's still running when our own
# poll gives up, that is a real "still busy" answer, not a fluke, and this function must not paper over
# it by falling through to a kill.
#
# Falls back to the OLD blind kill ONLY when the pipe can't be reached at all - no instance running, or
# a running instance from a build that predates this feature and was never asked (its window still owns
# the pipe name, so Connect succeeds, but it never wired ShutdownRequested and won't act on the line -
# request will simply produce no response). That's a real, if narrow, gap: an unresponsive OLD binary
# would silently swallow the request and then get killed by the fallback below exactly like today,
# instead of being recognized as "didn't understand, don't know if it's safe". A rebuild replaces the
# binary going forward, so this only bites once per machine/first upgrade.
function Request-IoSenderShutdown {
    param([int]$TimeoutSeconds = 60)

    if (-not (Get-Process ioSender -ErrorAction SilentlyContinue)) { return $true }   # nothing to ask

    $asked = $false
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ioSender', [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect(250)
        $writer = New-Object System.IO.StreamWriter($pipe)
        $writer.WriteLine("#SHUTDOWN:$TimeoutSeconds#")
        $writer.Flush()
        $writer.Dispose()
        $pipe.Dispose()
        $asked = $true
    } catch { }

    if (-not $asked) { return $false }   # couldn't even reach the pipe - caller falls back to a kill

    # Small buffer past the app's own window: it closes the instant JobRunning clears, this is just
    # polling for that externally, not a second independent timeout.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds + 5)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process ioSender -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false   # asked, and it's STILL busy - do not proceed to a kill
}

# A scratch build never touches bin\<Config>\ at all, so there's normally nothing to kill - but
# -Clean deletes the LIVE bin\ tree too (not just scratch's side folder), so a running instance's
# locked DLLs must go first regardless of -Scratch, or the delete below fails with Access to the
# path ... is denied (confirmed 2026-07-31).
if (-not $NoKill -and (-not $Scratch -or $Clean)) {
    if (-not (Request-IoSenderShutdown -TimeoutSeconds 60)) {
        if (Get-Process ioSender -ErrorAction SilentlyContinue) {
            throw "ioSender asked to close gracefully (a job may be running) and is still up after 60s - refusing to force-kill it. Let the job finish or close it yourself, then re-run."
        }
        # else: pipe unreachable (no instance, or a pre-shutdown-feature build) - fall through to the kill below.
    }
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

            # A launch leaves the connection alone entirely - the app uses whatever it is configured
            # for, which is what you want when testing the sender itself. Pass -simulator explicitly
            # to aim a run at the simulator.

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

            if ($DefaultConfig -or $Shot) {
                # Quitting the app IS the end of the session - block here so the restore (and/or the
                # screenshot filing) below runs the moment ioSender exits, with no second command to
                # remember. -Shot alone waits too: some screens can only be shot from YOUR config -
                # a fixture, for one, is a validated known position and so never exists on a default
                # config - and those runs still need the capture filed when you quit.
                if ($DefaultConfig) { Write-Host "==> Running on a default config. Quit ioSender to end the session and get your own settings back." -ForegroundColor Cyan }
                else { Write-Host "==> Waiting for you to quit ioSender, then filing the screenshot." -ForegroundColor Cyan }
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

        # -AdoptConfig: write it over the shipped template, comments and encoding restored. Deliberately
        # AFTER the scrub and the restore, so a failure here can never cost the real settings. The result
        # is a tracked file, so the diff is the review and 'git checkout' is the undo.
        if ($AdoptConfig -and (Test-Path $sessionCfg)) {
            try {
                $r = Convert-SessionConfigToTemplate -SessionPath $sessionCfg -TemplatePath $templateCfg
                Write-Host "==> Adopted into Default-App.config ($($r.Adopted) comment block(s) re-injected)." -ForegroundColor Green
                foreach ($m in $r.Missed) {
                    Write-Host "==> Comment DROPPED - no line matching '$m' in the new config. Re-add it by hand." -ForegroundColor Yellow
                }
                & git -C $root --no-pager diff --stat -- 'ioSender XL/ioSender XL/Default-App.config'
                Write-Host "==> Review with: git diff -- 'ioSender XL/ioSender XL/Default-App.config'" -ForegroundColor Cyan
            }
            catch {
                Write-Host "==> Adoption FAILED ($($_.Exception.Message)) - the template is untouched; the session config is still at $sessionCfg." -ForegroundColor Red
            }
        }
    }

    # Outside the restore block on purpose: -Shot works with or without -DefaultConfig, and files
    # only a capture taken after the launch - see -Shot's help.
    if ($Shot) {
        if ((Get-NewestScreenshotTime) -gt $shotBaseline) {
            & (Join-Path $root 'tools\copy-latest-screenshot.ps1') -Name $Shot
        }
        else {
            Write-Host "==> No screenshot taken this run - docs\manual\img\$Shot is unchanged." -ForegroundColor Yellow
        }
    }
}
