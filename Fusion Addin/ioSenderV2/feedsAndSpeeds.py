"""
feedsAndSpeeds.py - "Feeds and Speeds" command, part of the ioSenderV2 Fusion add-in.

Framework-free (raw adsk.core.CommandInputs only - no CommandInfo/config.json/
common_imports) and with NO recommendation math: this add-in only extracts every
Setup/Operation's CURRENT feeds/speeds/tool/geometry data and writes it to JSON.
Deciding what needs adjusting is ioSenderV2's job (the WPF app), not Fusion's - it
reads the export, computes recommendations (a ported material chip-load engine + a
check against the connected controller's actual grblHAL limits), and writes a
companion *-apply.json next to it. This command then applies THAT file's values back
onto the ops and regenerates their toolpaths.

The loop:
    1. Open this command - it exports immediately, writing
       ~/Downloads/ioSenderV2/<docName>.json : every setup/op's current
       tool/feed/speed/geometry data. ioSenderV2 reads this.
    2. ioSenderV2 writes  ~/Downloads/ioSenderV2/<docName>-apply.json :
           {"ops": [{"id": "s0/o0", "set": {"rpm": 9000, "cutting_feed": 550}}]}
    3. Open this command again and press OK - if that apply file exists, it reads
       it, writes each value onto the matching op (with read-back verification),
       and regenerates the affected toolpaths. If it doesn't exist yet, OK just
       closes - nothing to do until ioSenderV2 has written one.

No Action picker: an earlier version used a dropdown (Export/Apply) because the
execute/OK event was assumed unreliable and in-dialog icon buttons render as "?" in
this setup - but OK's execute event fires fine here since this dialog has no other
inputs to NOT change, so it's simpler to just export on open and apply-if-present
on OK.
"""

import os
import re
import json
import traceback

import adsk.core
import adsk.cam

# Keep event handlers alive for the lifetime of the add-in.
_handlers = []

CMD_ID = 'ioSenderV2FeedsSpeedsCmd'
CMD_NAME = 'Feeds and Speeds'
CMD_DESC = ('Export every Setup/Operation\'s current feeds, speeds, tool and '
            'geometry data to JSON for ioSenderV2 to analyze, then apply the '
            'adjustments it writes back.')

# Fixed folder both this add-in and ioSenderV2 (the WPF app) agree on.
EXPORT_FOLDER = os.path.expanduser('~/Downloads/ioSenderV2')

# Field -> unit expected by _set_value (RPM is unitless).
_APPLY_UNITS = {
    'rpm': '',
    'cutting_feed': 'mm/min',
    'plunge_feed': 'mm/min',
    'axial_step': 'mm',
    'radial_step': 'mm',
}

# Op-parameter name candidates (Fusion's API uses several conventions across
# versions/strategies; the first one that resolves wins). Add new names here
# when an apply fails and the log shows the actual parameter name on the op
# (see _dump_op_param_names diagnostic).
PARAM_CANDIDATES = {
    'rpm':            ['tool_spindleSpeed'],
    'cutting_feed':   ['tool_feedCutting'],
    'plunge_feed':    ['tool_feedPlunge'],
    'axial_step':     ['maximumStepdown', 'stepdown', 'maximumRoughingStepdown',
                       'passDepth', 'verticalStepdown', 'stepdownDistance'],
    'radial_step':    ['stepover', 'maximumStepover', 'maxRoughingStepover',
                       'optimalLoad', 'stepoverDistance', 'horizontalStepover',
                       'maximumHorizontalStepover', 'cuttingDistance'],
    'diameter':       ['tool_diameter', 'tool_diameterCutting'],
    'flutes':         ['tool_numberOfFlutes', 'tool_numFlutes'],
    'tool_type':      ['tool_type'],
    'coolant':        ['tool_coolant'],
}


# ---------------------------------------------------------------------------
# CAM document + path helpers
# ---------------------------------------------------------------------------

def _active_cam():
    """Return the active CAM product, or None (falls back to walking the
    active document's products)."""
    app = adsk.core.Application.get()
    cam = adsk.cam.CAM.cast(app.activeProduct)
    if cam is not None:
        return cam
    try:
        doc = app.activeDocument
        if doc:
            for i in range(doc.products.count):
                candidate = adsk.cam.CAM.cast(doc.products.item(i))
                if candidate:
                    return candidate
    except Exception:
        pass
    return None


def _safe_doc(cam):
    """Filesystem-safe base name for the document (drops a trailing ' vN')."""
    base = os.path.splitext(cam.parentDocument.name)[0]
    m = re.match(r'^(.*) v\d+$', base)
    if m:
        base = m.group(1)
    safe = ''.join(c if c.isalnum() or c in ' _-' else '_' for c in base).strip()
    return safe or 'untitled'


def _paths(cam):
    """(export_path, apply_path) for this document, under EXPORT_FOLDER."""
    safe = _safe_doc(cam)
    return (os.path.join(EXPORT_FOLDER, '%s.json' % safe),
            os.path.join(EXPORT_FOLDER, '%s-apply.json' % safe))


def _resolve_op(cam, op_id):
    """Resolve an 's<setup>/o<op>' id back to the operation object, or None."""
    m = re.match(r's(\d+)/o(\d+)$', str(op_id or ''))
    if not m:
        return None
    si, oi = int(m.group(1)), int(m.group(2))
    if si >= cam.setups.count:
        return None
    setup = cam.setups.item(si)
    if oi >= setup.operations.count:
        return None
    return setup.operations.item(oi)


# ---------------------------------------------------------------------------
# Parameter read/write helpers
# ---------------------------------------------------------------------------

def _resolve_param(container, candidates):
    """Try each candidate name on the container's `parameters` collection.
    Returns the parameter object on first hit, else None."""
    if container is None:
        return None
    try:
        params = container.parameters
    except Exception:
        return None
    for name in candidates:
        try:
            p = params.itemByName(name)
            if p is not None:
                return p
        except Exception:
            continue
    return None


def _read_value(container, key):
    """Read a parameter value (the inner .value.value) in Fusion's internal
    units. Returns None when the parameter isn't exposed by this op/tool."""
    p = _resolve_param(container, PARAM_CANDIDATES.get(key, []))
    if p is None:
        return None
    try:
        return p.value.value
    except Exception:
        try:
            return p.value
        except Exception:
            return None


def _read_string(container, key):
    """Like _read_value but for string-valued parameters (e.g. tool_type)."""
    p = _resolve_param(container, PARAM_CANDIDATES.get(key, []))
    if p is None:
        return None
    try:
        return p.value.value
    except Exception:
        try:
            return p.value
        except Exception:
            return None


def _param_raw(params, name):
    """Read one exact-named CAM parameter's raw value (bool/number/string),
    or None if the op doesn't expose it."""
    try:
        p = params.itemByName(name)
    except Exception:
        return None
    if p is None:
        return None
    try:
        return p.value.value
    except Exception:
        try:
            return p.value
        except Exception:
            return None


def _set_value(container, key, new_value, unit=''):
    """Set a parameter, converting from `unit` to Fusion's fixed internal unit
    (cm for length, mm/min for feed rate, RPM for RPM), then verify the
    round-trip read-back. Returns (ok: bool, reason: str). ok=True only when
    the post-write read-back is within 1% of the requested value."""
    p = _resolve_param(container, PARAM_CANDIDATES.get(key, []))
    if p is None:
        return False, 'no candidate matched'
    pname = getattr(p, 'name', '?')

    if unit == 'mm':
        internal_value = new_value / 10.0       # mm -> cm
        display_factor = 10.0                    # cm -> mm
    elif unit == 'mm/min':
        internal_value = new_value               # mm/min stored as-is
        display_factor = 1.0
    else:  # unitless (RPM)
        internal_value = new_value
        display_factor = 1.0

    before_internal = None
    try:
        before_internal = p.value.value
    except Exception:
        pass

    write_method = None
    try:
        p.value.value = internal_value
        write_method = '.value.value'
    except Exception as ex_val:
        try:
            p.expression = '%g' % internal_value
            write_method = '.expression (internal units)'
        except Exception as ex_expr:
            return False, ('%s: .value.value=%g rejected (%s); .expression also failed (%s)'
                           % (pname, internal_value, ex_val, ex_expr))

    after_internal = None
    try:
        after_internal = p.value.value
    except Exception:
        return False, '%s: wrote via %s but post-write read failed' % (pname, write_method)

    after_display = after_internal * display_factor
    before_display = before_internal * display_factor if before_internal is not None else None

    if new_value == 0 or abs(after_display - new_value) / abs(new_value) > 0.01:
        return False, ('%s: wrote %g (internal) via %s, but value is now %g %s '
                       '(was %s %s before) - Fusion overrode the write'
                       % (pname, internal_value, write_method, after_display, unit,
                          before_display, unit))
    return True, ('%s = %g %s via %s (was %s %s)'
                 % (pname, new_value, unit, write_method, before_display, unit))


def _set_param_typed(op, name, spec):
    """Set an operation parameter by its EXACT Fusion name to a typed value.

    `spec` is one of:
        bool                     -> boolean parameter (doMultipleDepths, ...)
        str                      -> choice/string parameter (rampType, ...)
        int / float              -> numeric, written in INTERNAL units as-is
        {"value": x, "unit": u}  -> numeric, converted from u to internal:
                                    'mm'->cm(/10), 'in'->cm(*2.54),
                                    'mm/min'/'deg'/'' -> as-is

    Tries op.parameters first, then op.tool.parameters. Returns (ok, reason)."""
    p = None
    for container in (op, getattr(op, 'tool', None)):
        if container is None:
            continue
        try:
            cand = container.parameters.itemByName(name)
        except Exception:
            cand = None
        if cand is not None:
            p = cand
            break
    if p is None:
        return False, '%s: not found on op or tool' % name

    unit = None
    if isinstance(spec, dict):
        val = spec.get('value')
        unit = spec.get('unit')
    else:
        val = spec

    if isinstance(val, bool) or isinstance(val, str):
        internal = val
    elif isinstance(val, (int, float)):
        if unit == 'mm':
            internal = val / 10.0
        elif unit == 'in':
            internal = val * 2.54
        else:
            internal = val
    else:
        return False, '%s: unsupported value %r' % (name, val)

    before = None
    try:
        before = p.value.value
    except Exception:
        pass
    try:
        p.value.value = internal
    except Exception as e1:
        try:
            p.expression = str(internal)
        except Exception as e2:
            return False, '%s: write rejected (%s; %s)' % (name, e1, e2)
    try:
        after = p.value.value
    except Exception:
        return True, '%s set to %s (no read-back)' % (name, internal)

    if isinstance(internal, (bool, str)):
        ok = (after == internal)
    elif internal == 0:
        ok = abs(after) < 1e-9
    else:
        ok = abs(after - internal) / abs(internal) <= 0.02
    return ok, '%s %s: now %s (was %s)' % (name, 'OK' if ok else 'MISMATCH', after, before)


def _dump_op_param_names(op, limit=200):
    """Sorted list of all parameter names exposed on op.parameters. Used to
    figure out the right candidate when an apply fails."""
    names = []
    try:
        params = op.parameters
        for i in range(min(params.count, limit)):
            try:
                names.append(params.item(i).name)
            except Exception:
                pass
    except Exception:
        pass
    return sorted(names)


# ---------------------------------------------------------------------------
# Raw data extraction (no recommendation math - that's ioSenderV2's job)
# ---------------------------------------------------------------------------

def _extract_op_data(op):
    """Pull every feed/speed-relevant CURRENT value off an operation. All None
    values mean the parameter isn't exposed on this op (common for some
    strategies) - ioSenderV2 accounts for that."""
    data = {
        'name': getattr(op, 'name', '(unknown)'),
        'strategy': getattr(op, 'strategy', None),
        'tool_name': None,
        'tool_type': None,
        'diameter_mm': None,
        'flutes': None,
        'rpm': None,
        'cutting_feed': None,
        'plunge_feed': None,
        'axial_step': None,
        'radial_step': None,
        'coolant': None,
    }
    tool = getattr(op, 'tool', None)
    if tool is not None:
        try:
            data['tool_name'] = tool.parameters.itemByName('tool_description').value.value
        except Exception:
            try:
                data['tool_name'] = getattr(tool, 'description', None) or getattr(tool, 'name', None)
            except Exception:
                pass
        data['tool_type'] = _read_string(tool, 'tool_type')
        diam = _read_value(tool, 'diameter')
        if diam is not None:
            data['diameter_mm'] = float(diam) * 10.0   # cm -> mm
        data['flutes'] = _read_value(tool, 'flutes')
    # Fusion's CAM parameter API stores distance params (diameter, stepdown,
    # stepover) in cm (*10 -> mm), feed rate (cutting, plunge) in mm/min
    # directly, and spindle speed in RPM directly.
    data['rpm'] = _read_value(op, 'rpm')
    cf = _read_value(op, 'cutting_feed')
    pf = _read_value(op, 'plunge_feed')
    data['cutting_feed'] = float(cf) if cf is not None else None
    data['plunge_feed']  = float(pf) if pf is not None else None
    ax = _read_value(op, 'axial_step')
    rd = _read_value(op, 'radial_step')
    data['axial_step']  = float(ax) * 10.0 if ax is not None else None   # cm -> mm
    data['radial_step'] = float(rd) * 10.0 if rd is not None else None
    data['coolant'] = _read_string(op, 'coolant')
    return data


def _geometry_info(op):
    """Hole/depth/stock geometry so toolpath INTENT (hole diameter, cut depth,
    through-vs-blind, Z extents) is visible in the export without opening
    Fusion. Length params are stored in cm internally -> x10 for mm. Returns
    {} when the op exposes none of these params."""
    try:
        params = op.parameters
    except Exception:
        return {}

    def L(name):  # length param, cm -> mm
        v = _param_raw(params, name)
        return round(v * 10.0, 4) if isinstance(v, (int, float)) else None

    def R(name):  # raw value
        return _param_raw(params, name)

    geo = {}

    # --- Hole geometry (bore/drill/hole-based strategies) ---
    hmode = R('holeMode')
    dmin, dmax = L('holeDiameterMinimum'), L('holeDiameterMaximum')
    through = R('auto_holeIsThrough')
    if hmode is not None or dmin is not None or through is not None:
        hole = {}
        if hmode is not None:
            hole['mode'] = hmode
        if dmin is not None and dmax is not None and abs(dmin - dmax) < 1e-6:
            hole['diameter_mm'] = dmin
        elif dmin is not None or dmax is not None:
            hole['diameter_min_mm'] = dmin
            hole['diameter_max_mm'] = dmax
        if through is not None:
            hole['through'] = bool(through)
        n = R('numberOfHoles')
        if n is not None:
            hole['number_of_holes_filter'] = n
        geo['hole'] = hole

    # --- Depth/Z extents (most milling strategies) ---
    top_v, bot_v = L('topHeight_value'), L('bottomHeight_value')
    if top_v is not None or bot_v is not None:
        depth = {
            'top_mode': R('topHeight_mode'),
            'top_offset_mm': L('topHeight_offset'),
            'top_z_mm': top_v,
            'bottom_mode': R('bottomHeight_mode'),
            'bottom_offset_mm': L('bottomHeight_offset'),
            'bottom_z_mm': bot_v,
        }
        if top_v is not None and bot_v is not None:
            depth['cut_depth_mm'] = round(abs(top_v - bot_v), 4)
        geo['depth'] = depth

    # --- Stock Z extents + breakthrough check ---
    sz_hi, sz_lo = L('stockZHigh'), L('stockZLow')
    if sz_hi is not None or sz_lo is not None:
        geo['stock_z'] = {'top_mm': sz_hi, 'bottom_mm': sz_lo}
        if bool(through) and bot_v is not None and sz_lo is not None:
            geo['breakthrough_mm'] = round(sz_lo - bot_v, 4)

    return geo


def _op_json(si, oi, data):
    """One op's extracted CURRENT data as a JSON-safe dict - no recommendation
    or verdict fields; ioSenderV2 computes those from this raw data."""
    return {
        'id': 's%d/o%d' % (si, oi),
        'setup_index': si,
        'op_index': oi,
        'name': data.get('name'),
        'strategy': data.get('strategy'),
        'tool': {
            'name': data.get('tool_name'),
            'type': data.get('tool_type'),
            'diameter_mm': data.get('diameter_mm'),
            'flutes': data.get('flutes'),
        },
        'current': {
            'rpm': data.get('rpm'),
            'cutting_feed': data.get('cutting_feed'),
            'plunge_feed': data.get('plunge_feed'),
            'axial_step': data.get('axial_step'),
            'radial_step': data.get('radial_step'),
            'coolant': data.get('coolant'),
        },
    }


def _count_params(op_dict):
    """Rough 'how much did we successfully extract' figure for progress
    logging: count of non-None leaf values across the op's tool/current
    blocks (geometry isn't counted - it's a bonus, not the core extraction)."""
    n = 0
    for block in (op_dict.get('tool') or {}, op_dict.get('current') or {}):
        for v in block.values():
            if v is not None:
                n += 1
    return n


def _build_payload(cam):
    """Full export payload for the active CAM document - raw current values
    and geometry only, no recommendations. Logs each setup/operation and its
    extracted-parameter count to Fusion's Text Commands as it goes, since
    this can take a while on a slow machine with many setups/operations."""
    app = adsk.core.Application.get()
    setups = []
    op_count = 0
    for si in range(cam.setups.count):
        setup = cam.setups.item(si)
        app.log('Feeds and Speeds export: setup "%s" (%d operation(s))'
                % (setup.name, setup.operations.count))
        ops = []
        setup_params = 0
        for oi in range(setup.operations.count):
            op = setup.operations.item(oi)
            op_count += 1
            try:
                data = _extract_op_data(op)
                data['name'] = '%s → %s' % (setup.name, data['name'])
                op_dict = _op_json(si, oi, data)
                try:
                    geo = _geometry_info(op)
                    if geo:
                        op_dict['geometry'] = geo
                except Exception as gex:
                    op_dict['geometry'] = {'error': str(gex)}
                n = _count_params(op_dict)
                setup_params += n
                app.log('  operation "%s": %d parameter(s) extracted' % (op.name, n))
                ops.append(op_dict)
            except Exception as ex:
                app.log('  operation "%s": FAILED: %s' % (getattr(op, 'name', '?'), ex))
                ops.append({'id': 's%d/o%d' % (si, oi), 'setup_index': si,
                            'op_index': oi, 'error': str(ex)})
        app.log('  setup "%s" total: %d parameter(s) across %d operation(s)'
                % (setup.name, setup_params, setup.operations.count))
        setups.append({'index': si, 'name': setup.name, 'operations': ops})
    return {
        'document': cam.parentDocument.name,
        'op_count': op_count,
        'apply_schema': {
            'ops': [{
                'id': 's<setup>/o<op>',
                'set': {k: '<value>' for k in _APPLY_UNITS},
                'params': {
                    '<exactFusionName>': '<bool | "choice" | number>',
                    '_note': 'params applied in order; enable booleans '
                             '(doMultipleDepths) before gated fields '
                             '(maximumStepdown)',
                },
            }],
        },
        'setups': setups,
    }


# ---------------------------------------------------------------------------
# Public: export / apply
# ---------------------------------------------------------------------------

def apply_file_exists():
    """True if an apply file exists for the active CAM document."""
    cam = _active_cam()
    if cam is None:
        return False
    return os.path.exists(_paths(cam)[1])


def export_ops():
    """Write the export JSON for the active CAM doc. Returns
    (export_path, apply_path, op_count)."""
    cam = _active_cam()
    if cam is None:
        raise RuntimeError('no active CAM document (open the Manufacturing '
                           'workspace with at least one Setup)')
    os.makedirs(EXPORT_FOLDER, exist_ok=True)
    payload = _build_payload(cam)
    export_path, apply_path = _paths(cam)
    with open(export_path, 'w', encoding='utf-8') as f:
        json.dump(payload, f, indent=2, ensure_ascii=False)
    return export_path, apply_path, payload['op_count']


def apply_from_file():
    """Read the apply JSON and write values onto the matching ops, then
    regenerate those toolpaths. Returns (apply_path, result_lines). Logs each
    affected setup/operation to Fusion's Text Commands as it goes, since
    toolpath regeneration can take a while on a slow machine."""
    app = adsk.core.Application.get()
    cam = _active_cam()
    if cam is None:
        raise RuntimeError('no active CAM document')
    _, apply_path = _paths(cam)
    if not os.path.exists(apply_path):
        raise FileNotFoundError('no apply file at %s - export first, then have '
                                'ioSenderV2 write it' % apply_path)
    with open(apply_path, encoding='utf-8') as f:
        spec = json.load(f)

    ops_in_file = spec.get('ops', [])
    app.log('Feeds and Speeds apply: %d operation(s) in apply file' % len(ops_in_file))

    results = []
    modified = []
    for entry in ops_in_file:
        op_id = entry.get('id')
        op = _resolve_op(cam, op_id)
        if op is None:
            app.log('  %s: NOT FOUND in current document' % op_id)
            results.append('%s: NOT FOUND in current document' % op_id)
            continue
        m = re.match(r's(\d+)/o\d+$', op_id)
        setup_name = cam.setups.item(int(m.group(1))).name if m else '?'
        app.log('  setup "%s" / operation "%s" (%s)...' % (setup_name, op.name, op_id))
        touched = False
        for key, val in (entry.get('set') or {}).items():
            unit = _APPLY_UNITS.get(key)
            if unit is None:
                line = '%s.%s: unknown field (skipped)' % (op_id, key)
                results.append(line)
                app.log('    ' + line)
                continue
            try:
                ok, reason = _set_value(op, key, float(val), unit=unit)
            except Exception as ex:
                line = '%s.%s: ERROR %s' % (op_id, key, ex)
                results.append(line)
                app.log('    ' + line)
                continue
            line = '%s.%s %s: %s' % (op_id, key, 'OK' if ok else 'FAILED', reason)
            results.append(line)
            app.log('    ' + line)   # includes the requested value + before/after read-back (see _set_value)
            touched = touched or ok
        for name, spec_val in (entry.get('params') or {}).items():
            try:
                ok, reason = _set_param_typed(op, name, spec_val)
            except Exception as ex:
                line = '%s.%s: ERROR %s' % (op_id, name, ex)
                results.append(line)
                app.log('    ' + line)
                continue
            line = '%s.%s %s: %s' % (op_id, name, 'OK' if ok else 'FAILED', reason)
            results.append(line)
            app.log('    ' + line)
            touched = touched or ok
        if touched:
            modified.append(op)

    regen_failed = False
    for op in modified:
        try:
            app.log('  regenerating toolpath: "%s"...' % op.name)
            cam.generateToolpath(op)
        except Exception as ex:
            regen_failed = True
            results.append('regen %s: %s' % (getattr(op, 'name', '?'), ex))

    app.log('Feeds and Speeds apply: %d of %d operation(s) updated' % (len(modified), len(ops_in_file)))

    # Consume the apply file on a fully-clean run so stale values can't be
    # re-applied later. Keep it on ANY failure so it can be fixed and retried.
    failure_markers = ('FAILED', 'ERROR', 'NOT FOUND', 'unknown field')
    had_failure = any(m in line for line in results for m in failure_markers)
    if modified and not had_failure and not regen_failed:
        try:
            os.remove(apply_path)
            results.append('(apply file cleared: %s)' % apply_path)
        except Exception as ex:
            results.append('(apply file could not be deleted: %s)' % ex)
    elif had_failure or regen_failed:
        results.append('(apply file kept — some steps failed; fix and retry)')

    return apply_path, results


# ---------------------------------------------------------------------------
# Command UI - raw adsk.core.CommandInputs only, no framework
# ---------------------------------------------------------------------------

def _set_status(inputs, text):
    st = inputs.itemById('status') if inputs else None
    if st:
        st.text = text


def _cleanup_files(cam):
    """Delete this document's export/apply files if present. Called both when the command opens (a
    clean start - a stale apply file left over from an abandoned earlier cycle should never silently
    apply) and when it closes (nothing lingers in ~/Downloads/ioSenderV2/ once a cycle is done, whether
    that cycle actually applied or was cancelled)."""
    app = adsk.core.Application.get()
    export_path, apply_path = _paths(cam)
    for path in (export_path, apply_path):
        try:
            if os.path.exists(path):
                os.remove(path)
                app.log('Feeds and Speeds: removed %s' % path)
        except Exception as ex:
            app.log('Feeds and Speeds: could not remove %s: %s' % (path, ex))


def _run_export(inputs):
    try:
        export_path, apply_path, n = export_ops()
        msg = ('Exported %d operation(s).\n\n'
               'Read:  %s\n'
               'Apply: %s\n\n'
               'Open ioSenderV2 to analyze and write the apply file, then run '
               'this command again and press OK to apply it.' % (n, export_path, apply_path))
    except Exception as e:
        msg = 'Export failed: %s' % e
    _set_status(inputs, msg)


def _run_apply(inputs):
    try:
        _, results = apply_from_file()
        body = '\n'.join(results) if results else '(apply file listed no ops)'
        if any('apply file cleared' in r for r in results):
            head = 'APPLIED — all steps OK, apply file cleared.'
        elif any('apply file kept' in r for r in results):
            head = 'APPLIED WITH ERRORS — apply file kept; fix and retry.'
        else:
            head = 'Apply finished.'
        msg = '%s\n\n%s' % (head, body)
    except Exception as e:
        msg = 'APPLY FAILED: %s' % e
    _set_status(inputs, msg)
    try:
        adsk.core.Application.get().userInterface.messageBox(msg, 'Feeds and Speeds — Apply')
    except Exception:
        pass


class CommandCreatedHandler(adsk.core.CommandCreatedEventHandler):
    def notify(self, args):
        app = adsk.core.Application.get()
        ui = app.userInterface
        try:
            cmd = args.command
            inputs = cmd.commandInputs

            inputs.addTextBoxCommandInput('status', 'Status', 'Exporting...', 7, True)

            # Clean start: a stale export/apply file from an abandoned earlier cycle should never
            # silently carry over (e.g. an old apply file from a cycle the user never came back to
            # finish must not get applied against a document that's since moved on).
            cam = _active_cam()
            if cam is not None:
                _cleanup_files(cam)

            # Export runs immediately on open - no Action picker to choose first. If ioSenderV2 already
            # wrote an apply file for this document (a previous Export -> ioSenderV2 round-trip), OK
            # applies it; otherwise OK just closes (nothing to do yet).
            _run_export(inputs)

            on_execute = ExecuteHandler()
            cmd.execute.add(on_execute)
            _handlers.append(on_execute)

            # destroy fires on close for ANY reason (OK, Cancel, Escape) - after execute for OK, so
            # apply_from_file() has already had its chance to read the apply file. Deletes both files so
            # nothing lingers once this cycle is done, whether it actually applied or was cancelled.
            on_destroy = DestroyHandler()
            cmd.destroy.add(on_destroy)
            _handlers.append(on_destroy)
        except Exception:
            ui.messageBox('Feeds and Speeds failed:\n{}'.format(traceback.format_exc()), CMD_NAME)


class ExecuteHandler(adsk.core.CommandEventHandler):
    def notify(self, args):
        try:
            if apply_file_exists():
                _run_apply(args.command.commandInputs)
            # No apply file yet (still waiting on ioSenderV2, or already applied) - OK just closes.
        except Exception:
            app = adsk.core.Application.get()
            app.userInterface.messageBox(
                'Feeds and Speeds failed:\n{}'.format(traceback.format_exc()), CMD_NAME)


class DestroyHandler(adsk.core.CommandEventHandler):
    def notify(self, args):
        try:
            cam = _active_cam()
            if cam is not None:
                _cleanup_files(cam)
        except Exception:
            pass   # best-effort cleanup - never block the dialog from closing


def create_command_definition(ui):
    """Create (or recreate) this command's CommandDefinition. Called by
    ioSenderV2.py's run() once at add-in startup; does not touch any
    panel/dropdown placement - the caller adds the returned def to its own
    toolbar control."""
    cmd_defs = ui.commandDefinitions

    cmd_def = cmd_defs.itemById(CMD_ID)
    if cmd_def:
        cmd_def.deleteMe()
    cmd_def = cmd_defs.addButtonDefinition(CMD_ID, CMD_NAME, CMD_DESC)

    on_created = CommandCreatedHandler()
    cmd_def.commandCreated.add(on_created)
    _handlers.append(on_created)

    return cmd_def


def cleanup(ui):
    """Delete this command's CommandDefinition. Called by ioSenderV2.py's
    stop(); the caller is responsible for removing any toolbar control that
    referenced it first."""
    cmd_def = ui.commandDefinitions.itemById(CMD_ID)
    if cmd_def:
        cmd_def.deleteMe()
    _handlers.clear()
