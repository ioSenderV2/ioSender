"""
ioSenderV2.py - Fusion 360 add-in entry point (must match this folder's name,
"ioSenderV2", so Fusion can find it: this file + ioSenderV2.manifest are the
add-in's actual entry point/manifest; batchPostProcess.py and feedsAndSpeeds.py
are plain sibling modules containing one command each, not entry points).

Owns: creating a single "ioSenderV2" toolbar panel on the Manufacture workspace
with two direct command buttons - Feeds and Speeds (feedsAndSpeeds.py) and
Batch Post Process (batchPostProcess.py). Neither command module places its
own toolbar control; each only builds a CommandDefinition
(create_command_definition/cleanup) for this file to attach.

No nested dropdown control inside the panel: an earlier version put a
dropdown (itself labeled "ioSenderV2") inside this panel (also labeled
"ioSenderV2"), so a narrow ribbon collapsed BOTH into their own
same-named flyouts - two "ioSenderV2" levels to click through before reaching
either command. The panel alone already gives Fusion something to collapse
into a single flyout when the ribbon is narrow, so the two commands go
directly on it.
"""

import os
import sys
import importlib
import traceback

import adsk.core

_addin_root = os.path.dirname(os.path.abspath(__file__))
if _addin_root not in sys.path:
    sys.path.append(_addin_root)

import batchPostProcess
import feedsAndSpeeds

# Fusion's Python host keeps already-imported modules in memory across a Stop/Run of the add-in within
# the same Fusion session - only fully quitting Fusion clears them. Without an explicit reload here, an
# edited batchPostProcess.py/feedsAndSpeeds.py (including one refreshed in place by an ioSenderV2 update,
# via the Help > Support > Install... symlink) would keep running the STALE code the interpreter loaded
# the first time, even after Stop/Run or after CheckFusionAddinUpdated's own "reload the add-in" prompt on
# the ioSenderV2 side. Reloading on every run() (not just once at import time) is what actually makes
# Stop/Run pick up changes, matching SRWCommands.py's own "force reload modules" pattern.
importlib.reload(batchPostProcess)
importlib.reload(feedsAndSpeeds)

PANEL_ID = 'ioSenderV2Panel'
PANEL_NAME = 'ioSenderV2'

# Tab to place the panel on, within the Manufacture (CAM) workspace. Fusion
# versions differ on the exact id of the CAM tab that holds Setup/Actions/etc,
# so try a preference list and fall back to whichever tab exists first.
_CAM_TAB_PREFERENCE = ['CAMActionsTab', 'CAMManageTab', 'CAMManufactureTab',
                       'MillingTab', 'CAMSetupTab', 'ActionsTab']


def _find_cam_tab(ui):
    ws = ui.workspaces.itemById('CAMEnvironment')
    if not ws:
        return None
    for pref in _CAM_TAB_PREFERENCE:
        for t in ws.toolbarTabs:
            if t.id == pref:
                return t
    return ws.toolbarTabs.item(0) if ws.toolbarTabs.count > 0 else None


def _cleanup(ui):
    """Remove any existing panel/command-defs from a previous run() (hot
    reload during development) or from stop()."""
    tab = _find_cam_tab(ui)
    if tab:
        panel = tab.toolbarPanels.itemById(PANEL_ID)
        if panel:
            for cmd_id in (feedsAndSpeeds.CMD_ID, batchPostProcess.CMD_ID):
                ctrl = panel.controls.itemById(cmd_id)
                if ctrl:
                    ctrl.deleteMe()
            panel.deleteMe()
    batchPostProcess.cleanup(ui)
    feedsAndSpeeds.cleanup(ui)


def run(context):
    app = adsk.core.Application.get()
    ui = app.userInterface
    try:
        _cleanup(ui)   # idempotent: clears anything left from a previous run()

        tab = _find_cam_tab(ui)
        if not tab:
            ui.messageBox('ioSenderV2: could not find the Manufacture workspace '
                          'toolbar - the add-in loaded but has no button.')
            return

        panel = tab.toolbarPanels.add(PANEL_ID, PANEL_NAME, '', False)

        cmd_def_feeds = feedsAndSpeeds.create_command_definition(ui)
        cmd_def_batch = batchPostProcess.create_command_definition(ui)
        panel.controls.addCommand(cmd_def_feeds)
        panel.controls.addCommand(cmd_def_batch)
    except Exception:
        if ui:
            ui.messageBox('ioSenderV2 failed to start:\n{}'.format(traceback.format_exc()))


def stop(context):
    app = adsk.core.Application.get()
    ui = app.userInterface
    try:
        _cleanup(ui)
    except Exception:
        if ui:
            ui.messageBox('ioSenderV2 failed to stop:\n{}'.format(traceback.format_exc()))
