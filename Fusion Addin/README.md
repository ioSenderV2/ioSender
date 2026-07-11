# The `ioSenderBatchPost` Fusion 360 add-in

This folder contains a small Fusion 360 add-in that posts every operation in the
active Manufacture document and combines them into **one** `.nc` file you load
directly in ioSender with **File ▸ Load**.

## What the add-in does

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

## Install

The add-in must live in Fusion's per-user `AddIns` folder. The scripts here find
that folder and copy the add-in in.

**Windows**
```powershell
powershell -ExecutionPolicy Bypass -File ".\install-windows.ps1"
```
(copies to `%APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns\ioSenderBatchPost`)

**macOS**
```bash
chmod +x install-macos.sh && ./install-macos.sh
```
(copies to `~/Library/Application Support/Autodesk/Autodesk Fusion 360/API/AddIns/ioSenderBatchPost`)

### One-time enable in Fusion (required)

Copying the add-in is all a script *can* do — Fusion then **auto-discovers** it,
but whether an add-in actually runs is a per-user setting stored inside Fusion
with no supported external API to flip. So enable it once:

1. In Fusion, **Utilities** tab ▸ **ADD-INS** ▸ **Scripts and Add-Ins** (or press
   **Shift+S**).
2. **Add-Ins** tab ▸ select **ioSenderBatchPost** ▸ **Run**.
3. Tick **Run on Startup** so it loads automatically from then on.

The **Batch Post (ioSender)** button then appears in the Manufacture workspace
**Actions** panel. (If your Fusion version puts it elsewhere or not at all,
change `PANEL_ID` near the top of `ioSenderBatchPost/ioSenderBatchPost.py`.)

## Use

1. In Fusion's Manufacture workspace, click **Batch Post (ioSender)**.
2. Confirm/choose the **Output folder** and run it.
3. In ioSender, **File ▸ Load**, pick the `<folder-name>.nc` it wrote.

## Updating

Re-run the install script — it overwrites the installed copy. If Fusion was
running, restart it (or toggle the add-in off/on in Scripts and Add-Ins) to pick
up the new version.
