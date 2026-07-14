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
```

**Flashing: GUI only, not `-t upload`.** `upload_protocol = teensy-cli` is configured but this box's
`tool-teensy` package only ships the GUI loader (`teensy.exe`) — no `teensy_loader_cli.exe` anywhere on
the system, so `-t upload` fails with "not recognized". Flash manually: open the Teensy Loader GUI app,
point it at `firmware.hex` (path below), press the button on the Teensy. Don't re-attempt `-t upload`
expecting it to suddenly work — it's a missing-tool gap, not a fluke.

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
