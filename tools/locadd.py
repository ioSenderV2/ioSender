#!/usr/bin/env python3
"""locadd.py - add English-baseline LocBaml rows for x:Uid'd controls to every locale CSV.

ioSender localizes via LocBaml: each x:Uid'd control needs one row per localizable property in
every Locale/<loc>/csv/<assembly>.resources.<loc>.csv. New features (the wizards, Height Map, ...)
were x:Uid'd in XAML but never had rows added, so they fall back to English in other languages.

This parses the listed XAML files, derives the LocBaml row for each localizable property (property
path + category matched to how LocBaml emits them - confirmed against existing rows), and appends the
ones not already present to ALL seven locale CSVs with the ENGLISH text as the baseline value (the
first column is always the en-US resource name even in the other-language files - that's how LocBaml
keys them). Translators then translate the value column; satellites are regenerated externally with
LocBaml. Idempotent: re-running adds nothing new.

Adding is only half of it. Re-wording a string that ALREADY has a row changes the XAML and nothing
else - and the CSV value is what ships, so the new wording sits in the source, absent from the app,
with nothing reporting the difference. --sync is the pass that catches that: it refreshes rows whose
English source has changed, but only where the stored value is still the untranslated English
baseline. A row someone has actually translated is reported and left alone.

Usage:  python tools/locadd.py                   # add missing rows
        python tools/locadd.py --sync            # ...and refresh re-worded English
        python tools/locadd.py --sync --dry-run  # show what both passes would do
"""

import csv
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCALES = ['de-DE', 'en-US', 'hu-HU', 'pt-BR', 'ru-RU', 'uk-UA', 'zh-CN']

# XAML view files to scan, paired with their built assembly's resource base name.
# Idempotent: listing a file that's fully localized adds nothing; new x:Uids get backfilled.
TARGETS = [
    # ioSender (ioSender XL views: MainWindow + the top-level view tabs)
    ('ioSender XL/ioSender XL/HeightMapView.xaml', 'ioSender'),
    ('ioSender XL/ioSender XL/StartJobView.xaml', 'ioSender'),
    ('ioSender XL/ioSender XL/JobView.xaml', 'ioSender'),
    ('ioSender XL/ioSender XL/JobWorkspace.xaml', 'ioSender'),
    ('ioSender XL/ioSender XL/ProgramPanel.xaml', 'ioSender'),
    ('ioSender XL/ioSender XL/MainWindow.xaml', 'ioSender'),
    ('ioSender XL/ioSender XL/MirrorWindow.xaml', 'ioSender'),

    # CNC.Controls.WPF (the main controls library)
    ('CNC Controls/CNC Controls/JobControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/RunStripPanel.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/StatusControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/PortDialog.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/AutoSquareWizard.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/AutoSquareProbeWizard.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/StepperCalibrationProbeWizard.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/StepperCalibrationScratchWizard.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/ToolView.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/WorkOrderView.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/ProgramView.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/OddJobsFeedsSpeedsDialog.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/TrinamicView.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/PIDLogView.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/SimulatorConfigView.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/MachineSetupWizard.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/CalibrationView.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/FixtureEditDialog.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/GrblConfigView.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/SettingsNavShell.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/ErrorsAndAlarms.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/GrblConfigControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/BasicConfigControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/UiGeneralConfigControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/UiRemoteConfigControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/OddJobsSettingsControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/CustomToolEditDialog.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/KbdDefaultSpeedControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/JogConfigControl.xaml', 'CNC.Controls.WPF'),
    # Was localized (30 rows already in the CSVs) but never listed here, so new x:Uids on it were
    # silently skipped - the run reports "Added 0 row(s)" and looks like a no-op rather than a gap.
    ('CNC Controls/CNC Controls/JogUiConfigControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/JogBaseControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/ConsoleControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/FileActionControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/GCodeListControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/KeyMapEditor.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/MainPageEditor.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/MPGPending.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/OffsetFlyout.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/FeedsAndSpeedsView.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/MacroExecuteControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/PanelFlyout.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/PendingChangesDialog.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/ProbeDefinitionEditDialog.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/FixtureEditDialog.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/ProbeDefinitionsDialog.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/ProbeMotionParamsDialog.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/SignalsControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/RestorePointDialog.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/OffsetRestoreDialog.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/ResetReproDialog.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/DROControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/DROBaseControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/JogControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/SpindleControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/CoolantControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/FeedControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/MDIControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/OverrideControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/OriginControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/OffsetView.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/ErrorsAndAlarms.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/GotoBaseControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/OutlineBaseControl.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/MacroManagerDialog.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/MacroEditor.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/SDCardView.xaml', 'CNC.Controls.WPF'),

    # CNC.Controls.Probing (the probing tab library - redesign + Start Job probing control)
    ('CNC Controls Probing/CNC Controls Probing/StartJobControl.xaml', 'CNC.Controls.Probing'),
    ('CNC Controls Probing/CNC Controls Probing/ProbingView.xaml', 'CNC.Controls.Probing'),
    ('CNC Controls Probing/CNC Controls Probing/EdgeFinderControl.xaml', 'CNC.Controls.Probing'),
    ('CNC Controls Probing/CNC Controls Probing/EdgeFinderIntControl.xaml', 'CNC.Controls.Probing'),
    ('CNC Controls Probing/CNC Controls Probing/CenterFinderControl.xaml', 'CNC.Controls.Probing'),
    ('CNC Controls Probing/CNC Controls Probing/RotationControl.xaml', 'CNC.Controls.Probing'),
    ('CNC Controls Probing/CNC Controls Probing/ToolLengthControl.xaml', 'CNC.Controls.Probing'),

    # CNC.Controls.Viewer (the 3D/carve viewer)
    ('CNC GCodeViewer/CNC GCodeViewer/CarveView.xaml', 'CNC.Controls.Viewer'),
    ('CNC GCodeViewer/CNC GCodeViewer/ViewOptionsDialog.xaml', 'CNC.Controls.Viewer'),

    # CNC.Controls.Camera (the camera view + its App-settings panel)
    ('CNC Controls Camera/CNC Controls Camera/ConfigControl.xaml', 'CNC.Controls.Camera'),

    # CNC.Converters (the file-import converters' own parameter dialogs)
    ('CNC Converters/SvgLaserDialog.xaml', 'CNC.Converters'),
]

# LibStrings.xaml ResourceDictionaries (code-string localization). Each <system:String x:Uid=..>value..
# entry becomes a libstrings.baml row (System.String.$Content). Paired with the built assembly.
LIBSTRINGS = [
    ('CNC Controls/CNC Controls/LibStrings.xaml', 'CNC.Controls.WPF'),
]

STR_RE = re.compile(r'<system:String\b[^>]*?\bx:Uid="([^"]+)"[^>]*?>(.*?)</system:String>', re.DOTALL)

# Localizable attributes we extract, in a stable order.
ATTRS = ['Content', 'Header', 'Label', 'Unit', 'Text', 'ToolTip']

# Content-bearing controls and the LocBaml category each gets (verified against existing rows).
CONTENT_CATEGORY = {'Button': 'Button', 'CheckBox': 'CheckBox', 'RadioButton': 'RadioButton', 'Label': 'Label'}


def prop_for(tag, attr):
    """(property-path, category, readable, modifiable) for a (control, attribute), or None to skip."""
    if tag == 'NumericField':
        if attr == 'Label':
            return ('CNC.Controls.NumericField.Label', 'None', 'False', 'True')
        if attr == 'Unit':
            return ('CNC.Controls.NumericField.Unit', 'None', 'False', 'True')
        if attr == 'ToolTip':
            return ('System.Windows.FrameworkElement.ToolTip', 'ToolTip', 'True', 'True')
        return None
    if attr == 'ToolTip':
        return ('System.Windows.FrameworkElement.ToolTip', 'ToolTip', 'True', 'True')
    if attr == 'Header' and tag in ('GroupBox', 'Expander', 'TabItem', 'HeaderedContentControl'):
        return ('System.Windows.Controls.HeaderedContentControl.Header', 'Label', 'True', 'True')
    if attr == 'Header' and tag == 'MenuItem':
        return ('System.Windows.Controls.HeaderedItemsControl.Header', 'Menu', 'True', 'True')
    if attr == 'Content' and tag in CONTENT_CATEGORY:
        return ('System.Windows.Controls.ContentControl.Content', CONTENT_CATEGORY[tag], 'True', 'True')
    if attr == 'Text' and tag == 'TextBlock':
        return ('System.Windows.Controls.TextBlock.Text', 'Text', 'True', 'True')
    if attr == 'Text' and tag == 'Run':
        # Inline runs inside a TextBlock - used where part of a sentence needs its own styling, or
        # where a literal phrase sits next to a data-bound one (a bound value can't be localized, so
        # the prose has to be its own Run to stay reachable).
        return ('System.Windows.Documents.Run.Text', 'Text', 'True', 'True')
    return None


ELEM_RE = re.compile(r'<([\w.:]+)\b([^>]*?\bx:Uid="([^"]+)"[^>]*?)/?>', re.DOTALL)


def attr_val(blob, name):
    m = re.search(r'\b' + name + r'="([^"]*)"', blob)
    return m.group(1) if m else None


def baml_name(xaml_path):
    return os.path.splitext(os.path.basename(xaml_path))[0].lower() + '.baml'


def rows_for(xaml_path, assembly):
    """List of (key, row) for an XAML file. key = (resname, uid:path); row = the 7 CSV fields."""
    with open(os.path.join(REPO, xaml_path), encoding='utf-8') as f:
        text = f.read()
    resname = '%s.g.en-US.resources:%s' % (assembly, baml_name(xaml_path))
    out = []
    seen = set()
    for m in ELEM_RE.finditer(text):
        tag = m.group(1).split(':')[-1]   # strip xmlns prefix
        blob, uid = m.group(2), m.group(3)
        for attr in ATTRS:
            val = attr_val(blob, attr)
            if val is None or val.strip() == '' or val.lstrip().startswith('{'):
                continue   # absent, empty, or a binding/markup-extension
            pp = prop_for(tag, attr)
            if not pp:
                continue
            path, cat, readable, modifiable = pp
            field1 = '%s:%s' % (uid, path)
            key = (resname, field1)
            if key in seen:
                continue
            seen.add(key)
            out.append((key, [resname, field1, cat, readable, modifiable, '', val]))
    return out


def rows_for_libstrings(xaml_path, assembly):
    """Rows for a LibStrings.xaml ResourceDictionary: each <system:String x:Uid=..>value entry."""
    with open(os.path.join(REPO, xaml_path), encoding='utf-8') as f:
        text = f.read()
    resname = '%s.g.en-US.resources:%s' % (assembly, baml_name(xaml_path))
    out, seen = [], set()
    for m in STR_RE.finditer(text):
        uid, val = m.group(1), m.group(2)
        field1 = '%s:System.String.$Content' % uid
        key = (resname, field1)
        if key in seen:
            continue
        seen.add(key)
        out.append((key, [resname, field1, 'None', 'True', 'True', '', val]))
    return out


def existing_keys(path):
    keys = set()
    if not os.path.exists(path):
        return keys
    with open(path, encoding='utf-8-sig', newline='') as f:
        for r in csv.reader(f):
            if len(r) >= 2:
                keys.add((r[0], r[1]))
    return keys


def read_rows(path):
    """Every row, in file order. Used to index existing values; the writer below does NOT round-trip
       through this - see sync_values."""
    rows = []
    if not os.path.exists(path):
        return rows
    with open(path, encoding='utf-8-sig', newline='') as f:
        for r in csv.reader(f):
            rows.append(r)
    return rows


def serialize_row(row):
    """One row, encoded exactly as the add pass would append it."""
    import io
    buf = io.StringIO()
    csv.writer(buf, lineterminator='', quoting=csv.QUOTE_MINIMAL).writerow(row)
    return buf.getvalue()


def sync_values(path, rows, baseline, dry):
    """Refresh rows whose ENGLISH SOURCE STRING HAS CHANGED since the row was added.

    The gap this closes: the add pass only ever appends rows that are missing, so editing the wording of
    a control that already HAS a row changes the XAML and nothing else - and the CSV value wins at
    runtime. The new text is then in the source, absent from the app, and nothing reports it. That is how
    a reworded string can look like it simply did not take effect.

    Only rows that are still UNTRANSLATED are touched: a locale row qualifies when its current value is
    byte-identical to the en-US baseline's value for the same key, which is what an English-seeded row
    looks like. Anything that has genuinely been translated differs from the baseline, is left exactly as
    it is, and is reported instead - a translator's work is not ours to overwrite.
    """
    if not os.path.exists(path):
        return 0, []

    want = {key: row[6] for (key, row) in rows}
    changed, skipped = 0, []

    # LINE-SURGICAL, deliberately. The obvious implementation - parse the whole CSV, edit the rows,
    # write it back - produced a diff touching rows this pass never looked at: csv.writer re-quotes to
    # its own rules, which are not always how the line was written originally, and the round trip also
    # ate the file's BOM. Both are invisible in a value-by-value comparison and both showed up as churn
    # across a 1500-line file. So: read the raw text, rewrite ONLY the lines whose value changes, and
    # leave every other byte exactly as it was.
    with open(path, 'rb') as f:
        raw = f.read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    text = raw.decode('utf-8-sig')
    nl = '\r\n' if '\r\n' in text else '\n'
    lines = text.split(nl)

    for i, line in enumerate(lines):
        if not line.strip():
            continue
        try:
            r = next(csv.reader([line]))
        except Exception:
            continue
        if len(r) < 7:
            continue
        key = (r[0], r[1])

        # THE VALUE IS EVERYTHING FROM FIELD 6 ON, not field 6. Some rows in these files were written
        # with an UNQUOTED comma inside the value, so csv.reader splits one value across several fields.
        # Treating r[6] as the whole value and re-serialising the row then writes the new value AND the
        # leftover fragments: "Dry run - spindle/coolant off, tool changes skipped...", tool changes
        # skipped... - a corrupted row that still parses. It silently mangled 273 rows across 27 files
        # on 2026-09-21 before the diff was read.
        value = ','.join(r[6:]) if len(r) > 7 else r[6]

        if key not in want or value == want[key]:
            continue
        # Translated away from the English baseline? Leave it and say so.
        base = baseline.get(key)
        if base is not None and value != base:
            skipped.append(key[1])
            continue
        # Rebuild as exactly seven fields, so the rewritten row is well-formed however the old one was.
        lines[i] = serialize_row(r[:6] + [want[key]])
        changed += 1

    if changed and not dry:
        out = nl.join(lines)
        with open(path, 'wb') as f:
            if bom:
                f.write(b'\xef\xbb\xbf')
            f.write(out.encode('utf-8'))

    return changed, skipped


def baseline_values(assembly):
    """The en-US CSV's values, which are the English baseline every other locale is seeded from."""
    path = os.path.join(REPO, 'Locale', 'en-US', 'csv', '%s.resources.en-US.csv' % assembly)
    out = {}
    for r in read_rows(path):
        if len(r) >= 7:
            # Same unquoted-comma hazard as sync_values - the value is fields 6 onward, not field 6, and
            # a truncated baseline here would make every such row look "translated" and be skipped.
            out[(r[0], r[1])] = ','.join(r[6:]) if len(r) > 7 else r[6]
    return out


def main():
    dry = '--dry-run' in sys.argv
    sync = '--sync' in sys.argv
    # --only <substring>: restrict BOTH passes to matching XAML paths. --sync over the whole repo turns up
    # hundreds of rows whose English drifted years apart, which is a real finding but not something to
    # bundle into whatever change is in hand - one file at a time keeps the diff reviewable.
    only = None
    if '--only' in sys.argv:
        i = sys.argv.index('--only')
        if i + 1 < len(sys.argv):
            only = sys.argv[i + 1].lower()
    grand = 0
    synced = 0
    stale = []
    # A view's own XAML can carry BOTH localizable controls and <system:String> resource entries - JobView
    # does - so every target gets both extractors. Running only rows_for over TARGETS silently skipped the
    # string resources, which is how new <system:String> entries were being added with zero locale rows.
    jobs = ([(x, a, rows_for) for (x, a) in TARGETS] +
            [(x, a, rows_for_libstrings) for (x, a) in TARGETS] +
            [(x, a, rows_for_libstrings) for (x, a) in LIBSTRINGS])
    for xaml, assembly, builder in jobs:
        if only and only not in xaml.lower():
            continue
        if not os.path.exists(os.path.join(REPO, xaml)):
            print('  skip (missing): %s' % xaml)
            continue
        rows = builder(xaml, assembly)
        base = baseline_values(assembly) if sync else None
        for loc in LOCALES:
            path = os.path.join(REPO, 'Locale', loc, 'csv', '%s.resources.%s.csv' % (assembly, loc))

            # Re-word an existing string and the ADD pass sees nothing to do - the row is already there.
            # The CSV value is what ships, so the new wording would sit in the XAML, absent from the app,
            # with nothing reporting it. --sync is the pass that catches that.
            if sync:
                n, skipped = sync_values(path, rows, base, dry)
                if n:
                    synced += n
                    print('%-45s %s  ~%d' % (os.path.basename(xaml), loc, n))
                for k in skipped:
                    stale.append('%s  %s  %s' % (loc, os.path.basename(xaml), k))

            have = existing_keys(path)
            new = [row for (key, row) in rows if key not in have]
            if not new:
                continue
            grand += len(new)
            print('%-45s %s  +%d' % (os.path.basename(xaml), loc, len(new)))
            if dry:
                for row in new:
                    print('      ', row[1], '=', row[6])
                continue
            with open(path, 'a', encoding='utf-8', newline='') as f:
                w = csv.writer(f, lineterminator='\n', quoting=csv.QUOTE_MINIMAL)
                for row in new:
                    w.writerow(row)
    print('%s %d row(s) across %d locales.' % ('Would add' if dry else 'Added', grand, len(LOCALES)))
    if sync:
        print('%s %d changed English value(s).' % ('Would update' if dry else 'Updated', synced))
        if stale:
            # Reported, never overwritten. These have been translated away from the English baseline, so
            # the English has moved on and the translation now describes the old behaviour - a person has
            # to decide what the new sentence is in that language.
            print('\n%d row(s) TRANSLATED and now out of date - left alone, they need a translator:' % len(stale))
            for s in stale:
                print('   ', s)


if __name__ == '__main__':
    main()
