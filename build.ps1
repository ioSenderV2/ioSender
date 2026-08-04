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
    Start a "default config session": move your own %AppData%\ioSender\App.config aside, then
    build and launch. ioSender's own fresh-install path (AppConfig.SeedUserConfigDir) sees no
    App.config and seeds one by copying the shipped Default-App.config, so the run is exactly
    what a brand-new install gets - the right state for default-matching screenshots, and for
    arranging the layout you want the template to ship with. This script never copies the
    template itself; it only stashes and restores. End the session with -EndDefaultConfig.

.PARAMETER EndDefaultConfig
    End the session: kill the app (so its final write lands), park the arranged config, print a
    section-level comparison against the shipped template, and move your own App.config back.
    Nothing is written to the repo unless you also pass -Adopt. The arranged file is kept, so
    you can adopt it afterwards without redoing the session.

.PARAMETER Adopt
    With -EndDefaultConfig: copy the arranged config over the repo's
    ioSender XL\ioSender XL\Default-App.config before restoring your own. The template's XML
    comments (the file header and the CustomTools note) are re-injected afterwards, because
    ioSender composes the document from scratch on save and drops them. Review with git diff
    before committing - anything you touched in the session lands in the shipped default.

.EXAMPLE
    .\build.ps1 -Clean -Launch
    Wipe every project's bin\/obj\ first, then build and launch - use after a build fails
    referencing a file that was just deleted/renamed (stale incremental cache), not routinely.

.EXAMPLE
    .\build.ps1 -DefaultConfig -Launch
    Stash your App.config, build, launch a first-run-clean ioSender. Arrange the layout, quit.

.EXAMPLE
    .\build.ps1 -EndDefaultConfig -Adopt
    Take what you arranged, write it into the repo template, and restore your own config.
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
    [switch]$DefaultConfig,
    [switch]$EndDefaultConfig,
    [switch]$Adopt,
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
# Default-config session. The point: see (and shape) what a brand-new install gets. This script
# NEVER copies the template into %AppData% - it only moves your own App.config out of the way,
# and ioSender's own first-run path (AppConfig.SeedUserConfigDir) does the seeding, so the run
# exercises the real fresh-install behaviour rather than an imitation of it.
# ---------------------------------------------------------------------------------------------
$userCfgDir  = Join-Path $env:APPDATA 'ioSender'
$liveCfg     = Join-Path $userCfgDir 'App.config'
$sessionDir  = Join-Path $userCfgDir '_default-config-session'
$stashedCfg  = Join-Path $sessionDir 'App.config.mine'       # yours, parked for the session
$arrangedCfg = Join-Path $sessionDir 'App.config.arranged'   # what the session produced
$templateCfg = Join-Path $root 'ioSender XL\ioSender XL\Default-App.config'
$sessionActive = Test-Path $stashedCfg

function Stop-IoSenderAndWait {
    # The running instance rewrites App.config as it exits, so every file swap below has to wait
    # for the process to be GONE, not merely signalled - otherwise its dying write lands on top
    # of whichever file we just moved into place.
    $procs = @(Get-Process ioSender -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }
    $procs | Stop-Process -Force
    try { $procs | Wait-Process -Timeout 15 -ErrorAction Stop } catch { }
}

function Get-ConfigSections([string]$path) {
    # key -> that section's XML, whitespace-normalised so indentation changes don't read as edits.
    $map = @{}
    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $false
    $doc.Load($path)
    foreach ($n in $doc.DocumentElement.SelectNodes('section')) { $map[$n.GetAttribute('key')] = $n.OuterXml }
    return $map
}

function Show-ConfigComparison([string]$arranged, [string]$template) {
    $a = Get-ConfigSections $arranged
    $t = Get-ConfigSections $template
    $keys = @($a.Keys) + @($t.Keys) | Sort-Object -Unique
    Write-Host "==> Arranged config vs shipped template, by section:" -ForegroundColor Cyan
    $changes = 0
    foreach ($k in $keys) {
        if (-not $t.ContainsKey($k))      { Write-Host ("    {0,-22} NEW      (absent from the template)" -f $k) -ForegroundColor Yellow; $changes++ }
        elseif (-not $a.ContainsKey($k))  { Write-Host ("    {0,-22} REMOVED" -f $k) -ForegroundColor Yellow; $changes++ }
        elseif ($a[$k] -ne $t[$k])        { Write-Host ("    {0,-22} changed" -f $k) -ForegroundColor Yellow; $changes++ }
        else                              { Write-Host ("    {0,-22} same" -f $k) -ForegroundColor DarkGray }
    }
    if ($changes -eq 0) { Write-Host "    (identical - nothing to adopt)" -ForegroundColor DarkGray }
    return $changes
}

function Restore-TemplateComments([string]$newFile, [string]$oldTemplate) {
    # ioSender composes App.config from scratch (ConfigStore.WriteDocument), so an adopted file
    # arrives with none of the template's prose - the file header explaining what this file IS,
    # and the CustomTools note recording why ids 0-13 must not move. Both are load-bearing for
    # the next person reading it, so put them back: document-level comments at the top, and any
    # comment that preceded a <section> re-attached to that same section by key.
    # Anchors are found with the DOM (reliable), but the splice itself is textual: XmlDocument
    # refuses a newly created whitespace node at document level, and re-saving the whole document
    # would reflow it - a text splice keeps every comment's own indentation and touches nothing else.
    try {
        $old = New-Object System.Xml.XmlDocument; $old.PreserveWhitespace = $true; $old.Load($oldTemplate)
        $text = [System.IO.File]::ReadAllText($newFile)
        $rootTag = '<' + $old.DocumentElement.Name
        $orphans = @()

        foreach ($c in @($old.DocumentElement.ChildNodes | Where-Object { $_.NodeType -eq 'Comment' })) {
            $anchor = $c.NextSibling
            while ($anchor -and $anchor.NodeType -ne 'Element') { $anchor = $anchor.NextSibling }
            $key = if ($anchor) { $anchor.GetAttribute('key') } else { $null }
            $m = if ($key) { [regex]::Match($text, "(?m)^([ \t]*)<section key=""$([regex]::Escape($key))""") } else { $null }
            if ($m -and $m.Success) {
                $indent = $m.Groups[1].Value
                $text = $text.Remove($m.Index, 0).Insert($m.Index, $indent + $c.OuterXml + "`n")
            }
            else { $orphans += ($c.Value -split "`n")[0].Trim() }
        }

        # Document-level comments (the file header) go immediately above the root element.
        foreach ($c in @($old.ChildNodes | Where-Object { $_.NodeType -eq 'Comment' })) {
            $i = $text.IndexOf($rootTag)
            if ($i -ge 0) { $text = $text.Insert($i, $c.OuterXml + "`n") }
            else { $orphans += ($c.Value -split "`n")[0].Trim() }
        }

        [System.IO.File]::WriteAllText($newFile, $text)
        if ($orphans.Count) {
            Write-Host "==> Could not re-attach $($orphans.Count) template comment(s) - the section they described is gone:" -ForegroundColor Yellow
            $orphans | ForEach-Object { Write-Host "      $_" -ForegroundColor Yellow }
        }
    }
    catch {
        Write-Host "==> Comments could not be re-injected ($($_.Exception.Message)) - check git diff before committing." -ForegroundColor Yellow
    }
}

if ($DefaultConfig -and $EndDefaultConfig) { throw "-DefaultConfig and -EndDefaultConfig are opposite ends of the same session; pass one." }
if ($Adopt -and -not $EndDefaultConfig)    { throw "-Adopt only means anything with -EndDefaultConfig (it adopts what that session produced)." }

if ($EndDefaultConfig) {
    if (-not $sessionActive) { throw "No default-config session is active (nothing stashed at $stashedCfg)." }
    Stop-IoSenderAndWait

    if (Test-Path $liveCfg) {
        Copy-Item $liveCfg $arrangedCfg -Force
        Write-Host "==> Session config parked: $arrangedCfg" -ForegroundColor Cyan
        $changed = Show-ConfigComparison $arrangedCfg $templateCfg

        if ($Adopt) {
            if ($changed -eq 0) { Write-Host "==> Nothing differs; template left alone." -ForegroundColor Yellow }
            else {
                # Keep the outgoing template aside first: its comments are the source for the
                # re-injection below, and overwriting it in place would destroy them.
                $priorTemplate = Join-Path $sessionDir 'Default-App.config.prior'
                Copy-Item $templateCfg $priorTemplate -Force
                Copy-Item $arrangedCfg $templateCfg -Force
                Restore-TemplateComments $templateCfg $priorTemplate
                Write-Host "==> Adopted into $templateCfg - review with git diff before committing." -ForegroundColor Green
            }
        }
        elseif ($changed -gt 0) {
            Write-Host "==> Not adopted. Re-run with -EndDefaultConfig -Adopt to write it into the template." -ForegroundColor Yellow
        }
    }
    else { Write-Host "==> No App.config was produced this session - nothing to compare." -ForegroundColor Yellow }

    Move-Item $stashedCfg $liveCfg -Force
    Write-Host "==> Your own App.config restored." -ForegroundColor Green
    return
}

if ($DefaultConfig) {
    if ($sessionActive) { throw "A default-config session is already active (yours is stashed at $stashedCfg). End it with -EndDefaultConfig first." }
    Stop-IoSenderAndWait
    New-Item -ItemType Directory -Force -Path $sessionDir | Out-Null
    if (Test-Path $liveCfg) {
        Move-Item $liveCfg $stashedCfg -Force
        Write-Host "==> Your App.config stashed: $stashedCfg" -ForegroundColor Cyan
    }
    else {
        # Nothing to stash, but the session still needs its marker so -EndDefaultConfig has
        # something to end and no later run mistakes this for a normal one.
        New-Item -ItemType File -Path $stashedCfg | Out-Null
        Write-Host "==> No existing App.config to stash (marker written)." -ForegroundColor Yellow
    }
    Write-Host "==> ioSender will seed a fresh config from Default-App.config on launch." -ForegroundColor Cyan
}
elseif ($sessionActive) {
    Write-Host "==> DEFAULT-CONFIG SESSION ACTIVE - this instance is NOT using your own settings." -ForegroundColor Yellow
    Write-Host "    Yours is stashed at $stashedCfg (restore with -EndDefaultConfig)." -ForegroundColor Yellow
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
