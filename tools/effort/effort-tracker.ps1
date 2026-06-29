<#
.SYNOPSIS
  Logs active work sessions based on real keyboard/mouse activity (Win32 GetLastInputInfo).

.DESCRIPTION
  A "session" is a stretch of activity at the computer. A gap of >= IdleGapMinutes with NO keyboard or
  mouse input ends the current session; the next input starts a new one. Each completed session is
  appended to SessionsCsv (start, end, minutes). The in-progress session is mirrored to a heartbeat file
  every poll, so a crash / reboot / Ctrl+C still captures it (it's finalised on the next run).

  Run it in the background (e.g. at login) and leave it. It does not care which app is focused - any
  input counts as "working" (ioSender, Fusion, the editor, this CLI, ...).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\effort\effort-tracker.ps1
  # or with a longer gap / different log:
  ... effort-tracker.ps1 -IdleGapMinutes 5 -PollSeconds 20 -SessionsCsv C:\path\sessions.csv
#>
param(
    [int]$IdleGapMinutes = 5,
    [int]$PollSeconds = 20,
    [string]$SessionsCsv = "$PSScriptRoot\sessions.csv"
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class IdleInfo {
    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    public static uint IdleMs() {
        var lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(lii);
        GetLastInputInfo(ref lii);
        return (uint)Environment.TickCount - lii.dwTime;   // unsigned subtraction handles the 49-day wrap
    }
}
"@

$gapMs        = $IdleGapMinutes * 60 * 1000
$heartbeat    = [System.IO.Path]::ChangeExtension($SessionsCsv, ".current")
$fmt          = "yyyy-MM-dd HH:mm:ss"

if (-not (Test-Path $SessionsCsv)) { "start,end,minutes" | Out-File -FilePath $SessionsCsv -Encoding utf8 }

function Save-Session([datetime]$start, [datetime]$end) {
    if ($null -eq $start -or $null -eq $end) { return }
    $mins = [math]::Round(($end - $start).TotalMinutes, 1)
    if ($mins -le 0) { return }
    ("{0},{1},{2}" -f $start.ToString($fmt), $end.ToString($fmt), $mins) | Add-Content -Path $SessionsCsv -Encoding utf8
    Write-Host ("logged  {0:HH:mm} - {1:HH:mm}  ({2} min)" -f $start, $end, $mins) -ForegroundColor Green
}

# Recover an in-progress session left by a previous run (crash / Ctrl+C / reboot).
if (Test-Path $heartbeat) {
    try {
        $hb = (Get-Content $heartbeat -Raw).Trim() -split ","
        Save-Session ([datetime]$hb[0]) ([datetime]$hb[1])
    } catch { }
    Remove-Item $heartbeat -ErrorAction SilentlyContinue
}

[datetime]$sessionStart = [datetime]::MinValue
[datetime]$lastActive   = [datetime]::MinValue
$inSession = $false

Write-Host ("effort-tracker: gap=${IdleGapMinutes}min poll=${PollSeconds}s -> $SessionsCsv") -ForegroundColor Cyan
Write-Host "Leave running. Ctrl+C to stop (current session is preserved)." -ForegroundColor DarkGray

try {
    while ($true) {
        $idle = [IdleInfo]::IdleMs()
        $now  = Get-Date

        if ($idle -lt $gapMs) {                                   # active
            if (-not $inSession) {
                $sessionStart = $now.AddMilliseconds(-$idle)      # back-date to the last actual input
                $inSession = $true
                Write-Host ("start   {0:HH:mm:ss}" -f $sessionStart) -ForegroundColor DarkCyan
            }
            $lastActive = $now
            ("{0},{1}" -f $sessionStart.ToString($fmt), $lastActive.ToString($fmt)) | Out-File $heartbeat -Encoding utf8
        }
        elseif ($inSession) {                                     # idle >= gap -> close the session
            Save-Session $sessionStart $lastActive
            Remove-Item $heartbeat -ErrorAction SilentlyContinue
            $inSession = $false
        }

        Start-Sleep -Seconds $PollSeconds
    }
}
finally {
    if ($inSession) { Save-Session $sessionStart $lastActive; Remove-Item $heartbeat -ErrorAction SilentlyContinue }
}
