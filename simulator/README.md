Place a prebuilt simulator executable named `simulator.exe` (or `grblHAL_sim.exe`) in the application folder or inside the installer .zip so the installer puts it next to the ioSender executable.

If the user selects the "Simulator" tab in the connection dialog and checks "Start simulator executable (if available)", ioSender will attempt to start `simulator.exe` from the application directory and then connect to `127.0.0.1:<port>` (default port 23).

This repository does not include a binary; include a prebuilt simulator from the grblHAL Simulator build artifacts when creating an installer zip.