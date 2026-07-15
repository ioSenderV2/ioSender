This folder (next to the ioSender executable) is where ioSender keeps a grblHAL simulator that
**matches the currently-connected controller's compile options** - automatically, no user action
needed. Nothing here is committed to the repository except this file; everything else
(`grblHAL_sim.exe`, cached `grblHAL_sim-<sig>.exe` copies, the build-signature marker,
`MyMachine.DAT`, `sim_setup.cfg`) is generated/downloaded at runtime and gitignored.

**How it's kept in sync (`SimulatorManager.EnsureMatchedSimulator`, called on every real-controller
connect):** the connected controller's options (axis count, probe, WCS rotation, ...) are mapped to a
short signature. If the simulator here doesn't already match it, ioSender fetches a prebuilt
`sim-<signature>` release from the shared cache, or - if none exists yet - dispatches a parameterized
build on GitHub Actions (`build-matched-sim` workflow, `ioSenderV2/Simulator`) and installs the result
once it's ready. This only runs against a real controller connection; it's skipped entirely when
already talking to a simulator.

**This is not where a user picks options by hand.** For that, use **Settings > Simulator** in the
app (or, once a real machine is fully set up, Machine Setup Wizard's last step - "Build simulator
matching this machine", one button, no picks): choose axes/probe/rotation/lathe-UVW/safety-door/
e-stop/ganged+auto-square-Y and click Build. Reuses the same CI workflow and release cache as above,
but always installs to **`%AppData%\ioSender\Simulator`** instead of this app-relative folder -
independent of the auto-matched flow, and writable without elevation even under an all-users (e.g.
Program Files) install. The Connect dialog's *Simulator* tab is only enabled once
`%AppData%\ioSender\Simulator\grblHAL_sim.exe` exists, and launches it (if not already running) on Ok.
A `sim-options.json` next to that exe records the picks it was last built with (and `EEPROM.DAT`/
littlefs the connected machine's copied settings + ATC macros), so the Settings tab can restore them
across sessions.
