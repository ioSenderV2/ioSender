<#
.SYNOPSIS
    Kill / (re)launch / status the already-built ioSender.exe - no rebuild.

.DESCRIPTION
    build.ps1 is the build[+launch] loop; this is its runtime complement for the
    debug/test cycle where the binary is already built and you just need to bounce
    it. It kills BOTH ioSender.exe and the AppLaunch supervisor (build.ps1 only
    kills ioSender), relaunches the existing exe with the usual test flags, polls
    to confirm it came up, and surfaces the crash/exit files the app drops.

    Consolidates the hand-rolled "Get-Process ioSender,AppLaunch | Stop-Process;
    Start-Sleep; Start-Process ...; Get-Process ..." one-liners (64 of them in the
    last week of transcripts) into one wrapper.

.PARAMETER Configuration
    Debug (default) or Release - which bin\ exe to launch.

.PARAMETER TestServer
    Launch with the UI test server enabled (-testserver). Combine with -Port.

.PARAMETER Port
    Test-server port (implies -TestServer). Default 0 = use the app default (8760).

.PARAMETER Headless
    Set IOSENDER_HEADLESS=1 so a crash dumps to the log + exits 0xFA11 instead of
    blocking on a modal dialog. Omit for interactive testing.

.PARAMETER KillOnly
    Kill ioSender + AppLaunch and stop - do not launch.

.PARAMETER Status
    Do not kill or launch: just report running PIDs + the last exit.json / crash.log.

.PARAMETER NoKill
    Launch without killing a running instance first.

.EXAMPLE
    tools\run-iosender.ps1 -TestServer
    Bounce the Debug build with the UI test server on the default port.

.EXAMPLE
    tools\run-iosender.ps1 -Status
    Just show whether it's running and why it last exited.

.EXAMPLE
    tools\run-iosender.ps1 -KillOnly
    Kill ioSender + AppLaunch and leave it dead.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$TestServer,
    [int]$Port = 0,
    [switch]$Headless,
    [switch]$KillOnly,
    [switch]$Status,
    [switch]$NoKill,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent          # tools\.. = repo root
$exe = Join-Path $root ("ioSender XL\ioSender XL\bin\{0}\ioSender.exe" -f $Configuration)
$appData = Join-Path $env:APPDATA 'ioSender'
$crashLog = Join-Path $appData 'ioSender.crash.log'
$exitJson = Join-Path $appData 'ioSender.exit.json'

function Show-Status {
    $procs = Get-Process ioSender, AppLaunch -ErrorAction SilentlyContinue
    if ($procs) {
        Write-Host "==> running:" -ForegroundColor Green
        $procs | Select-Object Name, Id, @{n = 'RAM(MB)'; e = { [int]($_.WorkingSet64 / 1MB) } } | Format-Table -AutoSize | Out-String | Write-Host
    }
    else {
        Write-Host "==> not running." -ForegroundColor Yellow
    }
    if (Test-Path $exitJson) {
        Write-Host "==> last exit ($exitJson):" -ForegroundColor Cyan
        Get-Content $exitJson -Raw | Write-Host
    }
    if (Test-Path $crashLog) {
        $age = (Get-Date) - (Get-Item $crashLog).LastWriteTime
        Write-Host ("==> crash.log present (updated {0:N0} min ago), last entry:" -f $age.TotalMinutes) -ForegroundColor Red
        Get-Content $crashLog -Tail 12 | Write-Host
    }
}

if ($Status) { Show-Status; exit 0 }

if (-not $NoKill) {
    $killed = Get-Process ioSender, AppLaunch -ErrorAction SilentlyContinue
    if ($killed) {
        $killed | Stop-Process -Force
        Start-Sleep -Milliseconds 800
        Write-Host "==> killed: $(( $killed | ForEach-Object { "$($_.Name)($($_.Id))" }) -join ', ')" -ForegroundColor DarkYellow
    }
    else {
        Write-Host "==> nothing to kill." -ForegroundColor DarkGray
    }
}

if ($KillOnly) { exit 0 }

if (-not (Test-Path $exe)) {
    Write-Host "==> built exe not found: $exe  (run build.ps1 first)" -ForegroundColor Red
    exit 1
}

$launchArgs = @()
if ($TestServer -or $Port -gt 0) {
    if ($Port -gt 0) { $launchArgs += "-testserver=$Port" } else { $launchArgs += '-testserver' }
}
if ($AppArgs) { $launchArgs += $AppArgs }

if ($Headless) { $env:IOSENDER_HEADLESS = '1' }
else { Remove-Item Env:\IOSENDER_HEADLESS -ErrorAction SilentlyContinue }

$argMsg = ''
if ($launchArgs) { $argMsg = " $($launchArgs -join ' ')" }
Write-Host "==> launching $Configuration ioSender$argMsg ..." -ForegroundColor Cyan
if ($launchArgs) { Start-Process $exe -ArgumentList $launchArgs } else { Start-Process $exe }

# poll up to ~5s for it to come up
$up = $null
for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Milliseconds 500
    $up = Get-Process ioSender -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($up) { break }
}
if ($up) {
    Write-Host "==> up: ioSender PID $($up.Id)" -ForegroundColor Green
    exit 0
}
else {
    Write-Host "==> did NOT come up within 5s - checking exit/crash files:" -ForegroundColor Red
    Show-Status
    exit 1
}
