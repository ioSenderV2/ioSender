#!/usr/bin/env python3
"""Derive the shipped "DefaultV3.0" UI preset from Default-App.config.

A shipped preset called "the 3.0 default" must BE the 3.0 default, not a snapshot of
somebody's screen that happened to be taken on a 3.0 build. Snapshots go stale silently -
the default moves, the preset does not, and the operator who picks it to "see the new look"
gets last month's arrangement with nothing reporting the difference.

So it is generated, never hand-maintained: this reads Default-App.config (the one authority
for the factory look, and the source of Reset to Default), takes exactly the sections and Core
elements ConfigOverlay classifies as carried, and writes the preset beside the exe's other
shipped ones.

An element the classified list names but Default-App.config does NOT carry is a FAILURE, not
something to paper over with an assumed default: the preset would silently fail to set it, so
switching to "the default look" from Classic would leave the classic run strip turned on. Add
it to Default-App.config with its factory value instead.

Usage:  python tools/make-default-preset.py            # regenerate
        python tools/make-default-preset.py --check     # exit 1 if the shipped file is stale
"""

import os
import re
import sys
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

DEFAULT_CONFIG = os.path.join(REPO, 'ioSender XL', 'ioSender XL', 'Default-App.config')
OVERLAY = os.path.join(REPO, 'CNC Core', 'CNC Core', 'ConfigOverlay.cs')
OUTPUT = os.path.join(REPO, 'ioSender XL', 'ioSender XL', 'Presets', 'DefaultV3.0.ioconfig')

PRESET_NAME = 'DefaultV3.0'


def array_items(source, name):
    # Strip // comments first: the declarations are commented heavily and the prose quotes words
    # ("classic", "the 3.0 default"), which a bare literal scan would read as list entries.
    source = re.sub(r'//[^\n]*', '', source)
    m = re.search(r'\b' + re.escape(name) + r'\s*=\s*\{(.*?)\}\s*;', source, re.S)
    if not m:
        raise SystemExit('could not find %s in ConfigOverlay.cs' % name)
    return [s for s in re.findall(r'"([^"]+)"', m.group(1))]


def read_text(path):
    with open(path, 'r', encoding='utf-8-sig') as f:
        return f.read()


def build():
    overlay = read_text(OVERLAY)
    sections = array_items(overlay, 'UiLayoutSections')
    elements = array_items(overlay, 'UiLayoutCoreElements')

    src = ET.parse(DEFAULT_CONFIG).getroot()

    root = ET.Element('AppConfig', {'version': '2', 'name': PRESET_NAME})

    # The layout sections, copied wholesale - same as ConfigOverlay.Extract does.
    for key in sections:
        sec = next((s for s in src.findall('section') if s.get('key') == key), None)
        if sec is None or len(sec) == 0:
            raise SystemExit('Default-App.config has no "%s" section - cannot build the preset' % key)
        root.append(sec)

    # Core as a MERGE section carrying only the classified elements. Replace would drag the whole
    # factory preference set (and the machine) along with the arrangement.
    core = next((s for s in src.findall('section') if s.get('key') == 'Core'), None)
    if core is None or len(core) == 0:
        raise SystemExit('Default-App.config has no "Core" section')

    payload_src = core[0]
    payload = ET.Element(payload_src.tag)

    missing = []
    for name in elements:
        found = payload_src.findall(name)
        if not found:
            missing.append(name)
            continue
        for el in found:
            payload.append(el)

    if missing:
        print('Default-App.config is missing classified UI elements:')
        for name in missing:
            print('    %s' % name)
        print()
        print('A preset cannot set what the factory default does not state, so switching to')
        print('"%s" would silently leave these at whatever the previous preset set.' % PRESET_NAME)
        print('Add each to Default-App.config with its factory value (see the Config field')
        print('initialisers in AppConfig.cs), then re-run.')
        raise SystemExit(1)

    sec = ET.SubElement(root, 'section', {'key': 'Core', 'mode': 'merge'})
    sec.append(payload)

    ET.indent(root, space='  ')
    return '<?xml version="1.0" encoding="utf-8"?>\n' + ET.tostring(root, encoding='unicode') + '\n'


def main():
    text = build()
    check = '--check' in sys.argv

    current = read_text(OUTPUT) if os.path.exists(OUTPUT) else None

    if current == text:
        print('%s is up to date with Default-App.config.' % os.path.basename(OUTPUT))
        return 0

    if check:
        print('%s is STALE - Default-App.config has moved since it was generated.' % os.path.basename(OUTPUT))
        print('Run: python tools/make-default-preset.py')
        return 1

    os.makedirs(os.path.dirname(OUTPUT), exist_ok=True)
    with open(OUTPUT, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)
    print('Wrote %s' % os.path.relpath(OUTPUT, REPO))
    return 0


if __name__ == '__main__':
    sys.exit(main())
