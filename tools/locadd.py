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

Usage:  python tools/locadd.py            # apply
        python tools/locadd.py --dry-run  # show what would be added
"""

import csv
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCALES = ['de-DE', 'en-US', 'hu-HU', 'pt-BR', 'ru-RU', 'uk-UA', 'zh-CN']

# XAML files to scan, paired with their built assembly's resource base name.
TARGETS = [
    ('ioSender XL/ioSender XL/HeightMapView.xaml', 'ioSender'),
    ('ioSender XL/ioSender XL/LoadStockView.xaml', 'ioSender'),
    ('CNC Controls/CNC Controls/SurfaceSpoilboardWizard.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/AutoSquareWizard.xaml', 'CNC.Controls.WPF'),
    ('CNC Controls/CNC Controls/StepperCalibrationScratchWizard.xaml', 'CNC.Controls.WPF'),
]

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
    if attr == 'Header' and tag in ('GroupBox', 'Expander'):
        return ('System.Windows.Controls.HeaderedContentControl.Header', 'Label', 'True', 'True')
    if attr == 'Content' and tag in CONTENT_CATEGORY:
        return ('System.Windows.Controls.ContentControl.Content', CONTENT_CATEGORY[tag], 'True', 'True')
    if attr == 'Text' and tag == 'TextBlock':
        return ('System.Windows.Controls.TextBlock.Text', 'Text', 'True', 'True')
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


def existing_keys(path):
    keys = set()
    if not os.path.exists(path):
        return keys
    with open(path, encoding='utf-8-sig', newline='') as f:
        for r in csv.reader(f):
            if len(r) >= 2:
                keys.add((r[0], r[1]))
    return keys


def main():
    dry = '--dry-run' in sys.argv
    grand = 0
    for xaml, assembly in TARGETS:
        rows = rows_for(xaml, assembly)
        for loc in LOCALES:
            path = os.path.join(REPO, 'Locale', loc, 'csv', '%s.resources.%s.csv' % (assembly, loc))
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


if __name__ == '__main__':
    main()
