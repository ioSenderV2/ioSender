This folder is where ioSender looks for the bundled grblHAL simulator (`grblHAL_sim.exe`),
next to the application executable. **No binary is committed to this repository.**

There are two ways the executable gets here:

1. **Download from the connection dialog (recommended).** When `grblHAL_sim.exe` is not
   found, the *Simulator* tab shows a **Download** button. It posts the build definition in
   `sim-build.json` to the grblHAL Web Builder, then extracts `grblHAL_sim.exe` from the
   returned archive into this folder. To change the simulator's compiled feature set, export a
   new "Save selection" JSON from the web builder (Simulator driver, Windows board) and replace
   `sim-build.json`.

2. **Place it manually.** Drop a prebuilt `grblHAL_sim.exe` here (or next to the ioSender
   executable), e.g. from the grblHAL Simulator build artifacts or your own build.

Once present, the *Simulator* tab can start it and connect to `127.0.0.1:<port>` (default 23).
Per-machine settings are supplied at runtime via the EEPROM image (`-e`), not baked into the
build, so the same downloaded executable works for any machine.
