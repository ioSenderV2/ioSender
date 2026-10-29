#!/usr/bin/env python3
"""Every Settings > User Interface setting must be classified for UI presets.

THE RULE (see ConfigOverlay.UiLayoutCoreElements): a preset carries everything those pages can
change, minus a named exclusion list. The failure mode this guards against is not a wrong
decision - it is a SILENT one. TabsKeys went missing, then the three panel lists, then the
Job-tab switches, each discovered only when a preset failed to reproduce a screen, because
nothing anywhere said "this setting exists and nobody decided about it".

So: parse the bindings off the User Interface settings pages, parse the two lists out of
ConfigOverlay.cs, and fail on anything in neither. Adding a checkbox to one of those pages now
fails this check until someone writes the setting into one list or the other.

Usage:  python tools/check-preset-coverage.py          # exit 1 if anything is unclassified
        python tools/check-preset-coverage.py --list    # print the full classification
"""

import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# The panels reached from Settings > User Interface. Their bound Config properties are the
# surface this rule is about. The two inline editors (KeyMapEditor, MainPageEditor) are NOT
# scanned for bindings: they write through code, and what they write is covered by the layout
# sections and the excluded-sections list instead.
PAGES = [
    'CNC Controls/CNC Controls/UiGeneralConfigControl.xaml',
    'CNC Controls/CNC Controls/UiSoundConfigControl.xaml',
    'CNC Controls/CNC Controls/UiRemoteConfigControl.xaml',
    'CNC Controls/CNC Controls/MainPageEditor.xaml',
]

# Sound choices are set in code-behind rather than bound in XAML (the combos carry ids), so the
# properties they write are declared here with the file that writes them, and verified to exist.
CODE_WRITTEN = {
    'CNC Controls/CNC Controls/UiSoundConfigControl.xaml.cs': ['SoundOnLimit', 'SoundOnProbe', 'SoundOnAlarm'],
    'CNC Controls/CNC Controls/KeyMapEditor.xaml.cs': ['TabShortcuts', 'ActionShortcuts'],
}

OVERLAY = 'CNC Core/CNC Core/ConfigOverlay.cs'
CONFIG = 'CNC Controls/CNC Controls/AppConfig.cs'

# Bindings that are not Config settings at all - view-model state the page reads for display.
NOT_SETTINGS = {'FeedrateUnit', 'IsJobRunning', 'UiScalePercent'}


def read(rel):
    with open(os.path.join(REPO, rel), 'r', encoding='utf-8-sig') as f:
        return f.read()


def array_items(source, name):
    """The string literals of a `static readonly string[] <name> = { ... };` declaration."""
    # Strip // comments first: the declarations are commented heavily and the prose quotes words
    # ("classic", "the 3.0 default"), which a bare literal scan would read as list entries.
    source = re.sub(r'//[^\n]*', '', source)
    m = re.search(r'\b' + re.escape(name) + r'\s*=\s*\{(.*?)\}\s*;', source, re.S)
    if not m:
        raise SystemExit('could not find %s in %s' % (name, OVERLAY))
    return set(re.findall(r'"([^"]+)"', m.group(1)))


def bound_properties():
    """Config property names bound by the User Interface pages, with where each came from."""
    found = {}
    for page in PAGES:
        text = read(page)
        for name in re.findall(r'Path=(?:Base\.)?([A-Za-z_][A-Za-z0-9_]*)', text):
            if name not in NOT_SETTINGS:
                found.setdefault(name, page)

    for path, names in CODE_WRITTEN.items():
        text = read(path)
        for name in names:
            if name not in text:
                raise SystemExit('%s no longer writes %s - update CODE_WRITTEN' % (path, name))
            found.setdefault(name, path)

    return found


def main():
    overlay = read(OVERLAY)
    included = array_items(overlay, 'UiLayoutCoreElements')
    excluded = array_items(overlay, 'UiLayoutExcluded')

    config = read(CONFIG)
    found = bound_properties()

    unclassified = []
    phantom = []
    for name, where in sorted(found.items()):
        if name in included or name in excluded:
            # A classified name must still be a real Config property, or the list is describing
            # something that no longer exists and the preset silently carries nothing.
            if not re.search(r'\bpublic\s+[A-Za-z0-9_<>\[\]?]+\s+' + re.escape(name) + r'\b', config):
                phantom.append((name, where))
            continue
        unclassified.append((name, where))

    if '--list' in sys.argv:
        for name, where in sorted(found.items()):
            state = 'IN ' if name in included else ('out' if name in excluded else '???')
            print('%-3s %-32s %s' % (state, name, where))
        print()

    # Classified names that no page binds are fine: the layout lists (TabsKeys and friends) are
    # written by the editors, not bound on a page. Only the two failures below matter.
    for name, where in phantom:
        print('CLASSIFIED BUT NOT A Config PROPERTY: %s (bound by %s)' % (name, where))

    for name, where in unclassified:
        print('UNCLASSIFIED UI SETTING: %s' % name)
        print('    bound by %s' % where)
        print('    add it to UiLayoutCoreElements (a preset carries it) or UiLayoutExcluded')
        print('    (with the reason it must not travel) in %s' % OVERLAY)

    if unclassified or phantom:
        print()
        print('%d unclassified, %d phantom. See the rule on UiLayoutCoreElements.'
              % (len(unclassified), len(phantom)))
        return 1

    print('Preset coverage OK - %d UI settings, all classified (%d carried, %d excluded).'
          % (len(found),
             len([n for n in found if n in included]),
             len([n for n in found if n in excluded])))
    return 0


if __name__ == '__main__':
    sys.exit(main())
