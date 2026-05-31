"""
ioSenderBatchPost - Fusion 360 add-in

Posts every operation in every Setup of the active Manufacture document to its
own .nc file in a chosen folder, named:

    <seq#>_<displayName>_T<tool#>.nc      e.g.  2_FinishBottom_T2.nc

That is all it does - it does NOT combine the files, insert tool changes, or
restore Fusion-Personal-Use rapids. Those steps are performed by ioSender's
"File > Load Folder" command, which reads exactly this set of files.

This is a standalone extract of the post-processing half of the SRWCommands
"Batch Post Process" command, with none of the SRWCommands framework.
"""

import adsk.core
import adsk.cam
import adsk.fusion
import os
import re
import traceback

# Keep event handlers alive for the lifetime of the add-in.
_handlers = []

# Available post processors for the dialog dropdown: {filename: full path}.
# Rebuilt each time the command dialog is opened.
_post_processors = {}

CMD_ID = 'ioSenderBatchPostCmd'
CMD_NAME = 'Batch Post (ioSender)'
CMD_DESC = ('Post every operation in every setup to its own '
            '<seq>_<name>_T<tool>.nc file in a folder, for ioSender\'s '
            'File > Load Folder command.')

# Toolbar panel the button is added to. CAMActionPanel is the Manufacture
# workspace "Actions" panel (where the stock Post Process button lives). If the
# button does not appear in your Fusion version, change this id.
PANEL_ID = 'CAMActionPanel'


# ---------------------------------------------------------------------------
# Posting helpers (ported from SRWCommands postProcess/entry.py)
# ---------------------------------------------------------------------------

def _safe_filename(name):
    """Sanitize an operation/setup name for use as a filename."""
    cleaned = ''.join(c if c.isalnum() or c in ' _-' else '_' for c in name)
    return cleaned.strip() or 'operation'


def _list_post_processors(cam):
    """Map {filename: full path} of available .cps posts (personal folder first).

    grbl.cps is the right choice for the ioSender pipeline: it does NOT emit M6,
    and ioSender's Load Folder inserts the G53 G0 Z0 + M6 T<n> tool change. A post
    that emits its own M6 would produce a double tool change per toolpath.
    """
    posts = {}
    if cam:
        for folder_attr in ('personalPostFolder', 'genericPostFolder'):
            folder = getattr(cam, folder_attr, None)
            if not folder or not os.path.isdir(folder):
                continue
            try:
                for f in sorted(os.listdir(folder)):
                    if f.lower().endswith('.cps') and f not in posts:
                        posts[f] = os.path.join(folder, f)
            except Exception:
                pass
    return posts


def _get_tool_number(op):
    """Best-effort read of an operation's tool number. Returns 0 if unknown."""
    try:
        tool = op.tool
        if tool is None:
            return 0
        try:
            return int(tool.parameters.itemByName('tool_number').value.value)
        except Exception:
            pass
        try:
            return int(getattr(tool, 'number', 0))
        except Exception:
            pass
    except Exception:
        pass
    return 0


def _ensure_toolpath(cam, op):
    """Generate the toolpath for an operation if needed. True if valid after."""
    try:
        if op.isToolpathValid:
            return True
    except Exception:
        pass
    try:
        future = cam.generateToolpath(op)
        while not future.isGenerationCompleted:
            adsk.doEvents()
        try:
            return bool(op.isToolpathValid)
        except Exception:
            return True
    except Exception:
        return False


def _default_output_folder(app):
    """~/Downloads/<docName> with the Fusion ' v<n>' cloud suffix stripped."""
    try:
        doc = app.activeDocument
        base = os.path.splitext(doc.name)[0] if doc and doc.name else 'Fusion'
        m = re.match(r'^(.*) v\d+$', base)
        if m:
            base = m.group(1)
        return os.path.join(os.path.expanduser('~/Downloads'), _safe_filename(base))
    except Exception:
        return os.path.expanduser('~/Downloads')


def _post_all(folder, post_file):
    """Post every operation to <folder> with <post_file>. Returns (posted, total, failures)."""
    app = adsk.core.Application.get()

    cam = adsk.cam.CAM.cast(app.activeProduct)
    if not cam:
        raise RuntimeError('No active Manufacture (CAM) document. Open a document in the '
                           'Manufacture workspace with at least one setup, then re-run.')
    if cam.setups.count == 0:
        raise RuntimeError('This Manufacture document has no setups.')

    os.makedirs(folder, exist_ok=True)

    total = 0
    failures = []
    for s_idx in range(cam.setups.count):
        setup = cam.setups.item(s_idx)
        op_count = setup.operations.count
        for o_idx in range(op_count):
            op = setup.operations.item(o_idx)
            total += 1
            tool_number = _get_tool_number(op)

            # Setup name when a setup has one op (the common case where the
            # setup is named after the machining step); else Setup_Op.
            display_name = setup.name if op_count == 1 else '%s_%s' % (setup.name, op.name)

            if not _ensure_toolpath(cam, op):
                failures.append('%s: toolpath generation failed' % display_name)
                continue

            program = _safe_filename('%d_%s_T%d' % (total, display_name, tool_number))
            post_input = adsk.cam.PostProcessInput.create(
                program, post_file, folder,
                adsk.cam.PostOutputUnitOptions.DocumentUnitsOutput)
            try:
                post_input.isOpenInEditor = False
            except Exception:
                pass

            try:
                ok = cam.postProcess(op, post_input)
            except Exception as ex:
                failures.append('%s: postProcess raised: %s' % (display_name, ex))
                continue
            if not ok:
                failures.append('%s: postProcess returned False' % display_name)

    return total - len(failures), total, failures


# ---------------------------------------------------------------------------
# Command UI
# ---------------------------------------------------------------------------

class ExecuteHandler(adsk.core.CommandEventHandler):
    def notify(self, args):
        app = adsk.core.Application.get()
        ui = app.userInterface
        try:
            inputs = args.command.commandInputs
            folder = (inputs.itemById('outputFolder').value or '').strip()
            if not folder:
                ui.messageBox('Please specify an output folder.', CMD_NAME)
                return

            dd = inputs.itemById('postProcessor')
            selected = dd.selectedItem.name if (dd and dd.selectedItem) else None
            post_file = _post_processors.get(selected) if selected else None
            if not post_file or not os.path.isfile(post_file):
                ui.messageBox('No post processor selected, or the file was not found.', CMD_NAME)
                return

            posted, total, failures = _post_all(folder, post_file)

            msg = 'Posted %d of %d operation(s) to:\n%s\n\nOpen this folder in ioSender with File > Load Folder.' % (posted, total, folder)
            if failures:
                msg += '\n\nFailed:\n  ' + '\n  '.join(failures)
            ui.messageBox(msg, CMD_NAME)
        except Exception:
            ui.messageBox('Batch Post failed:\n{}'.format(traceback.format_exc()), CMD_NAME)


class CommandCreatedHandler(adsk.core.CommandCreatedEventHandler):
    def notify(self, args):
        app = adsk.core.Application.get()
        ui = app.userInterface
        try:
            cmd = args.command
            inputs = cmd.commandInputs

            inputs.addStringValueInput('outputFolder', 'Output folder', _default_output_folder(app))

            # Post-processor dropdown, defaulting to grbl.cps. The choice doesn't affect
            # behaviour (ioSender handles files with or without M6) - it's just which post
            # Fusion runs per operation. Explicit so we never silently pick the wrong one.
            global _post_processors
            _post_processors = _list_post_processors(adsk.cam.CAM.cast(app.activeProduct))
            dd = inputs.addDropDownCommandInput('postProcessor', 'Post processor',
                                                adsk.core.DropDownStyles.TextListDropDownStyle)
            names = sorted(_post_processors.keys(), key=lambda n: n.lower())
            default = next((n for n in names if n.lower() == 'grbl.cps'), names[0] if names else None)
            for n in names:
                dd.listItems.add(n, n == default, '')
            if not names:
                dd.listItems.add('(no .cps posts found)', True, '')

            inputs.addTextBoxCommandInput(
                'info', '',
                'Each operation is posted to &lt;seq&gt;_&lt;name&gt;_T&lt;tool&gt;.nc in this folder. '
                'Combining, tool-change insertion and rapid restoration are done by ioSender '
                '(File &gt; Load Folder), which works with any post (M6 or not).',
                3, True)

            onExecute = ExecuteHandler()
            cmd.execute.add(onExecute)
            _handlers.append(onExecute)
        except Exception:
            ui.messageBox('Failed:\n{}'.format(traceback.format_exc()), CMD_NAME)


def run(context):
    app = adsk.core.Application.get()
    ui = app.userInterface
    try:
        cmd_defs = ui.commandDefinitions

        cmd_def = cmd_defs.itemById(CMD_ID)
        if cmd_def:
            cmd_def.deleteMe()
        cmd_def = cmd_defs.addButtonDefinition(CMD_ID, CMD_NAME, CMD_DESC)

        on_created = CommandCreatedHandler()
        cmd_def.commandCreated.add(on_created)
        _handlers.append(on_created)

        panel = ui.allToolbarPanels.itemById(PANEL_ID)
        if panel:
            existing = panel.controls.itemById(CMD_ID)
            if existing:
                existing.deleteMe()
            panel.controls.addCommand(cmd_def)
        else:
            ui.messageBox('ioSenderBatchPost: toolbar panel "%s" not found. The command is '
                          'registered but has no button; edit PANEL_ID in the add-in to place it.' % PANEL_ID,
                          CMD_NAME)
    except Exception:
        if ui:
            ui.messageBox('ioSenderBatchPost failed to start:\n{}'.format(traceback.format_exc()))


def stop(context):
    app = adsk.core.Application.get()
    ui = app.userInterface
    try:
        panel = ui.allToolbarPanels.itemById(PANEL_ID)
        if panel:
            ctrl = panel.controls.itemById(CMD_ID)
            if ctrl:
                ctrl.deleteMe()

        cmd_def = ui.commandDefinitions.itemById(CMD_ID)
        if cmd_def:
            cmd_def.deleteMe()
    except Exception:
        if ui:
            ui.messageBox('ioSenderBatchPost failed to stop:\n{}'.format(traceback.format_exc()))
