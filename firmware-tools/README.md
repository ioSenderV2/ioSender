`teensy_loader_cli.exe` here is PJRC's HalfKay bootloader uploader, used by Machine Setup's Machine
tab (Update firmware) to flash a downloaded `.hex` onto the connected Teensy 4.x board headlessly - no
GUI interaction, no manual bootloader-button press (`-s` triggers the board's own 134-baud soft-reboot
trick over its existing serial port).

Unlike `simulator/` (variable per-controller-config binaries, fetched at runtime and gitignored), this
tool is a single fixed ~27 KB binary with no moving parts, so it **is committed** here directly rather
than downloaded on demand. `LICENSE-teensy_loader_cli.txt` is upstream's GPLv3.

**Provenance / rebuilding:** vendored source lives in `tools/teensy_loader_cli/` (unmodified from
[PaulStoffregen/teensy_loader_cli](https://github.com/PaulStoffregen/teensy_loader_cli) except one
include-path patch for this MinGW-w64 distribution - see the `PATCHED` comment in
`teensy_loader_cli.c`). Run `tools/teensy_loader_cli/build.ps1` (needs `gcc.exe` on PATH) to rebuild
and re-drop the exe here.
