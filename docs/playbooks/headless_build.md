# Headless build

**When:** build (and optionally launch) without opening the Visual Studio GUI.
**Script:** `build.ps1` (repo root) — the ONE build entrypoint.
**Memory context:** `iosender-headless-build.md`, `iosender-build-setup.md`.

Kills a running ioSender.exe, builds via MSBuild (found through `vswhere`), optionally launches.
The `.vscode/tasks.json` tasks all delegate to this (Ctrl+Shift+B = *Build Debug + Launch*).

## Ready commands

```powershell
.\build.ps1 -Launch                     # kill, Debug build, launch — the standard "go test it"
.\build.ps1 -Configuration Both         # verify Debug + Release both clean (pre-commit)
.\build.ps1 -Configuration Release      # Release only
.\build.ps1 -Launch -demomarker -forgetnetwork   # trailing tokens forward to ioSender.exe
```

## Parameters

- `-Configuration Debug|Release|Both` — default Debug. `Both` fails if either config fails.
- `-Launch` — start the built exe after a successful build (ignored for `Both`).
- `-NoKill` — skip killing a running ioSender.exe first.
- `-Headless` — launch with `IOSENDER_HEADLESS=1` so an unhandled exception dumps to
  `%AppData%\ioSender\ioSender.crash.log` and exits `0xFA11` (64017) instead of blocking on a modal
  dialog. Use for unattended runs; omit for interactive testing.
- Trailing tokens after the known params forward to ioSender.exe (e.g. `-demomarker`, `-debuglog`).

## Notes

- Only `ioSender XL\ioSender XL.sln` is supported (plain ioSender removed 2026-06-29). Output exe is
  still `ioSender.exe`. .NET Framework 4.6.2, WPF — Windows only, no `dotnet build`.
