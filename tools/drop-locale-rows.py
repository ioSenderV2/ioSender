#!/usr/bin/env python3
"""drop-locale-rows.py - remove locale CSV rows whose x:Uid no longer exists in any XAML.

locadd.py only ADDS rows. When a control is deleted its rows stay behind, and nothing reports
them - so they accumulate silently, which is the same failure mode as a missing row, just quieter.

This works on BYTES and only ever drops whole lines, so the BOM state and the CRLF/LF mix of each
file survive untouched. That is deliberate: PS 5.1's -Encoding utf8 writes a BOM and Get-Content
-Raw reads BOM-less UTF-8 as ANSI, and a bulk rewrite of these files once mojibaked 273 rows.
Nothing here re-encodes anything.

It also refuses to drop a prefix that is still referenced by a XAML file, so a typo cannot quietly
delete rows that are still in use.

It is also the way to REFRESH a string whose English text has changed. locadd.py adds a row only
when one is absent, so editing Text= in a XAML leaves the old wording in the CSVs - and a localized
build takes the CSV's value, so the screen would still show the superseded text. Drop the row with
--force and re-run locadd.py to have it re-added as the XAML now reads.

Usage:
    python tools/drop-locale-rows.py <uid-prefix> [<uid-prefix> ...]
    python tools/drop-locale-rows.py --dry-run btn_soundTest
    python tools/drop-locale-rows.py --force lbl_soundIntro   # then: python tools/locadd.py
"""

import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCALE_DIR = os.path.join(REPO, 'Locale')


def xaml_files():
    for root, dirs, files in os.walk(REPO):
        dirs[:] = [d for d in dirs if d not in ('bin', 'obj', '.git', 'node_modules', '.vs')]
        for f in files:
            if f.endswith('.xaml'):
                yield os.path.join(root, f)


def still_referenced(prefix):
    """True if any XAML still declares an x:Uid starting with this prefix."""
    needle = ('x:Uid="' + prefix).encode('utf-8')
    for path in xaml_files():
        try:
            with open(path, 'rb') as fh:
                if needle in fh.read():
                    return path
        except OSError:
            continue
    return None


def csv_files():
    for root, dirs, files in os.walk(LOCALE_DIR):
        for f in files:
            if f.endswith('.csv'):
                yield os.path.join(root, f)


def main():
    args = [a for a in sys.argv[1:]]
    dry_run = '--dry-run' in args
    prefixes = [a for a in args if not a.startswith('--')]

    if not prefixes:
        print(__doc__)
        return 2

    force = '--force' in args

    for p in prefixes:
        hit = still_referenced(p)
        if hit and not force:
            print('REFUSED: "%s" is still used by %s' % (p, os.path.relpath(hit, REPO)))
            print('         Rows for a live control must not be dropped.')
            print('         To REFRESH a changed string, drop with --force then re-run locadd.py,')
            print('         which re-adds the row from the XAML as it now reads.')
            return 1
        if hit and force:
            print('--force: "%s" is live (%s) - dropping anyway; re-run locadd.py to re-add it.'
                  % (p, os.path.relpath(hit, REPO)))

    total = 0
    for path in sorted(csv_files()):
        with open(path, 'rb') as fh:
            data = fh.read()

        lines = data.splitlines(True)        # keepends - the line endings are not ours to normalise
        kept, dropped = [], 0
        for line in lines:
            # The Uid is the SECOND field, not the first: a row reads
            #   CNC.Controls.WPF.g.en-US.resources:uisoundconfigcontrol.baml,btn_x:System...,Button,...
            # so matching at line start silently matched nothing, which looked exactly like
            # "there was nothing to drop".
            comma = line.find(b',')
            field = line[comma + 1:] if comma >= 0 else line
            if any(field.startswith(p.encode('utf-8')) for p in prefixes):
                dropped += 1
                continue
            kept.append(line)

        if dropped == 0:
            continue

        total += dropped
        print('%-58s -%d' % (os.path.relpath(path, REPO).replace('\\', '/'), dropped))
        if not dry_run:
            with open(path, 'wb') as fh:
                fh.write(b''.join(kept))

    print('%s %d row(s).' % ('Would drop' if dry_run else 'Dropped', total))
    return 0


if __name__ == '__main__':
    sys.exit(main())
