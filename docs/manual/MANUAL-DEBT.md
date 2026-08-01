# Manual update debt

Living list of online-manual updates owed after shipped UI/UX changes. The manual is
`docs/manual/index.html` (LIVE at https://iosenderv2.github.io/ioSender/), republished with
`docs/manual/publish-pages.ps1`. Pay this off in a focused manual session — reshoot the flagged
screenshots (see `docs/playbooks/reimport_manual_screenshot.md`) and fix the flagged topic text,
then `publish-pages.ps1`. Check items off as done; delete the section once a batch is fully paid.

---

## Debt from the main-menu overhaul (changelog #84, shipped 2026-07-09)

The menu bar, toolbar row, program view, console, and camera access all changed. Impact:

### Screenshots to reshoot (biggest item)
- [ ] **Any screenshot showing the old menu bar** (`File  Camera  Help` with a full File menu) — the bar
      is now **`Connect…  Camera  Help`** (Camera hidden unless a camera is bound). Sweep every topic's
      screenshots; the top menu bar shows in many.
- [ ] **Any screenshot showing the toolbar-icon row** beneath the menu bar (Open/Reload/Edit/Close icons +
      macro buttons) — that whole row is **gone**. Reshoot so the row is absent.
- [ ] **Program view** screenshots — the title bar is now a **Load File / Load Folder** affordance when empty
      and **name + ✕ close** when loaded (topics: `job`, `start-job`, `getting-started`).

### Topic text to fix
- [ ] `connect` — "Connect" is now a **top-level menu item** (was File → Connect…); it reads
      **Reconnect…** once connected.
- [ ] `job` / `getting-started` — **loading a file/folder** is now via the **program-view header buttons**
      (or drag-drop), not File → Load / Load Folder. **Save** and **Transform** are now on the **program
      list's right-click menu** (search the manual for "Transform" — a couple of spots, ~lines 833-834).
- [ ] **Console** — the "Open Console" menu item is gone; the pop-out console now opens by
      **double-clicking the Console tab** (Esc still toggles). Update any "Open Console" mention.
- [ ] **Camera** — now **opt-in**: bind a device in **Settings → App → Camera** (Device dropdown +
      Connect/Disconnect) to make the Camera menu appear. Update/add camera guidance.
- [ ] **Help** — new **Help → Support** submenu (currently holds "Open Application data folder").
- [ ] Search for stale words: **"File menu"**, **"Open Console"**, **"toolbar"** (~lines 1087-1088),
      **"Reload"/"Edit" file icons** — all removed/moved.
- [ ] **F1 / context-help** mappings — verify none point at removed menu items.

### Not yet built (will add MORE debt when done)
- Help → Support **Check for updates** (deferred feature).
- **Macro-name flyout** replacing the removed macro toolbar (deferred idea).

---

## Debt from the job-flow redesign (#190–#195, shipped 2026-07-26 to 2026-07-31)

Big one: "Start Job" was renamed to **Setup** and unified into one shared tab (no longer duplicated/G59-pinned
for Odd Jobs); Odd Jobs was retired and its Work Order composer promoted to a **top-level Work Order tab**;
Work Order's Run now hands its generated program to the real Job-tab program list (with a Source: File/
Generated badge + Edit-jump-back button) instead of a floating preview; Setup gained a Dynamic
fixture/Geometry panel (folds in the Probing-tab pickers + Height Map), Material-driven conductive-probing
rules, and touch-plate TLO support.

### Text — DONE 2026-07-31
- [x] `#start-job` topic renamed to `#setup`, all internal links repointed, body rewritten for the unified
      Setup tab (Fixture/Probe/Geometry/Stock incl. Material/Actions incl. G54–G59/G92 + height map fold-in,
      conductive-stock callout, "no completion gate" note).
- [x] New `#work-order` topic added (toolpaths/operations tree, Generate/tool-ordering, Run → Job-tab handoff,
      Source/Edit badge, "one Setup not one per program" callout, T-number reservation callout).
- [x] `#job` topic's "Loading a program" list gained Work Order as a third source; mentions the
      Source (File/Generated) badge and Edit button.
- [x] Getting-started tab table + "first five minutes" list updated to include Work Order.

### Screenshots to reshoot/add
- [x] `start-job-panel.png` → reshot 2026-08-01, current Setup tab (normal fixture, Material/Stock/Actions visible).
- [x] **New: a Work Order screenshot** → `work-order.png`, added 2026-08-01, shows the toolpath/operations
      tree (Contour/Pocket/Oval 1/Counterbore) plus the tool-order popover and compiled g-code. Wired into
      the `#work-order` topic in place of the shot-todo placeholder.
- [x] `job-runscreen.png` → reshot TWICE 2026-08-01. First pass caught a real product bug while sourcing the
      shot: Work Order's run showed status in a separate floating panel (`_macroRunView`, not the docked Job
      tab list) with a dead status column - fixed same session (`MacroProcessor.Run` gained an opt-in
      `preferJobView` flag; `RunStreamedJobInPlace` now builds the Work Order burst directly into
      `GCode.File` instead of a disconnected transient copy, so `ProgramPanel`'s own docked
      `GCodeListControl` - permanently bound to `GCode.File.Data` - gets the live `ok`/`*` writes for free).
      Final reshot confirms it working: docked list shows live status, no floating panel, no Edit button
      (there never really was a "Source: File/Generated badge" as first described - corrected in the manual
      text along with the now-removed floating-panel/Edit-button claims).

### Screenshots batch — FULLY DONE 2026-08-01
All three planned shots (`start-job-panel.png`, `work-order.png`, `job-runscreen.png`) plus the optional
bonus (`setup-dynamic-geometry.png` - Dynamic fixture + Geometry panel, doubles as a live example of the
conductive-stock warning) are in and wired into `index.html`. Nothing left on this batch.

- [x] Bonus find, not originally tracked: `offsets-table.png` was also stale (showed an old run-bar control)
      - reshot 2026-08-01, current tab strip/run bar. Same filename, no `index.html` change needed.
- [x] **Superseded by a bigger decision, same day:** the user decided to remove the `#probing` and
      `#heightmap` topics from the manual ENTIRELY (2026-08-01), not just re-caption them - both tabs are
      still registered in `TabRegistry.cs` (`ViewType.Probing`/`ViewType.HeightMap`, not `alwaysVisible`) and
      kept in the codebase "for the short term", but are no longer part of the user's own layout or the
      recommended workflow (Setup's Dynamic fixture covers ad-hoc probing; Setup's "Probe height map" action
      covers surface compensation). Both `<section>` blocks removed from `index.html`, every cross-reference
      link to `#probing`/`#heightmap` elsewhere in the manual cleaned up (Intro, Getting Started, Clean
      Results, Accuracy & Calibration, Setup, Offsets, Machine Setup - ~14 spots total), tag balance verified
      (`<section>`/`<ul>`/`<figure>` counts all matched after the edit). `probing-tabs.png` and
      `heightmap.png` are now orphaned image files - left in `docs/manual/img/` (git history keeps them
      recoverable) rather than deleted, flagged orphaned in `_image-review.html`. If either tab comes back
      into the recommended workflow later, these topics can be restored from git history (`git log --
      docs/manual/index.html`) rather than rewritten from scratch.

### Not yet built (will add MORE debt when done)
- Hardware verification is still in progress for several Work Order paths (bore clearing, counterbore→
  through-drill on one centreline, tabs-on-last-op, patterned bolt circles) and the touch-plate TLO path —
  once verified, the manual's "behind the scenes"/callout wording may need a confidence-level pass.
