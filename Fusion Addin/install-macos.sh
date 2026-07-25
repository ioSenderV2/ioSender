#!/bin/bash
#
# Installs the ioSenderV2 Fusion 360 add-in for the current user by copying it
# into Fusion's AddIns folder, where Fusion auto-discovers add-ins.
#
# ioSenderV2 itself is Windows-only, so there is no in-app installer to
# supersede this on macOS - it remains the only install path for a Mac-side
# Fusion 360 install.
#
# Run:
#     chmod +x install-macos.sh && ./install-macos.sh
#
# After installing you must enable it ONCE in Fusion (it cannot be auto-run
# from outside Fusion):
#     Utilities > ADD-INS > Scripts and Add-Ins (Shift+S) > Add-Ins tab >
#     select "ioSenderV2" > Run  (tick "Run on Startup" to keep it).

set -e

SRC="$(cd "$(dirname "$0")" && pwd)/ioSenderV2"
ADDINS="$HOME/Library/Application Support/Autodesk/Autodesk Fusion 360/API/AddIns"

if [ ! -d "$SRC" ]; then
    echo "Add-in source folder not found: $SRC" >&2
    exit 1
fi
if [ ! -d "$ADDINS" ]; then
    echo "Fusion 360 AddIns folder not found:" >&2
    echo "  $ADDINS" >&2
    echo "Is Fusion 360 installed for this user?" >&2
    exit 1
fi

DEST="$ADDINS/ioSenderV2"
rm -rf "$DEST"
cp -R "$SRC" "$DEST"

echo "Installed ioSenderV2 to:"
echo "  $DEST"
echo
echo "Now enable it in Fusion 360 (one time):"
echo "  Utilities > ADD-INS > Scripts and Add-Ins (Shift+S) > Add-Ins tab"
echo "  > select 'ioSenderV2' > Run   (tick 'Run on Startup')."
