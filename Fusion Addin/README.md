# Load Folder + the `ioSenderBatchPost` Fusion 360 add-in

ioSender's **File ▸ Load Folder** command loads a folder of per-operation `.nc`
files and assembles them, in memory, into one program shown as an expandable
per-toolpath outline (with *Start from this toolpath* / *Run just this toolpath*).
It also strips each file's header/footer, inserts a `G53 G0 Z0` safe-Z retract +
`M6 T<n>` tool change before each toolpath, and can restore the rapid moves that
Fusion's Personal Use licence downgrades to feed moves.

This folder contains the small Fusion 360 add-in that produces those per-op
files. The add-in **only posts** — all combining, tool-change insertion and
rapid restoration happen in ioSender's Load Folder, so the two halves stay
decoupled.

## The workflow

```
Fusion 360 (ioSenderBatchPost add-in)            ioSender (File > Load Folder)
---------------------------------------          ------------------------------
Post every operation to its own file     --->    Pick the folder
  <seq>_<name>_T<tool>.nc in a folder            - strip per-file header/footer
                                                 - insert G53 G0 Z0 + M6 T<n>
                                                 - (optional) restore G1->G0 rapids
                                                 - show as a toolpath outline
                                                 - run all / from / just one toolpath
```

## What the add-in does

For the active Manufacture document it posts **every operation in every setup**
to its own file in a folder you choose:

```
<seq#>_<displayName>_T<tool#>.nc        e.g.  2_FinishBottom_T2.nc
```

- `seq#` — 1-based order across all setups (the order ioSender runs them in).
- `displayName` — the **setup** name when a setup has one operation, else
  `<SetupName>_<OpName>`.
- `tool#` — the operation's tool number.

You pick the **post processor** in the dialog (it lists the `.cps` posts in your
personal/generic post folders, defaulting to `grbl.cps`). The choice doesn't
affect correctness — it's just which post Fusion runs per operation. ioSender's
Load Folder inserts a `G53 G0 Z0` + `M6 T<n>` tool change before each toolpath
**only when the file doesn't already contain an `M6`**, so posts that emit their
own `M6` work too (no double tool change).

The add-in does **not** combine the files, insert tool changes or restore rapids
— ioSender does that on load.

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
3. In ioSender, **File ▸ Load Folder**, pick that folder, and answer the
   *restore rapids* prompt.

## Updating

Re-run the install script — it overwrites the installed copy. If Fusion was
running, restart it (or toggle the add-in off/on in Scripts and Add-Ins) to pick
up the new version.
