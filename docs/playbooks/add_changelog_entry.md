# Add a changelog entry

**When:** you've shipped a feature/fix and want it in the changelog.
**Where:** the changelog lives **inside `Overview.html`** as `<section id="features-and-fixes">`
(the standalone `FeaturesAndFixes.html` is retired). Edit on the `integration` branch.
**Memory context:** `iosender-proposed-prs-howto.md`.

Entries are a flat numbered list `1..N`, badge `#N`, anchor id `prN`. No branch column, no stars,
no dependency counts.

## Ready command — `tools\add-changelog-entry.ps1` does all four places + totals

Write a small JSON spec (title, tag NEW/CHG/FIX, at-a-glance group + one-liner, curated file rows,
description), then:

```powershell
tools\add-changelog-entry.ps1 -Spec <spec.json> -Pdf
```

The script derives `#N`, computes **every count from the file rows** (per-entry Files chg/del/add +
Lines, and re-sums the grand totals), inserts the detail block / TOC row / at-a-glance row, bumps the
header count, self-checks that all places agree, prints the diff, and (`-Pdf`) regenerates the PDF.
**You only author content.** Spec shape + per-file `status`/`count`/`label` semantics are in the
script header. The manual anatomy below is the reference for what it builds and for the numstat
**curation** (grouping siblings / omitting tooling files) you still do when filling in `files`.

## The catch: FOUR places must change for one entry (miss one and it's wrong)

1. **Detail section** — the full entry (numstat file table + description). Append **before
   `</section>`** (near end of file, *not* `</body>`), wrapped in `<!-- ===== #N ===== -->`.
2. **"Features at a glance"** grouped table — a one-line `<tr>` in the right `<h3>` group.
3. **TOC index** (`<div class="toc"><table>`) — a per-entry row **before the `<tr class="tot">`
   totals row**, then **bump the totals row** (chg/del/add files + add/del lines + "Totals (N changes)").
4. **Header count** (`<div class="sub">`) — bump "**N improvements in the `integration` build**".

## Locate the anchors (Overview.html is ~1900 lines — jump straight to them)

```bash
# The last entry number + all 4 landmark lines at once:
grep -nE 'id="pr[0-9]+"|improvements in the|class="tot"|<td class="n">[0-9]' Overview.html | tail -6
# The <h3> groups of the "Features at a glance" table (pick the right one for step 2):
grep -n '<h3>' Overview.html
```
- **Detail section (1):** the last `id="prN"` line is your template block; the new entry goes just
  before the final `</section>` (right after that block's closing `</div>`).
- **At-a-glance (2):** insert before the `</table>` that closes your chosen `<h3>` group.
- **TOC + totals (3):** insert before the `<tr class="tot">` line; edit that same line for the totals.
- **Header (4):** the `improvements in the` line.

## Use the last entry as a template

Copy the highest existing entry's detail block + TOC row + at-a-glance row, then substitute. This keeps
the exact markup current.

## Numstat convention (TOC row mirrors the DETAIL table, NOT raw git)

The detail table is a **curated** file list — group siblings (e.g. `Locale/*/csv (7 locales)`), omit
self-referential/tooling files (`Overview.html`, `tools/locadd.py`, sometimes `.csproj`).
The **TOC row counts = sum of the detail table**, not `git diff --numstat`.
Starting point: `git show --numstat --format=oneline <commit>` → then curate/group/omit → sum THAT.
- **Files** `chg del add`: chg = # modified, add = `+K` brand-new, del = # deleted (a grouped row of
  K files counts as K).

## Ready fragment — detail entry skeleton

```html
<!-- ===== #N ===== -->
<div class="pr">
<h2 id="prN"><span class="badge">#N</span>Title</h2>
<table class="files">
<tr><th>File</th><th style="text-align:right">+</th><th style="text-align:right">&minus;</th></tr>
<tr><td class="mono">path/File.cs</td><td class="num add">823</td><td class="num">0</td></tr>   <!-- zero-del: class="num" (no "del") -->
<tr><td class="mono">path/Other.cs</td><td class="num add">3</td><td class="num del">21</td></tr>
</table>
<div class="desc"><p>summary…</p><ul><li><strong>…</strong> …</li></ul><p class="foot">build status / caveats</p></div>
</div>
</section>
```

## Ready fragment — TOC row

```html
<tr><td class="n">N</td><td><a href="#prN">Title</a></td><td class="sz"><span class="k">Files:</span> <span class="chg">C</span> <span class="del">D</span> <span class="add">+A</span><br><span class="k">Lines:</span> <span class="add">+A</span> <span class="del">-D</span></td></tr>
```

Cross-ref another entry as `<a href="#prN">#N</a>` (visible `#N`, href/anchor stays `prN`).

## Then: regenerate the PDF and commit

- Regenerate `Overview.pdf` — see [regenerate_overview_pdf.md](regenerate_overview_pdf.md).
- Sanity check: `grep -c '<div class="pr">'`, `grep -c 'id="pr'`, and the `<td class="n">` TOC-row count
  should all equal N; header count + "Totals (N changes)" match.
- `git add Overview.html Overview.pdf` (named files, **not** `-A` — untracked sim binaries/macros live in the tree).
