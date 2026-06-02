#!/bin/bash
#
# Installs the ioSenderBatchPost Fusion 360 add-in for the current user by
# copying it into Fusion's AddIns folder, where Fusion auto-discovers add-ins.
#
# Run:
#     chmod +x install-macos.sh && ./install-macos.sh
#
# After installing you must enable it ONCE in Fusion (it cannot be auto-run
# from outside Fusion):
#     Utilities > ADD-INS > Scripts and Add-Ins (Shift+S) > Add-Ins tab >
#     select "ioSenderBatchPost" > Run  (tick "Run on Startup" to keep it).

set -e

SRC="$(cd "$(dirname "$0")" && pwd)/ioSenderBatchPost"
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

DEST="$ADDINS/ioSenderBatchPost"
rm -rf "$DEST"
cp -R "$SRC" "$DEST"

echo "Installed ioSenderBatchPost to:"
echo "  $DEST"
echo
echo "Now enable it in Fusion 360 (one time):"
echo "  Utilities > ADD-INS > Scripts and Add-Ins (Shift+S) > Add-Ins tab"
echo "  > select 'ioSenderBatchPost' > Run   (tick 'Run on Startup')."
