#!/usr/bin/env python3
"""gen-sim-macros.py - regenerate the Simulator repo's embedded ATC macro strings.

Reads ioSender's canonical ATC macros - the MACROS list below, which must match AtcMacros.Required,
the same files AtcMacros.cs embeds to provision a real controller - and emits a C header,
src/embedded_macros.h, in the sibling Simulator repo. driver.c writes these into littlefs at boot
(sim_seed_atc_macros), so every simulator this repo builds is ATC-macro-ready out of the box -
matching a correctly-provisioned real machine, with no runtime transfer step needed.

Re-run this whenever macros/*.macro changes and the Simulator repo needs to pick it up (idempotent -
re-running with unchanged sources produces a byte-identical header).

Usage: python tools/gen-sim-macros.py [--simulator-repo PATH]
"""

import argparse
import os

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Order matters: it is the order AtcMacros.Required uses, and the manifest below lists them in it.
#
# tlo.macro was MISSING from this list from 2026-09-14 - when it was extracted out of tc.macro into its
# own file and added to AtcMacros.Required - until 2026-10-26. Every simulator this project builds is
# generated from this list, so none of them ever had tlo.macro on its littlefs, and a named-sub CALL for
# a file that is not there fails with error:81, which says nothing whatsoever about a missing file.
#
# That is why tlo.macro's o130 seek-depth branch could never be exercised on the simulator: not an
# untested branch, an UNTESTABLE one. The sender recovers at runtime (it reports the macro Missing and
# offers to upload it), which is why this never broke hardware - it broke the one route that could have
# verified the macro without hardware.
#
# Adding a macro means adding it in THREE places: here, AtcMacros.Required, and driver.c's
# sim_seed_atc_macros table in the Simulator repo. Miss the third and it is embedded but never written.
MACROS = ["tc.macro", "pcorner.macro", "pvisecorner.macro", "pcenter.macro", "tlo.macro"]

# Sidecar AtcMacros writes next to the macros. Without it the sim has no checksum to compare, so
# ioSender falls back to comparing SIZES - which is what made a one-byte generator bug visible as
# "ATC macros are out of date" on a simulator whose macros were otherwise fine.
CHECKSUM_FILE = "atc.sum"


def c_string_literal(data):
    """Emit `data` (BYTES) as adjacent C string literals, one per line, reproducing it EXACTLY.

    This has to be byte-for-byte. ioSender compares the controller's file SIZE against the byte
    length of its own embedded copy, so a single byte of drift reports a perfectly good macro as
    Outdated. The previous version got this wrong twice, in opposite directions:

      * it read the file in TEXT mode, so Python's universal newlines collapsed every CRLF to LF -
        losing one byte per CRLF (tc.macro has four, hence -4); and
      * it split on '\\n' and appended '\\n' to every element, including the empty one left at the
        end of a file that ends with a newline - adding one byte back.

    Files with no CRLF (pcorner, pvisecorner) showed only the +1. Reading bytes and escaping them
    individually removes the whole class of error rather than patching either symptom.
    """
    out = []
    line = '    "'
    for b in bytearray(data):
        if b == 0x5C:            # backslash
            line += '\\\\'
        elif b == 0x22:          # double quote
            line += '\\"'
        elif b == 0x0D:          # CR - kept, never normalised away
            line += '\\r'
        elif b == 0x0A:          # LF - ends this C line, keeping one literal per source line
            out.append(line + '\\n"')
            line = '    "'
        elif 0x20 <= b <= 0x7E:
            line += chr(b)
        else:
            # 3-digit octal: unambiguous even when the next character is itself a digit, unlike \x
            # which greedily consumes every following hex digit.
            line += '\\%03o' % b
    if line != '    "':          # trailing bytes with no final newline
        out.append(line + '"')
    return '\n'.join(out) if out else '    ""'


def atc_manifest(bodies):
    """Reproduce AtcMacros.EmbeddedManifest: one "<sha256>  <name>" row per macro, in Required order,
    every row newline-terminated. Two spaces between hash and name, as the sender writes it.

    This used to emit AtcMacros' OLDER format - a single aggregate hash over the whole set. The sender
    still accepts that (LegacySumMatches), so this is not a correctness fix, it is a cost one: an
    aggregate that disagrees re-uploads all five macros over YModem (~500 128-byte packets, ~23 s
    measured), where a per-file manifest re-uploads only the file that actually changed.

    SHA-256 over the macro's RAW BYTES, which is what the sender's hash is over: it hashes the UTF8
    bytes of the embedded resource, the resource IS the file, and no macro carries a BOM - checked, so
    StreamReader's BOM stripping cannot make the two disagree. Non-ASCII is present (tc 9 bytes, tlo 4)
    and round-trips identically. Read in BINARY regardless: text mode would normalise any CRLF away,
    which is the bug that once cost tc.macro four bytes. The working tree is all-LF today, so that
    hazard is currently dormant rather than gone - a .macro authored on Windows would wake it up.

    One row this cannot reproduce, by construction: under the -notoolsetter flag the sender rewrites
    tc.macro's #<_tc_touchplate> value before hashing it, so that run's tc.macro row legitimately
    differs from this one and tc.macro gets re-uploaded once. Per-file is exactly what keeps that
    disagreement down to the one file instead of the whole set.
    """
    import hashlib
    out = ''
    for name, body in bodies:
        out += hashlib.sha256(body).hexdigest() + '  ' + name + '\n'
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--simulator-repo', default=os.path.join(os.path.dirname(REPO), 'Simulator'))
    args = ap.parse_args()

    dest = os.path.join(args.simulator_repo, 'src', 'embedded_macros.h')
    if not os.path.isdir(os.path.dirname(dest)):
        raise SystemExit('Simulator repo src/ not found at ' + os.path.dirname(dest) + ' - pass --simulator-repo')

    parts = ['// embedded_macros.h - GENERATED by ioSender/tools/gen-sim-macros.py - do not hand-edit.',
             '// Source: ioSender/macros/*.macro (the same files AtcMacros.cs provisions onto a real',
             '// controller). Re-run the generator after changing them.',
             '',
             '#ifndef EMBEDDED_MACROS_H',
             '#define EMBEDDED_MACROS_H',
             '']

    bodies = []
    for name in MACROS:
        path = os.path.join(REPO, 'macros', name)
        # BINARY - see c_string_literal. Text mode silently rewrites line endings.
        with open(path, 'rb') as f:
            content = f.read()
        bodies.append((name, content))
        ident = 'embedded_macro_' + name.replace('.macro', '').replace('-', '_')
        parts.append('static const char *' + ident + ' =')
        parts.append(c_string_literal(content) + ';')
        parts.append('')

    # The sidecar, so the simulator comes up looking exactly like a machine ioSender provisioned.
    # Built through c_string_literal like every macro above, so the row newlines are real bytes and
    # one C literal appears per manifest row - readable in the header, and byte-exact either way.
    manifest = atc_manifest(bodies)
    parts.append('// The atc.sum sidecar, byte-for-byte as AtcMacros.EmbeddedManifest builds it: one')
    parts.append('// "<sha256>  <name>" row per macro, in AtcMacros.Required order. Seeded so the sender')
    parts.append('// compares checksums and short-circuits, instead of falling back to comparing file sizes.')
    parts.append('static const char *embedded_atc_sum =')
    parts.append(c_string_literal(manifest.encode('ascii')) + ';')
    parts.append('')

    parts.append('#endif // EMBEDDED_MACROS_H')
    parts.append('')

    with open(dest, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(parts))

    print('wrote', dest)
    for name, content in bodies:
        print('  %-20s %6d bytes' % (name, len(content)))
    print('  %-20s %6d bytes, %d row(s)' % (CHECKSUM_FILE, len(manifest), len(bodies)))
    for row in manifest.rstrip('\n').split('\n'):
        print('      ' + row)


if __name__ == '__main__':
    main()
