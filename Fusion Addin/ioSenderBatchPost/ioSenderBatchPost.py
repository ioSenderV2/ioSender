"""
ioSenderBatchPost - Fusion 360 add-in

Posts every operation in every Setup of the active Manufacture document to its
own .nc file in a chosen folder, named:

    <seq#>_<displayName>_T<tool#>.nc      e.g.  2_FinishBottom_T2.nc

It also writes a "0_tooltable.nc" header file carrying (STOCK X=.. Y=.. Z=..) and
(TOOL T=.. D=.. TYPE=..) comment lines - the stock size and each tool's diameter
and shape - which the grblHAL simulator's 3D view reads for material-removal
carving. ioSender's "File > Load Folder" loads that file first (comments
preserved) and pushes those leading comments to the simulator; real controllers
ignore them. See TOOL_TABLE_FORMAT.md in the simulator repo for the grammar.

It does NOT combine the per-op files, insert tool changes, or restore
Fusion-Personal-Use rapids. Those steps are performed by ioSender's Load Folder
command, which reads exactly this set of files.

This is a standalone extract of the post-processing half of the SRWCommands
"Batch Post Process" command, with none of the SRWCommands framework.
"""

import adsk.core
import adsk.cam
import adsk.fusion
import os
import re
import math
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


# ---------------------------------------------------------------------------
# Tool-table / stock extraction for the simulator's 3D carve.
#
# This mirrors the SRWCommands PostProcess add-in so both produce an identical
# 0_ToolTable.nc. CAM length parameters are in cm (Fusion internal units) -> mm;
# angle parameters are in radians. The simulator's (STOCK)/(TOOL) comments are
# always in mm / degrees regardless of document units. See TOOL_TABLE_FORMAT.md.
# ---------------------------------------------------------------------------

def _tool_geometry(op):
    """{'number','diameter','type','angle'} for an op's tool, or None when the
    cutter has no usable diameter (the simulator ignores a TOOL with no D=)."""
    try:
        tool = op.tool
        if tool is None:
            return None
        params = tool.parameters

        def _pval(name):
            try:
                return params.itemByName(name).value.value
            except Exception:
                return None

        raw_dia = _pval('tool_diameter')
        if raw_dia is None or float(raw_dia) <= 0.0:
            return None
        diameter_mm = float(raw_dia) * 10.0     # cm -> mm

        raw_type = _pval('tool_type')
        t = (raw_type if isinstance(raw_type, str) else '').lower()

        raw_angle = _pval('tool_taperAngle')
        if raw_angle is None:
            raw_angle = _pval('tool_tipAngle')

        if 'ball' in t:
            ttype = 'BALL'
        elif ('chamfer' in t or 'v-bit' in t or 'vbit' in t or 'v bit' in t
              or 'engrave' in t or 'taper' in t):
            ttype = 'VBIT'
        elif ('flat' in t or 'bull' in t or 'end mill' in t or 'face' in t
              or 'slot' in t):
            ttype = 'FLAT'
        elif t:
            ttype = t.split()[0].upper()
        else:
            ttype = 'FLAT'

        angle = None
        if ttype == 'VBIT' and raw_angle is not None:
            # CAM angle is radians; the chamfer/taper angle is the half-angle from
            # the tool axis and v-bits are specified by their INCLUDED angle, so
            # double it. (Matches SRWCommands; verify against a real v-bit.)
            angle = int(round(math.degrees(float(raw_angle)) * 2.0))

        return {'number': _get_tool_number(op), 'diameter': diameter_mm,
                'type': ttype, 'angle': angle}
    except Exception:
        return None


def _read_stock_dims(setup):
    """Stock box size (x, y, z mm) from a setup's parameters, or None. Prefers the
    computed box dims, then the box corners, then the fixed-box dims (all cm->mm)."""
    try:
        prm = setup.parameters
    except Exception:
        return None

    def _p(name):
        try:
            return float(prm.itemByName(name).value.value)
        except Exception:
            return None

    def _ok(v):
        return v is not None and not math.isnan(v) and v > 1e-9

    # 1) Computed box dimensions (fixed and relative box stock).
    x, y, z = (_p('job_stockInfoDimensionX'),
               _p('job_stockInfoDimensionY'),
               _p('job_stockInfoDimensionZ'))
    if _ok(x) and _ok(y) and _ok(z):
        return (x * 10.0, y * 10.0, z * 10.0)

    # 2) Stock box corners.
    xl, xh = _p('stockXLow'), _p('stockXHigh')
    yl, yh = _p('stockYLow'), _p('stockYHigh')
    zl, zh = _p('stockZLow'), _p('stockZHigh')
    if None not in (xl, xh, yl, yh, zl, zh):
        cx, cy, cz = xh - xl, yh - yl, zh - zl
        if _ok(cx) and _ok(cy) and _ok(cz):
            return (cx * 10.0, cy * 10.0, cz * 10.0)

    # 3) Fixed-box dimensions.
    x, y, z = _p('job_stockFixedX'), _p('job_stockFixedY'), _p('job_stockFixedZ')
    if _ok(x) and _ok(y) and _ok(z):
        return (x * 10.0, y * 10.0, z * 10.0)

    return None


def _stock_dims_mm(cam):
    """Stock (x, y, z) mm from the first setup with readable box stock, else None."""
    for i in range(cam.setups.count):
        dims = _read_stock_dims(cam.setups.item(i))
        if dims:
            return dims
    return None


def _format_tool_diameter(d_mm):
    """Diameter as a compact decimal keeping >=1 decimal place (6.35, 12.7, 1.0)."""
    s = ('%.3f' % d_mm).rstrip('0')
    if s.endswith('.'):
        s += '0'
    return s


def _fmt(v):
    """Whole-number mm (rounded UP, matching the (STOCK) line) for the summary message."""
    return str(int(math.ceil(v)))


def _write_tool_table(folder, stock, tools):
    """Write 0_ToolTable.nc with (STOCK ...) + (TOOL ...) lines. Returns the path.
    Format matches the SRWCommands add-in so both produce identical files."""
    lines = ['(Tool table - generated by ioSenderBatchPost)']
    if stock:
        # Round UP: these dims bound the stock envelope (simulator block / soft-limit reference), so never
        # under-size - a fractional 427.095 must become 428, not 427.
        lines.append('(STOCK X=%d Y=%d Z=%d)' % (math.ceil(stock[0]), math.ceil(stock[1]), math.ceil(stock[2])))
    for t in sorted(tools.values(), key=lambda d: d['number']):
        dstr = _format_tool_diameter(t['diameter'])
        angle = (' A=%d' % t['angle']) if (t['type'] == 'VBIT' and t['angle'] is not None) else ''
        lines.append('(TOOL T=%d D=%-6sTYPE=%s%s)' % (t['number'], dstr, t['type'], angle))
    path = os.path.join(folder, '0_ToolTable.nc')
    with open(path, 'w', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    return path


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


def _post_all(folder, post_file, write_tool_table=True, stock_override=None):
    """Post every operation to <folder> with <post_file>.

    stock_override (x, y, z mm; z may be None) replaces the setup stock for the (STOCK) line only.

    Returns (posted, total, failures, table_info) where table_info is None or
    {'path', 'tools', 'stock'} describing the 0_tooltable.nc that was written.
    """
    app = adsk.core.Application.get()

    cam = adsk.cam.CAM.cast(app.activeProduct)
    if not cam:
        raise RuntimeError('No active Manufacture (CAM) document. Open a document in the '
                           'Manufacture workspace with at least one setup, then re-run.')
    if cam.setups.count == 0:
        raise RuntimeError('This Manufacture document has no setups.')

    os.makedirs(folder, exist_ok=True)

    log = ['ioSenderBatchPost',
           'post: %s' % post_file,
           'post exists: %s' % os.path.isfile(post_file),
           'folder: %s' % folder,
           'setups: %d' % cam.setups.count]

    total = 0
    failures = []
    tools = {}      # tool number -> geometry dict (first occurrence wins)
    for s_idx in range(cam.setups.count):
        setup = cam.setups.item(s_idx)
        op_count = setup.operations.count
        for o_idx in range(op_count):
            op = setup.operations.item(o_idx)
            total += 1
            tool_number = _get_tool_number(op)

            # Collect cutter geometry for the tool table (independent of posting).
            geom = _tool_geometry(op)
            if geom and geom['number'] not in tools:
                tools[geom['number']] = geom

            # Setup name when a setup has one op (the common case where the
            # setup is named after the machining step); else Setup_Op.
            display_name = setup.name if op_count == 1 else '%s_%s' % (setup.name, op.name)

            if not _ensure_toolpath(cam, op):
                failures.append('%s: toolpath generation failed' % display_name)
                log.append('SKIP %s: no valid toolpath' % display_name)
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
                log.append('FAIL %s: postProcess raised: %s' % (display_name, ex))
                continue
            if not ok:
                failures.append('%s: postProcess returned False' % display_name)
                log.append('FAIL %s: postProcess returned False' % display_name)
            else:
                outnc = os.path.join(folder, program + '.nc')
                nbytes = os.path.getsize(outnc) if os.path.exists(outnc) else -1
                log.append('OK   %s -> %s.nc (%d bytes)' % (display_name, program, nbytes))

    table_info = None
    if write_tool_table and tools:
        # Measured-stock override (pasted from ioSender Load Stock) wins for the (STOCK) line; else read the
        # setup's stock box. We DO NOT change Fusion's CAM stock - resizing it forces a toolpath regenerate
        # against the new box and can post operations with no motion (tool changes only).
        stock = _stock_dims_mm(cam)
        if stock_override:
            ox, oy, oz = stock_override
            if oz is None:
                oz = stock[2] if stock else 0
            stock = (ox, oy, oz)
        path = _write_tool_table(folder, stock, tools)
        table_info = {'path': path, 'tools': len(tools), 'stock': stock}

    log.append('posted %d of %d, failures %d' % (total - len(failures), total, len(failures)))
    try:
        with open(os.path.join(folder, '_batchpost.log'), 'w', newline='\n') as f:
            f.write('\n'.join(log) + '\n')
    except Exception:
        pass

    return total - len(failures), total, failures, table_info


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

            write_tt = True
            tt = inputs.itemById('writeToolTable')
            if tt is not None:
                write_tt = bool(tt.value)

            # Optional measured stock (pasted from ioSender Load Stock, "X Y [Z]") - used ONLY for the (STOCK)
            # line in the tool table (the simulator block). We do NOT alter Fusion's CAM stock: resizing it
            # forces a toolpath regenerate against the new box and can post operations with no motion.
            stock_override = None
            stock_field = (inputs.itemById('measuredStock').value or '').strip()
            if stock_field:
                nums = re.findall(r'[-+]?\d+(?:\.\d+)?', stock_field)
                if len(nums) < 2:
                    ui.messageBox('Measured stock needs at least X and Y (e.g. "428 428 19").\n'
                                  'Leave it blank to use the setup stock.', CMD_NAME)
                    return
                stock_override = (float(nums[0]), float(nums[1]),
                                  float(nums[2]) if len(nums) >= 3 else None)

            posted, total, failures, table_info = _post_all(folder, post_file, write_tt, stock_override)

            msg = 'Posted %d of %d operation(s) to:\n%s' % (posted, total, folder)
            if table_info:
                if table_info['stock']:
                    sx, sy, sz = table_info['stock']
                    msg += '\n\nWrote %s (%d tool(s), stock %s x %s x %s mm).' % (
                        os.path.basename(table_info['path']), table_info['tools'], _fmt(sx), _fmt(sy), _fmt(sz))
                else:
                    msg += '\n\nWrote %s (%d tool(s); stock size unavailable - set it in the simulator).' % (
                        os.path.basename(table_info['path']), table_info['tools'])
            msg += '\n\nOpen this folder in ioSender with File > Load Folder.'
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

            # Paste ioSender Load Stock's measured size here ("X Y" or "X Y Z" mm - use its Copy size button).
            # When set, it is written to the (STOCK ...) line in 0_ToolTable.nc (the grblHAL simulator's block)
            # in place of the setup stock. It does NOT change Fusion's CAM stock (that would force a toolpath
            # regenerate and can post empty operations). Blank = use the setup's stock for (STOCK).
            inputs.addStringValueInput('measuredStock', 'Measured stock X Y [Z] (mm) -> sim (STOCK)', '')

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

            inputs.addBoolValueInput('writeToolTable', 'Write tool table (0_tooltable.nc)', True, '', True)

            inputs.addTextBoxCommandInput(
                'info', '',
                'Each operation is posted to &lt;seq&gt;_&lt;name&gt;_T&lt;tool&gt;.nc in this folder. '
                'Combining, tool-change insertion and rapid restoration are done by ioSender '
                '(File &gt; Load Folder), which works with any post (M6 or not).<br/><br/>'
                'The tool table writes a 0_tooltable.nc with (STOCK ...) and (TOOL ...) comments '
                '(stock size + each tool\'s diameter/shape) for the grblHAL simulator\'s 3D carve. '
                'Harmless on real machines.<br/><br/>'
                'Measured stock: paste the size from ioSender Load Stock (its Copy size button) - it replaces the '
                '(STOCK ...) line written for the grblHAL simulator with the real measured size. It does NOT '
                'change Fusion\'s CAM stock or your toolpaths. Leave blank to use the setup\'s stock.',
                5, True)

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
