# Manual audit — 2026-07-24

Read-only audit of `docs/manual/index.html` against everything shipped since the last manual pass
(commit d65c94a, 2026-07-17, documented in `docs/manual/SCREENSHOT-REFRESH-2026-07.md`). That pass
paid off `SCREENSHOT-AUDIT.md` (2026-07-16) through changelog entry **#133**. This audit covers
**#134 through #178** (current HEAD, `Overview.html`) and **supersedes** `SCREENSHOT-AUDIT.md` —
its findings are folded in below rather than duplicated.

Method: read every changelog entry #133–#178 in `Overview.html`; grepped `docs/manual/index.html`
for every term those entries touch (Cycle Start, Generate, Dry Run/Check Run/Simulate, Offsets
Set-all/Clear-all, Fixture definitions, Stepper Calibration Probe, Feeds & Speeds, Check for
Updates); opened the 13 referenced PNGs under `docs/manual/img/` and visually compared against
current XAML (`MainWindow.xaml`, `MachineSetupWizard.xaml`, `OffsetView.xaml`, `ToolsView.xaml.cs`,
`JobControl.xaml`) and code-behind (`ToolsView.xaml.cs`, `ComponentRegistry`/`TabRegistry`
registrations).

**Headline: this is a bigger invalidating event than the original main-menu overhaul.** Every
full-window screenshot reshot on 2026-07-17 is stale again — the Apple-HIG redesign (#166),
the Cycle-Start→Run rename (#161), and the Offsets rework (#171) all landed *after* that refresh.
On top of that, an entire new top-level tab (**Feeds & Speeds**, #178) has zero manual coverage.

**Correction (2026-07-25, live-tested by the user):** the `StretchTabControl` "overflow" item below
was mis-diagnosed from the static screenshot alone. On the actual current build, a too-narrow window
does **not** silently drop the newest tab — the standard WPF `TabPanel` wraps overflow tabs onto a
**second row** instead. So the tab strip stays fully reachable; the only real defect is cosmetic
(two-row tab strip looks less polished at narrow widths), not a hidden/unreachable tab. Downgraded
from "escalate past noticed-but-not-fixed" to a minor polish item — see §5 C2.

**Also done 2026-07-25:** the top-level tab order was changed to match the user's preferred
arrangement — Settings, Feeds & Speeds, Start Job, Job, Offsets, SD Card, Probing, Tools, Machine
Setup, Height Map, Lathe Tools (last) — via `CNC Controls/CNC Controls/LayoutModel.cs`
`DefaultLayout.Build()` (the actual source of the default tab sequence; `TabDescriptor.Order` in
`TabRegistry.cs` turned out to be dead/unread code, not the real mechanism — `MainWindow.xaml.cs`'s
`TabRegistry.Register(...)` order args were also updated for consistency but have no runtime effect).
This only changes the default for **fresh installs**; existing saved profiles (persisted `Base.Tabs`)
are unaffected per [[iosender-multiuser-transition]] and were not force-migrated.

---

## 1. Executive summary

| Bucket | Count |
|---|---|
| **MISSING** — shipped functionality with no manual coverage at all | 6 |
| **OBSOLETE** — manual text describing UI/workflows that no longer exist | 5 |
| **NEEDS UPDATING** — still-relevant topics/screenshots now showing stale chrome or content | 11 (of 13 screenshots) + 3 text sections |
| Carry-over items re-confirmed | 4 of 4 (2 unchanged, 1 newly evidenced, 1 unchanged) |

---

## 2. MISSING — no manual coverage at all

| # | Finding | Evidence | Suggested fix |
|---|---|---|---|
| M1 | **Feeds & Speeds tab (Fusion Addin integration) — entirely undocumented.** New top-level tab, registered `TabRegistry.Register(... ViewType.FeedsAndSpeeds, "Feeds & Speeds", () => new FeedsAndSpeedsView(), 95 ...)` in `MainWindow.xaml.cs:2418`. No `#feeds-and-speeds` anchor, no screenshot, no mention anywhere in `index.html` (only an unrelated generic phrase "feeds-and-speeds calculator" at line 675 in the Clean Results topic). **Urgent**: the user is about to publish a promotional video walking through exactly this feature. | changelog #178 (820-line `feedsAndSpeeds.py`, `FeedsSpeedsAdvisor.cs`, `FeedsAndSpeedsView.xaml/.cs`) | New top-level topic: what the tab does (Intro/Load-Import/Results sub-tabs), the material chip-load table cross-checked against live `$30`/`$110-112`, the optional AI second-opinion pass (model picker, `ANTHROPIC_API_KEY` gate), "Write apply file". Also document the companion **Help → Support → Install ioSenderV2 Fusion Addin...** menu item (`MainWindow.xaml:62`, undocumented) and the Fusion-side Batch Post Process material-split option. New screenshot required. |
| M2 | **Run-bar mode dropdown (Dry Run / Check Run / Simulate) — undocumented.** `#job`'s "The run bar" table (lines 810-818) only lists Cycle Start/Feed Hold/Stop/Reset/Optional stop — the old, single-mode button. | changelog #161 (Run + mode dropdown), #176 (adds Simulate mode) | Rewrite the run-bar table: Run button + adjacent mode-dropdown (Run/Dry Run/Check Run/Simulate), what each mode does, that Check Run no longer arms `$C` until pressed, that the mode always reverts to Run at job end, and that Simulate temporarily reconnects to the bundled simulator and restores the original connection afterward. |
| M3 | **Stepper Calibration (Probe) tab — undocumented**, including its Z-axis 1-2-3 gauge-block mode. `#tools`'s reference table (line 992) only has a combined "Stepper calibration / scratch" row; the actual sub-tab registry (`ToolsView.xaml.cs:29-36`) has *four* distinct stepper-cal entries: Manual, Scratch, **Probe** (missing from manual), Surface Spoilboard is separate. | changelog #155 (new tab), #159 (G30 park, Safe Z delta, auto-hide), #164 (Z via gauge block), #173/#174 (polish) | Add a "Stepper calibration (probe)" row to the Tools table: XY via corner-fence probe or Z via 1-2-3 gauge block, parks at G30, "reuse last saved starting position" checkbox, least-squares Z fit. |
| M4 | **Fixture definitions workflow (Machine Setup step 6 + the Fixture edit dialog's Corner Fence schematic) — only named in passing.** The `#machine-setup` step table has one row (line 916) but no screenshot/detail of the fixture-edit dialog itself, its redesigned Corner Fence schematic, or directly-editable X/Y/Z position. | changelog #143 (fixture edit dialog redesign, predates #133 but never got a manual pass either — confirmed absent) | Either fold a sub-section into `#machine-setup` or add a short "Fixtures" callout under `#start-job` (which already references "Fixture" via the dropdown in `start-job-panel.png`). |
| M5 | **Check for Updates / Roll back to previous version — undocumented.** Zero hits for "update" anywhere in `index.html` outside unrelated JS function names. Both `Help → Support → Check for updates...` and `...Roll back to previous version...` exist in `MainWindow.xaml:59-60` with no manual mention. | changelog #156, #157, #165, #169 (silent title-bar update check too) | Add a short paragraph to `#settings` or a new "Keeping ioSender up to date" callout: Check for Updates flow (including the dev-build release-picker path from #165), Roll back, and the silent "(update available)" title-bar hint from #169. |
| M6 | **Dry Run mode's specific behavior (skips tool changes, sender-side spindle/coolant-off) — undocumented**, distinct from M2's dropdown-existence gap. | changelog #134, #138, #141 (three separate Dry Run bug-fix rounds — it's a mature, real feature) | Cover in the same run-bar section as M2: Dry Run runs at Z-offset with spindle/coolant forced off and skips M6 tool changes; useful as an air-cut rehearsal. |

---

## 3. OBSOLETE — describes UI/workflows that no longer exist

| # | Location | Obsolete text | Why | Fix |
|---|---|---|---|---|
| O1 | `#job` run-bar table, line 813-814 | **"Cycle Start"** (×2) | Renamed to **Run** app-wide in #161 (13 files touched, including `JobControl.xaml`, tooltips, all 7 locale CSVs) | s/Cycle Start/Run/ — and fold in the mode dropdown per M2 above rather than a plain rename. |
| O2 | `#clean-results` opener, line 643 | *"You can load a file and press Cycle Start"* | Same rename | *"...and press Run"* |
| O3 | `#offsets` "The Offsets tab" section, lines 1016-1023 | **"Get current position"**, **"Set all"**, **"Clear all"** as the tab's controls | The whole tab was rebuilt in #171 into an inline-editable `DataGrid`: per-row `Get` (MPos) and `Clr` buttons, orange/blue change-tracking (same convention as the Grbl settings tree), row commits on focus-leave, and G28/G30/G92 rows confirm before writing. The global "Set all"/"Clear all" buttons **do not exist any more** — confirmed by reading `OffsetView.xaml` (only a per-row `Header="Get MPos"` `DataGridTemplateColumn` with a `Get` button remains, no `Set all`/`Clear all` anywhere in the file). | Rewrite the whole "The Offsets tab" subsection: inline grid, per-row Clr/Get MPos columns, change-tracking colors, G28.1/G30.1 capture-current-position semantics, G92's "declare current position as zero" semantics (not a target-move), sign-constrained X/Y/Z validation, and the new draggable splitter to the usage-notes panel (#177). |
| O4 | `#start-job` step 5, line 873 | *"...then **Generate**."* implying a Start-Job-local Generate button | #162 eliminated all 5 tool tabs' own Generate buttons; Generate is now the *shared* Run bar at the bottom of the window, which reads "Generate" only while that tab is focused and nothing's built yet, then flips to "Run". | Reword: *"...then press **Generate** on the run bar at the bottom of the window."* — small but avoids implying a panel-local button that no longer exists. |
| O5 | `#tools` callout, line 996-998 ("Big programs run for real") | Implicitly still describes a per-tab Generate → separate Run flow | Same #162 fold-in as O4 — worth a one-line update noting Generate/Run now share one button+dropdown per tab. | Minor wording pass alongside M3. |

---

## 4. NEEDS UPDATING — still-relevant, now stale (chrome and/or content)

### 4a. Screenshots

All 13 screenshots were re-inspected. The 2026-07-17 refresh fixed the *previous* generation of
staleness (old `File Camera Help` menu, old toolbar row); **all of that is now compounded by a
second wave** — the Apple-HIG redesign (#166: pill-style tab strip, oval checkboxes, color-dot
GroupBox headers, recessed `NumericField`s) touched, by its own changelog description, "Job,
Settings, Probing, Height Map, SD Card, Offsets, Machine Setup and Tools tabs" — i.e. every
full-window screenshot in the manual.

Visually confirmed (opened the PNG) for the three highest-traffic topics:

| File | Confirmed stale because | Priority |
|---|---|---|
| `img/job-runscreen.png` | Flat gray tab strip (pre-pill), **"Cycle Start"** button still visible (pre-#161 rename), old bottom jog-pad/run-bar duplicate layout, no Run-mode dropdown | Critical |
| `img/start-job-panel.png` | Same flat tab strip + "Cycle Start"; a plain "Generate" button shown *inside the Start Job panel itself* — no longer matches reality now that Generate lives in the shared run bar | Critical |
| `img/machine-setup-overview.png` | Same flat tab strip + "Cycle Start"; **and** the captured tab strip only shows through "7 · Controller macros" — **the 8th step tab ("Build simulator") is not visible at all**, live confirmation of the `StretchTabControl` overflow bug (see §5) | Critical — doubles as bug evidence |

Not re-opened individually this pass but **inferred stale by the same mechanism** (flat tab strip,
old button styling, old checkboxes) since #166 explicitly names these tabs as redesigned:

| File | Topic | Reason |
|---|---|---|
| `img/offsets-table.png` | `#offsets` | Content is now doubly wrong — old grid layout **and** the whole tab structure changed (#171, see O3) |
| `img/settings-grbl.png` | `#settings` | Apple-HIG pill tabs; also missing any visual trace of the Fusion Addin install menu item |
| `img/probing-tabs.png` | `#probing` | Apple-HIG chrome |
| `img/tools-tab.png` | `#tools` | Apple-HIG chrome; also doesn't show the new Stepper Calibration (Probe) sub-tab (M3) |
| `img/sdcard.png` | `#sdcard` | Apple-HIG chrome; also #168's "resolving" vs "No card" distinction not shown |
| `img/gcode-viewer.png` | `#gcode-viewer` | Apple-HIG chrome |
| `img/heightmap.png` | `#heightmap` | Apple-HIG chrome |

Two dialogs previously marked "CURRENT — no action" in `SCREENSHOT-AUDIT.md` were re-opened and
downgraded to **low-priority stale**, not urgent:

| File | Verdict |
|---|---|
| `img/connect-dialog.png` | Still structurally accurate (Serial/Network/Simulator tabs, same fields) but its OK/Cancel buttons predate the AppleStyles.xaml global merge (`App.xaml:9` — `AppleStyles.xaml` is merged app-wide, so **every** dialog, not just tabs, picked up the new button/checkbox look). Cosmetic only — low priority. |
| `img/errors-dialog.png` | Same: content (error/alarm code tables) still matches `ErrorsAndAlarms.xaml` exactly; only button chrome is dated. Low priority. |

### 4b. Text sections (beyond the OBSOLETE items in §3)

| Section | Issue |
|---|---|
| `#settings` tab table (lines 1049-1058) | Still accurate on tab *names* (8 tabs, Simulator included) but doesn't mention that Settings > Simulator's status text is now live-refreshing on every option change (#169), or that a silent update check now appends "(update available)" to the window title (#169) — folds into M5. |
| `#tools` reference table (lines 987-995) | Row-for-row stale: "Stepper calibration / scratch" collapses what is actually 3 separate sub-tabs (Manual, Scratch, Probe) in the real `ComponentRegistry` (`ToolsView.xaml.cs:30-32`) — see M3. |
| `#machine-setup` step table | The *manual's* table (lines 909-918) is correct (already fixed 2026-07-17) — only the **in-app** wizard text is stale, see §5 carry-over C3. Listed here only to avoid double-fixing: no manual-side action needed. |

---

## 5. Carry-over items from `SCREENSHOT-AUDIT.md` (2026-07-16) — re-confirmed

| # | Item | Status |
|---|---|---|
| C1 | `img/lathe-wizard.png` not reshot — blocked because `LatheEnabled` syncs purely from controller-reported capabilities and the simulator doesn't report lathe support | **Still blocked, unchanged.** No lathe-related changelog entry #134-178 touches this. Once unblocked it will *also* need the Apple-HIG chrome pass (§4a), so batch it with that work rather than reshooting twice. |
| C2 | `StretchTabControl` (`CNC Controls/CNC Controls/StretchTabControl.cs`) has no overflow/scroll affordance — newest tab(s) silently don't render when the strip is too narrow | **Still open, and now visually re-confirmed in this audit**: `machine-setup-overview.png` itself only shows 7 of the wizard's 8 step tabs in the captured strip (§4a). Source-checked: #172 touched `StretchTabControl.cs` (+51/-6) but only for drag-debris/clip fixes during header reorder — no scroll/overflow/menu affordance was added anywhere in the file (grepped for `Scroll`/`Overflow`/`clip` — only the pre-existing reorder-drag `Clip` usage exists). **This is now higher-stakes**: the main window gained a 10th top-level tab (`Feeds & Speeds`, order 95 — rightmost) since this bug was first filed, so the newest, most promotable feature is the one most exposed to silently not rendering on a narrower window. Worth escalating past "noticed but not fixed." |
| C3 | In-app Machine Setup wizard's own Overview-tab text still says "six steps" and omits Fixtures/Build simulator | **Still open, confirmed by direct XAML read** — `MachineSetupWizard.xaml` lines 67-73: the `ov_s6` TextBlock reads *"6 · Controller macros - install the controller-side macro set."*, with no `ov_s7`/`ov_s8` for Fixture definitions or Build simulator at all. This is an **app bug**, not a manual bug (the manual's own table is correct) — flagging again per the task's request to re-confirm, not a manual-audit action item. |
| C4 | Final full click-through verification of `index.html` in a browser | **Still not done.** Out of scope for this static/read-only pass; flag as a follow-up once the reshoot+text batch below lands (verifying a half-updated page mid-batch wastes the pass). |

---

## 2026-07-25 update — text pass DONE

All text-only findings from §2 (MISSING) and §3 (OBSOLETE) have been applied to
`docs/manual/index.html`, plus M3 and M5:
- **M1** — new `#feeds-and-speeds` section written (Machinist track, `machinist:8`): setup, the
  Load/Import → Results → Ask AI → Write apply file workflow, and Batch Post Process. Ships with a
  `shot-todo` placeholder (no screenshot yet — see below).
- **M2 + M6** — `#job`'s run-bar table rewritten for the Run/Dry Run/Check Run/Simulate dropdown,
  including Check Run's "not armed until pressed" behavior and the always-reverts-to-Run-at-job-end note.
- **M3** — Tools table now lists all three stepper-cal sub-tabs separately, including Probe's
  gauge-block/Corner-Fence/G30-park/Safe-Z-delta behavior.
- **M5** — new "Keeping ioSender up to date" paragraph under `#settings` (Check for updates, dev-build
  release picker, silent title-bar hint, Roll back).
- **O1–O5** — all fixed: both "Cycle Start" leftovers, the Offsets tab rewritten for the real
  inline-editable grid (Get/Clr per row, orange/blue change-tracking, G28/G30/G92 confirm-first), and
  the two Generate-button wording nits in `#start-job`/`#tools`.

**Still open:**
- **M4** (fixture-definitions/Corner-Fence dialog detail) — not done, stayed low-priority per the
  original order of attack.
- **All screenshot reshoots** (§4a) — none taken yet; text is now ahead of the images. Also needs a NEW
  screenshot for `#feeds-and-speeds` itself. Blocked on a `-testserver` capture session — see
  [[iosender-testserver-real-hardware-safety]], needs explicit per-turn ask.
- **C1 lathe-wizard.png** — still blocked (unrelated capability-reporting gap).
- **C4 final click-through** — do after the reshoot batch.
- Tab order changed 2026-07-25 (Settings, Feeds & Speeds, Start Job, Job, Offsets, SD Card, Probing,
  Tools, Machine Setup, Height Map, Lathe Tools) — screenshots should capture the NEW order.

## 6. Suggested order of attack

Given the upcoming promotional video, prioritize by "what would embarrass us on camera" first:

1. **Fix the `StretchTabControl` overflow bug (C2) before anything else.** If `Feeds & Speeds` — the
   star of the video — silently fails to render as a tab on a normal window width, no amount of
   manual work matters. This is a code fix, not a doc fix, but it blocks confidently screenshotting
   almost every other item below (any capture window narrower than "all tabs fit" will misrepresent
   the app).
2. **M1 — write the Feeds & Speeds manual topic** (new anchor, screenshot, Fusion-addin install
   mention). Highest-value net-new content, directly matches the video's subject.
3. **M2 + M6 — rewrite the run-bar section** (`#job`) for Run/Dry Run/Check Run/Simulate. Second
   most likely thing a viewer clicks straight after watching the video (probably including a live
   run demo).
4. **`job-runscreen.png` + `start-job-panel.png` + `machine-setup-overview.png` reshoots** — same
   three flagged Critical in the original audit, now doubly stale (chrome *and*, for
   `start-job-panel.png`, the Generate-button content per O4).
5. **O3 — rewrite `#offsets`** (text + `offsets-table.png` reshoot) — the single biggest
   content-drift item, on par with the original audit's machine-setup step-count fix.
6. **O1/O2 — the two remaining "Cycle Start" text fixes** — trivial, no screenshot dependency,
   bundle with #4.
7. **M3 — Stepper Calibration (Probe) row + short description** in `#tools`, plus reshoot
   `tools-tab.png`.
8. **M5 — Check for Updates / Roll back / silent update-check callout** — no screenshot required,
   quick win.
9. **Remaining chrome-only reshoots**: `settings-grbl.png`, `probing-tabs.png`, `sdcard.png`,
   `gcode-viewer.png`, `heightmap.png` — batch together, same Apple-HIG cause.
10. **M4 — Fixture-definitions/Corner-Fence schematic detail** — lower urgency, already lightly
    covered via the step-6 table row and `start-job-panel.png`'s Fixture dropdown.
11. Low-priority cosmetic: `connect-dialog.png` / `errors-dialog.png` button-style reshoot — batch
    with #9 if convenient, skip otherwise (functionally accurate today).
12. **C1 — lathe-wizard.png**: keep blocked until the controller/simulator capability-reporting gap
    is separately resolved; when it is, shoot once with current (post-Apple-HIG) chrome rather than
    twice.
13. **C4 — final click-through** — do this LAST, after the batch above lands, not before.
