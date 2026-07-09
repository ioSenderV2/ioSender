# Localization (LocBaml) catch-up pass

**When:** a feature's UI text has settled and you want it localized — done as ONE pass folded into the
end-of-session commit, NOT per-change (wording churns).
**Script:** `tools/locadd.py` (self-deriving, committed).
**Memory context:** `iosender-localization-sweep.md`.

Scope rule: **only OUR added/changed lines** get localized, not Terje's base. New strings → the
en-US CSV; the other 6 locales (de-DE hu-HU pt-BR ru-RU uk-UA zh-CN) seed with the English value as a
translate-me placeholder.

## Ready command

```powershell
# apply (adds missing rows to all 7 locales, idempotent):
$env:PYTHONIOENCODING = "utf-8"   # dry-run print chokes on cp1252 for glyphs like ▲
python tools/locadd.py
```

Re-run should report 0 new rows. Scoping helper: `scratchpad/locscope.py` reports per-file missing counts.

## How it works

- `tools/locadd.py` **parses each listed XAML**, derives every control's LocBaml row itself (property
  path + category), appends English-baseline rows to all 7 locales, idempotent (also backfills rows
  that only reached en-US). It also emits `LibStrings.xaml` `<system:String>` rows.
- Keep the **TARGETS** list in `locadd.py` current with the file set (this is the thing that goes stale
  — e.g. `LoadStockView.xaml` → `StartJobView.xaml`). Adding a file is a one-liner: `('path.xaml', 'assembly')`.
- If a control's rows **don't emit** (dry-run shows the file with `+0`, or a specific control missing),
  the gap is `prop_for()` not the CSV — teach it the (tag, attr) → (property-path, category) mapping and
  re-run. Derive the mapping by grepping an existing row for that control type. E.g. `MenuItem` Header →
  `System.Windows.Controls.HeaderedItemsControl.Header` / `Menu` was added this way (2026-07-09); fixing
  the tool retroactively backfilled every previously-missed menu header across all TARGETS. NEVER hand-add
  CSV rows — extend the tool so it stays idempotent and reusable.

## Rules / gotchas

- Add `x:Uid` to new XAML controls inline as you create them (cheap insurance).
- **CSV comma-quoting:** a value containing a comma MUST be RFC4180 double-quoted or LocBaml mis-splits
  it. `locadd.py` handles this; `scratchpad/normcsv.py` is the idempotent fixer.
- After locadd, normalize line endings on touched CSVs: `sed -i 's/\r$//'`.
- **Multi-assembly:** CNC Controls = `CNC.Controls.WPF`; GCodeViewer = `CNC.Controls.Viewer`; both
  MainWindow.xaml files compile to the `ioSender` assembly.
- **.cs literals:** route through `LibStrings.FindResource("Key")` (add a `<system:String>` row to
  `CNC Controls/LibStrings.xaml` + a `libstrings.baml` CSV row). In ioSender XL fully qualify
  `CNC.Controls.LibStrings` (ambiguous with CNC.Core). Terje localizes the message, not the caption.
- **Lathe wizards excluded** — no `CNC.Controls.Lathe` locale CSV exists.
- No LocBaml/satellite build in the repo — CSVs are translator source; satellites regenerated externally.
