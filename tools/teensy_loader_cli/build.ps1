# Build teensy_loader_cli.exe (PJRC's HalfKay bootloader uploader) for Windows x64 with MinGW-w64 gcc,
# and drop it into ../../firmware-tools/ where ioSender's csproj bundles it into the app output.
#
# Source is vendored here unmodified except one include-path patch (see the "PATCHED" comment in
# teensy_loader_cli.c) needed because this MinGW-w64 distribution ships hidsdi.h/hidclass.h at the
# top-level include path, not under the legacy ddk/ subdirectory the upstream Makefile's mingw32msvc
# cross-compiler expected. No functional change.
#
# Requires gcc on PATH (or edit $gcc below). Re-run whenever teensy_loader_cli.c is updated from upstream
# (https://github.com/PaulStoffregen/teensy_loader_cli).

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$gcc = (Get-Command gcc.exe -ErrorAction SilentlyContinue).Source
if (-not $gcc) { throw "gcc.exe not found on PATH - install MinGW-w64 (e.g. via WinLibs) first." }

$out = Join-Path $here "..\..\firmware-tools\teensy_loader_cli.exe"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

& $gcc -O2 -Wall -s -DUSE_WIN32 -o $out (Join-Path $here "teensy_loader_cli.c") -lhid -lsetupapi -lwinmm
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Copy-Item (Join-Path $here "LICENSE") (Join-Path (Split-Path $out) "LICENSE-teensy_loader_cli.txt") -Force

Write-Host "Built $out"
