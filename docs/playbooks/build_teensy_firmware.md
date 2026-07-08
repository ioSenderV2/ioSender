# Build the grblHAL Teensy 4.1 firmware

**When:** building/flashing the iMXRT1062 (Teensy 4.x) firmware.
**Repo:** `c:\github\iMXRT1062`. **Toolchain:** PlatformIO CLI.
**Memory context:** `firmware-build-teensy.md`, `firmware-fork-pr-model.md`.

`pio.exe` is installed (bundled by the VSCode PlatformIO extension) but **not on PATH**:
`C:\Users\steve\.platformio\penv\Scripts\pio.exe` (call by full path).

## Ready commands

```powershell
$pio = "C:\Users\steve\.platformio\penv\Scripts\pio.exe"
& $pio run -d "C:\github\iMXRT1062\grblHAL_Teensy4" -e teensy41           # build
& $pio run -d "C:\github\iMXRT1062\grblHAL_Teensy4" -e teensy41 -t upload # build + flash (Teensy connected)
```

## Notes

- `platformio.ini` is in the **`grblHAL_Teensy4\`** subdir, not the repo root. Env = **`teensy41`**
  (default; `teensy40` also exists).
- Output: `grblHAL_Teensy4\.pio\build\teensy41\firmware.hex` (+ `firmware.elf`).
- Canonical build branch = `stevenrwood/iMXRT1062 @ srw/local-build-config` (carries `my_machine.h`,
  build-stamp pre-script, and the grbl `srw/combined` overlay with the WCS/G53-rotation fixes).
- First build downloads the platform + ARM toolchain + lib_deps (~3.5 min); rebuilds are fast.
- **Identify firmware on a board:** run `$I`. A `[BUILD:...]` line (gated on `build_stamp.h`) means it
  was built from `srw/combined`; no `[BUILD:]` line ⇒ other/older firmware. `NEWOPT:` lists the feature set.
- CI: `.github/workflows/firmware.yml` builds it on push to `srw/local-build-config` / manual dispatch;
  submodules are fork-pinned so a plain recursive checkout builds the SRW firmware natively.
