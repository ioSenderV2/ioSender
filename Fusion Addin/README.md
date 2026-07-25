# The `ioSenderV2` Fusion 360 add-in

This folder contains the ioSenderV2 Fusion 360 add-in: an **`ioSenderV2`** dropdown in the
Manufacture workspace toolbar with two commands.

## Batch Post Process

Posts every operation in the active Manufacture document and combines them into **one** `.nc`
file you load directly in ioSender with **File ▸ Load**.

For the active Manufacture document it posts **every operation in every setup**,
then stitches them into a single program named after the output folder:

```
<folder-name>.nc
```

Fusion's `postProcess` API only posts one operation at a time, so each operation
is posted to a temp directory first, then combined and the temp files discarded
— the output folder ends up with just `<folder-name>.nc` (plus a `_batchpost.log`
for troubleshooting). The combined file:

- Opens with `(STOCK X=.. Y=.. Z=..)` and one `(TOOL T=.. D=.. TYPE=..)` comment
  line per tool used — the stock size and each tool's diameter/shape, which the
  grblHAL simulator's 3D view reads for material-removal carving (real
  controllers ignore them). Format spec: `TOOL_TABLE_FORMAT.md` in the simulator
  repo.
- Precedes each operation with a `(--- seq: name (Tn) ---)` section marker, then
  a `G53 G0 Z0` safe-Z retract + `M6 T<n>` tool change (skipped if the post
  already emitted its own `M6` — no double tool change).
- Restores the rapid moves Fusion's Personal Use licence downgrades to feed
  moves.

ioSender recognizes the `(--- ... ---)` section markers on a plain **File ▸
Load** and shows the same expandable per-toolpath outline (with *Start from this
toolpath* / *Run just this toolpath*) that used to require the separate Load
Folder command.

You pick the **post processor** in the dialog (it lists the `.cps` posts in your
personal/generic post folders, defaulting to `grbl.cps`). The choice doesn't
affect correctness — it's just which post Fusion runs per operation.

## Feeds and Speeds

Exports every Setup/Operation's current feeds, speeds, tool, and geometry data to
JSON, for ioSenderV2 (the WPF app) to analyze — ioSenderV2 decides what needs
adjusting (a table-driven material/chip-load engine, cross-checked against the
connected controller's actual machine limits, plus an optional AI review pass
when configured) and writes a companion apply file.

No Action picker — the command just does the right thing for whatever state
you're in:

- **Opening it** deletes any leftover export/apply file for this document
  (a clean start — a stale apply file from an abandoned earlier cycle must
  never silently apply), then exports immediately, writing
  `~/Downloads/ioSenderV2/<docName>.json`.
- **Pressing OK** applies `~/Downloads/ioSenderV2/<docName>-apply.json` (written
  by ioSenderV2) if it exists — writing those values back onto the matching
  operations and regenerating their toolpaths — or just closes if it doesn't
  (nothing to apply yet).
- **Closing the dialog** (OK, Cancel, or Escape) deletes both files again, so
  nothing lingers in `~/Downloads/ioSenderV2/` once a cycle is done.

So the round trip is: open the command (exports) → switch to ioSenderV2 to
analyze and write the apply file → switch back to Fusion (leave the dialog
open) and press OK
(applies).

No recommendation math happens in Fusion — this command only round-trips raw
current values and the adjustments ioSenderV2 decided on.

## Install

The add-in must live in Fusion's per-user `AddIns` folder.

**From ioSenderV2 (recommended)** — Help ▸ Support ▸ *Install ioSenderV2 Fusion
Addin...* creates a symlink to this folder instead of copying it, so future
ioSenderV2 updates that change the add-in's code need no reinstall — just
reload the add-in (or restart Fusion).

**Manual scripts** (offline/no ioSenderV2-on-this-machine use):

**Windows**
```powershell
powershell -ExecutionPolicy Bypass -File ".\install-windows.ps1"
```
(copies to `%APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns\ioSenderV2`)

**macOS**
```bash
chmod +x install-macos.sh && ./install-macos.sh
```
(copies to `~/Library/Application Support/Autodesk/Autodesk Fusion 360/API/AddIns/ioSenderV2`)

### One-time enable in Fusion (required)

Installing (either way) is all that *can* happen automatically — Fusion then
**auto-discovers** the add-in, but whether it actually runs is a per-user
setting stored inside Fusion with no supported external API to flip. So enable
it once:

1. In Fusion, **Utilities** tab ▸ **ADD-INS** ▸ **Scripts and Add-Ins** (or press
   **Shift+S**).
2. **Add-Ins** tab ▸ select **ioSenderV2** ▸ **Run**.
3. Tick **Run on Startup** so it loads automatically from then on.

The **ioSenderV2** dropdown then appears in the Manufacture workspace toolbar.

## Use

1. In Fusion's Manufacture workspace, open the **ioSenderV2** dropdown.
2. **Batch Post Process**: confirm/choose the **Output folder** and run it; in
   ioSender, **File ▸ Load** the `<folder-name>.nc` it wrote.
3. **Feeds and Speeds**: **Export**, switch to ioSenderV2 to analyze and write
   the apply file, come back and **Apply**.

## Updating

If installed via the ioSenderV2 Help-menu symlink, updating ioSenderV2 itself
updates the add-in — just reload it (or restart Fusion). If installed via the
manual scripts, re-run the install script, then restart Fusion (or toggle the
add-in off/on in Scripts and Add-Ins) to pick up the new version.
