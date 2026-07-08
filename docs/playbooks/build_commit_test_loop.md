# Build / commit / test loop

**When:** every set of ioSender edits. The user follows along and tests as you go.
**Memory context:** `iosender-build-commit-workflow.md`.

Order matters — **debug+launch BEFORE release/commit**, so the user's testing overlaps your
release-verify + commit (no idle waiting).

## The loop

1. **Kill any running `ioSender.exe` first** — else the build fails with file-locked DLL copy errors
   (MSB3021/MSB3027).
2. **Test build = DEBUG, then LAUNCH.** The window opening is the user's cue to start testing.
3. **While the user tests:** do a **Release** build to confirm it's clean, then **commit**, then
   **write the summary + "what to test"**.
4. By the time the summary is out, the user is back with feedback.

## Ready commands

```powershell
# 1+2 (kill, debug build, launch) — one shot via build.ps1:
.\build.ps1 -Launch

# 3 (pre-commit verify both configs):
.\build.ps1 -Configuration Both
```

Manual kill, if needed: `Get-Process ioSender -ErrorAction SilentlyContinue | Stop-Process -Force`

## Notes

- `build.ps1` finds MSBuild via vswhere; raw MSBuild path on this box is
  `C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe`.
- See [headless_build.md](headless_build.md) for all build.ps1 params.
- **Never `msbuild /t:Rebuild`** — sibling HintPath DLL folders don't exist here, so a clean can't
  re-resolve `RP.Math`/`websocket-sharp`. Use `/t:Build`.
