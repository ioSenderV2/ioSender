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

---

## Debt from v2.36 / v2.37 / v2.38 (#197–#208, shipped 2026-08-01 to 2026-08-03)

The last UI-invalidating wave before the manual rewrite. The **Settings and Machine Setup tab strips are
gone** (#208, replaced by one shared searchable navigation tree), **calibration moved into Machine Setup**
(#197), **spoilboard surfacing became a Work Order toolpath** (#198), and the **Tools tab lost half its
contents and now hides itself** (#204).

### Text — DONE 2026-08-03
- [x] `#settings` rewritten: nav tree + five categories (Controller / Application / Jogging / G Code /
      User Interface) replaces the 8-row tab table; search-the-words-on-the-page with match count and the
      "Matched tooltip:" explainer; Camera/Demo recording (OBS) split; why the Grbl page keeps its own
      `$`-tree; sub-tab key bindings dropped (top-level unaffected).
- [x] `#machine-setup`: eight steps → **nine** (new **8 · Calibration** with Stepper + Squareness
      sub-pages); "row of numbered tabs" → nav tree with green/orange/red status dots; new
      "Defining a fixture (step 6)" section covering the non-modal dialog, Test-offers-current-position,
      and per-fixture probe memory (**this closes the long-deferred M4 fixture-dialog item**).
- [x] `#tools` rewritten small per the user's call (2026-08-03: rewrite, don't delete — the tab is kept in
      code for other users' hardware): only Tool table / Trinamic / PID, each with its gating condition, a
      "no Tools tab? nothing is wrong" callout, and a where-did-the-rest-go table pointing at Work Order and
      Machine Setup. Figure dropped — `tools-tab.png` is now orphaned (it showed a removed tool).
- [x] `#work-order`: Surface toolpath + Entire Spoilboard (and why it ignores the work order's WCS on
      purpose), the WCS field (Follow Setup / pinned G54–G59), user-addable custom tools + name-based
      operation binding, Dry Run really neutralizes the spindle.
- [x] `#setup`: new "Plate thickness always applies" callout — touch-plate compensation is no longer gated
      on stock conductivity (#202), which was a real 12 mm Z error on hardware.
- [x] `#jogging`: jog-pad centre (bullseye / stop sign) and four corner buttons with the 20 mm holdback,
      plus the "targets the machine envelope, not the loaded program" note.
- [x] `#offsets`: new "Go To needs a homed machine" warn callout (#201 — the false-zero G30 crash).
- [x] `#job`: run bar dropped the feed unit label into a tooltip; both readouts size for five digits.
- [x] `#accuracy-calibration`: repointed steps/mm and squaring at Machine Setup → Calibration (the standalone
      manual/scratch wizards are deleted, not moved).
- [x] `#getting-started` tab table: Tools row now says it only appears if the controller supports it;
      Settings row mentions the nav tree.
- [x] Swept every stale `#tools` cross-reference (intro spoilboard, toolsetter callout, clean-results,
      accuracy xref, feeds-and-speeds xref) and `Settings → App` → `Settings → Application`.
- [x] Verified: all `href="#…"` anchors resolve, `<section>` tags balanced 18/18.

### Screenshots to reshoot/add — NOT DONE
Needs `-testserver`, so it needs the user's explicit turn-by-turn go-ahead.
- [ ] `settings-grbl.png` — **dead**, shot pre-nav-shell. Needs the tree + Grbl page.
- [ ] **New: a settings-search shot** — search box with a match count and ideally a "Matched tooltip:" hit,
      since that's the headline feature of #208 and the hardest to describe in words.
- [ ] `machine-setup-overview.png` — **dead**, shot pre-nav-shell. Needs the nine steps with status dots.
- [ ] **New: a Machine Setup → Calibration shot** — the step that absorbed the deleted wizards.
- [ ] **New: a Work Order Surface toolpath shot** — ideally with Entire Spoilboard ticked.
- [ ] `tools-tab.png` — now **orphaned** (no figure references it). Leave in `img/`, git keeps it
      recoverable; only reshoot if a Tools figure is ever wanted again.

### Known app bug found while auditing (NOT a manual bug)
- [x] **FIXED 2026-09-17** (`c42c7065`). Machine Setup's in-app **Overview** list stopped at six entries
      and was mislabelled from 6 on. It is now `ov_s1`–`ov_s8`, matching `GetPages()` and the manual's
      eight steps. (Carried here as open until 2026-09-18 on the strength of a memory note rather than
      a look at the file — the note was a week out of date. Read the source.)

---

## Debt from v2.39 (#209–#214, shipped 2026-08-03)

The **tabs-to-menus move**. The main bar was cut back to what a running job needs — Setup, Job, Work
Order, Offsets — and everything else went to the **File** and **Tools** menus, opening in its own
window (#210). The **Tools container tab is gone entirely**: its three hardware-gated tools are
individual Tools-menu entries. On top of that: interface preferences gathered under **User Interface →
General** (#211), a shortcut now names a *view* not a place plus the **Top-level tabs placement
editor** (#212), the jog pad's go-to buttons became optional (#213), and #214's fix batch moved the
console off **Esc** onto **F12**.

### Text — DONE 2026-08-04
- [x] `#getting-started` "The main window at a glance": the six-row tab table (which still listed
      Machine Setup, Settings and Tools as tabs) split into a **four-tab table** plus a **menu-bar
      table** for Connect / File / Tools / Help, and a callout that the split is a default — Top-level
      tabs moves anything anywhere, and a shortcut follows its view.
- [x] `#tools`: no longer "the Tools tab holds three utilities" — three separate Tools-menu entries
      under Camera, with the real labels (Tool table, **Trinamic tuner**, **PID Tuner**). The
      "No Tools tab?" callout became "Nothing under Camera in the Tools menu?".
- [x] Locations corrected: Machine Setup = **File → Machine**, Settings = **File → Settings** (both in
      their own window), SD Card and Feeds and Speeds = **Tools →**, and `#connect` now says the
      handshake enables the *views* a controller supports, not "the tabs".
- [x] `#settings` category table: **User Interface gained the General page** (#211) with what actually
      moved onto it, and **Application → Main** is described as controller comms rather than "colours,
      behaviour" — those left in #211.
- [x] `#lathe`: dropped "turn on lathe mode under Settings → Application → Main" — there is no such
      switch; lathe mode follows the controller reporting `LATHE` in `$I`. Wizards are at
      **Tools → Lathe Tools**.
- [x] Verified after the pass: every `href="#…"` resolves, 18 topic sections balanced.
- [x] Already current before this pass, checked not assumed: `#settings` placement editor + shortcut
      rules + search-owner naming (written when #212/#209 shipped), `#jogging` go-to-buttons checkbox,
      and the **F12** console key in `#job`.

### Screenshots — mostly DONE 2026-08-04
Shot with `build.ps1 -default-config -Shot <name>`, so they match a fresh install rather than this
box's saved layout; the script files the capture when you quit the app.
- [x] `main-window-tools-menu.png` (`#getting-started`) — Tools menu open over the tab strip. Setup sits
      *behind* the open menu (unavoidable: the menu drops from directly above it), and the menu is short
      because Camera needs a bound device and the tool/tuner entries need a controller that has them —
      both now said in the caption rather than pretended away.
- [x] `settings-top-level-tabs.png` (`#settings`) — placement rows with the Offsets dropdown open on all
      four destinations. Doubles as proof Settings opens in its own window.
- [x] `work-order-composition.png` (`#work-order`) — **replaces the planned Surface shot as the lead
      figure**, per the user: a real five-toolpath, fifteen-operation work order says far more than a
      spoilboard pass. The old `work-order.png` moved down to sit beside Generate.
- [x] `machine-setup-calibration.png` (`#machine-setup`) — **Z stepper via a 1-2-3 block, NOT a fixture.**
      The planned caption was wrong: a default config has no fixture *by design* (a fixture is a validated
      known position), and Z stepper calibration never needed one.
- [x] `work-order-surface.png` (`#work-order`) — a Surface toolpath with the **Feeds and Speeds dialog
      open**, which turned out to be worth more than the planned framing: it is the manual's only shot of
      the advisor, so the caption covers material, chip load and the ±10% nudge. *Entire Spoilboard* sits
      behind the dialog, so the caption says so rather than letting the figure imply it.

**No screenshot placeholders remain in the page.**
- [ ] `settings-grbl.png` and `machine-setup-overview.png` were reshot 2026-08-03 and are current for
      the nav tree, but both now open as **windows** rather than tabs — worth a glance to check the
      window chrome in the shot doesn't misrepresent where they live.

---

## Debt from the toolpath outline (#376, shipped 2026-09-17) — RECORDED AS IT SHIPPED

The program list now groups into collapsible **toolpath sections on any file with tool changes**, not
just on ioSenderV2 Fusion add-in output. That also makes two commands reachable that most users will
never have seen, because on an ordinary file they were not offered.

- [x] **`#job` — a sub-section on the outline.** Where sections come from (an M6, or the add-in's own
      markers), the **Program start** and **Program end** groups, and the naming convention worth
      telling people about: *a comment immediately above the tool change becomes the toolpath's name*.
      That is opt-in behaviour a reader can use in their own posts and hand-written files.
- [x] **`#job` — the two run commands**, right-click on a group: **Start from this toolpath** (runs
      Program start, then from there to the end) and **Run just this toolpath** (Program start, that
      toolpath, Program end). Both now run the program's own preamble rather than a synthetic one.
- [ ] **Screenshot**: this wants a figure of the grouped list with one section expanded. Fold into the
      `job-runscreen.png` reshoot already owed as priority 1 of the audit's §6 — a job loaded with
      three toolpaths would cover the run strip, Peek, and the outline in one shot.
- [x] **Worth a mention in `#work-order`** too: a generated work order now outlines by tool change like
      any other program.

---

## Debt from Peek (shipped 2026-09-17) — RECORDED AS IT SHIPPED

Logged the same day, which is the whole point of this file and is what did not happen for the six
weeks the audit below had to reconstruct.

**Peek** is a new run-strip button: pause a running job at the next block boundary, park at `G30`
with the spindle off, look at the work, then Resume (or Cycle Start) to go back and carry on.
Hardware-verified 2026-09-17 — see `docs/Architecture-Peek.md`.

- [x] **`#job` — the run strip.** The control list and the run-control table both need Peek adding.
      It sits to the right of Stop and is **collapsed when it would do nothing**, like Feed Hold and
      Stop (#355), so "I don't see it" is expected rather than a fault.
- [x] **`#job` — a short sub-section on what Peek actually does**, because two things surprise:
      it takes effect at the **end of the current block**, not instantly (Feed Hold remains the
      immediate stop), and it parks at **G30**, not at machine home.
- [x] **`#settings` — the Keyboard table.** The **Program** group gains a third row,
      *Peek / Resume*, beside MDI and Status. Unbound by default.
- [x] **Refusals worth documenting**: Peek is not offered for an **SD card job** (the controller
      streams those itself, so the sender cannot starve it) or on a controller without expression
      support (the park reads the stored `G30` parameters).
- [ ] **Screenshot**: `job-runscreen.png` is already owed a reshoot as priority 1 of the audit's §6.
      Whoever takes it should have a job running so the strip shows Feed Hold, Stop **and** Peek
      together — one shot pays off both items.

---

## Debt from #216–#371 (shipped 2026-08-04 to 2026-09-17) — AUDIT WRITTEN 2026-09-17

👉 **The item list is `docs/manual/MANUAL-AUDIT-2026-09-17.md`.** All 156 entries were read and
sorted there into **21 MISSING / 16 OBSOLETE / 13 NEEDS-UPDATING / 19 stale figures**, with the
evidence and the source that settles each one. Work from that file; this section is now just the
status board over it.

**156 changelog entries** shipped since the v2.40 payoff zeroed this file: 47 NEW, 30 CHG, 79 FIX.
Nothing was recorded here as it shipped, which is the actual failure. Unlike July's *restyling* wave,
most of this one is **new capability** — SVG carving, the diode-laser path, text and V-carving, four
new Work Order operations, a rebuilt Height Map, a Calibration view, configuration overlays,
parametric programs.

### Text — the five WRONG statements: DONE 2026-09-17
These were not gaps, they described the app incorrectly. Each replacement was verified against source
before it was written, not against the changelog entry that caused the drift.

- [x] **The four-tab default.** `#getting-started` said "Four tabs, left to right" and that everything else
      "lives in the File and Tools menus". #248 restored the **full nine-tab bar** as the shipped default
      (`DefaultLayout.Build()` **and** `Default-App.config`'s `TabsKeys`/Layout section, which agree:
      Settings, Feeds and Speeds, Setup, Job, Offsets, SD Card, Work Order, Machine Setup, Lathe Tools).
      Tab table and menu table both rewritten; the File/Tools menu contents were wrong with them, since
      SD Card, Feeds and Speeds and Lathe Tools are tabs again.
- [x] **"opens in its own window".** #332: a view whose descriptor sets `RequiresRunStrip` opens as a
      **session tab** instead — Setup, Work Order, Machine Setup and Calibration. Stated as a note, with
      the reason (the run strip's Generate/Run and jog pad exist only on the main window) and the
      closable-tab rule from #333.
- [x] **"Machine Setup → 8 · Calibration"**, four distinct sites. Calibration left Machine Setup in #332
      and is `Tools → Calibration` with **four** pages: Stepper (probe), Stepper (scratch), Squareness
      (pins), Squareness (probe). All four references repointed; the step-8 table row replaced by a note
      saying where it went and why.
- [x] **"All nine steps".** Machine Setup is **eight** steps now (1 Machine, 2 Home position, 3 Axis
      information, 4 Homing & limits, 5 Probe definitions, 6 Fixture definitions, 7 Controller macros,
      8 Build simulator — read off `GetPages()` and the `hdr*` headers). Caption, the "(1–9)" list item
      and the "why the order matters" paragraph all corrected.
- [x] **"The current target is shown in the status bar".** #220 retired the bottom status bar. The target
      reads at the **right-hand end of the menu bar** (`UpdateMenuBarInfo`: "Connected: TARGET" green /
      "Not connected" red, hidden on a narrow window). Corrected in `#connect`.

**Nearly shipped an invented fact and caught it:** the first draft said a compressed three-tab arrangement
"ships as a configuration overlay", which is what `DefaultLayout`'s own comment claims. There is **no
`.ioconfig` in the repo** — only the apply/export mechanism. Sentence rewritten to the verifiable claim.

### Screenshots — OWED
Two figures now carry an italic line in their caption saying what they predate, rather than being left to
assert something false. That is a stopgap, not a fix:
- [ ] `main-window-tools-menu.png` — shows the old four-tab bar and a Tools menu with no Calibration.
- [ ] `machine-setup-overview.png` — shows nine steps with Calibration expanded.
- [ ] Every other figure predates #216. **Now individually assessed** — see §6 of
      `MANUAL-AUDIT-2026-09-17.md`, which ranks all 19 by how badly the shot misleads.
      `job-runscreen.png` is priority 1, ahead of both of the above: the run strip replaced the run
      bar and the bottom status bar (#220), on the most-visited topic in the manual. Of the four
      orphaned images, **`heightmap.png` must not be deleted** — the new Height Map topic needs a
      figure; the other three depict retired arrangements and can go.

### STILL OWED — deferred to follow-on sessions (user's call, 2026-09-17)
- [x] **(2) Rebuild the audit** — DONE 2026-09-17, `MANUAL-AUDIT-2026-09-17.md`. It supersedes
      `MANUAL-AUDIT-2026-07-24.md`, `SCREENSHOT-AUDIT.md` and `SCREENSHOT-REFRESH-2026-07.md`, whose
      waves were paid off in v2.38 and v2.40 — open one of those only for history.
- [x] **(3a) The correction pass — DONE 2026-09-17** (`234cb927`). All 16 OBSOLETE sites from §3 of
      the audit, including the three wrong facts (the rotation sign, the SVG-laser menu entry, and
      the two Machine Setup callouts that contradicted each other). **Plus two sites the audit had
      not catalogued**, found by sweeping for the claim rather than the wording: a second wording of
      the Machine-Setup-is-a-window claim in the first-five-minutes list, and the
      **`R0`-before-`G53` habit** advised in two places — which is now worse than useless, since an
      `R0` is itself a rotation write and that is exactly what corrupts the parser position
      (#358/#359). Replaced with a warning under `#offsets` saying what actually needs care.
      "Run bar" was also unified to "run strip" throughout, the app's own name for it since #220.
- [ ] **(3b) The missing topics** — §2 of the audit, **21** of them, not the ~15 this file used to
      estimate. Four are **named-only** rather than absent (M11 scratch calibration, M14 overlays,
      M20 machine mirror, M21 Restart) — a word count scores them as covered and they are not.
  - [x] **M1 + M2 — DONE 2026-09-17** (`7ed98713`). One new topic, **`#carving` "Engraving &
        carving"**, covering Text / single-stroke vs V-carve / Shape text, SVG artwork, Negative and
        the two panel kinds, the depth model and **Max carve depth**, plus the parts of **M5** that
        belong with a carve (Clear floor, Mark, Mark dashed). Written from the app's tooltips, every
        claim checked in source.
  - [x] **M3 — DONE 2026-09-17** (`cca9e5f4`). New topic **`#laser` "Burning an SVG on a diode
        laser"**, after `#carving` (intermediate:7). The gate is stated in a warning box before
        anything else: no `-enableSVGLaserJob`, no menu entry **and** no row in Settings → Keyboard,
        so the feature is absent rather than hidden. Written from the dialog's tooltips and the
        emitter, not the changelog. Four things the audit had not catalogued and that a reader needs:
        placement is **refused rather than trimmed** (and why that is not advisory on a machine with
        `$20=0`/`$21=0`); the power ramp's **CLAMPED** warning; shading depth is set by the
        **interval** as much as the power, which is why the readout leads with an areal figure; and an
        **aborted job leaves the `G92` applied**, because `G92.1` is on the last line.
        **Still owed: a figure** — the dialog's three tabs, already listed in §6.
  - [x] **M5 remainder — Mark only. DONE 2026-09-17** (`14e8adc1`). In `#work-order`, under
        Running a Work Order, because it is a property of the *run*. Says the four things only the
        compiler knows — one dimple per **toolpath** (a Drill and a Bore on one toolpath is one
        hole), a Bore counts, no hole means an empty program refused up front, and the dimple's own
        tool/feeds through the ordinary dialog with the **dimple's** diameter rather than the bit's
        — plus the hazard as a warning: **the setting persists with the work order**, and what tells
        you is the summary line and the program's two MARK ONLY header lines.
        ✅ **The comment claiming a "Generate (mark only)" button was corrected** (`2e26663b`).
        It was a wrong COMMENT, not a bug: `JobControl.UpdateRunButtonLabel` takes the button's text
        from the `GenerateLabel` resource and nothing in the run strip reads the flag — so nothing
        misbehaved, but the claim was the stated justification for persisting `MarkOnly` and was one
        edit from reaching the manual. The comment now names the two things that really do say so: the
        summary line under the checkbox and the program's `*** MARK ONLY` header.
  - [x] **M4 (Height Map) — DONE 2026-09-17** (`0ddc3062`). Topic **`#heightmap`**, anchored on that
        name deliberately: `ManualHelp.cs` maps `ViewType.HeightMap` to it, so F1 from the view lands
        there. Covers the two modes as different jobs, the extent coming from Machine Setup step 3's
        **Work surface** rather than the travel, why the panel has no depth/feed field (they live in
        the probe definition), the drop allowance in terms of what it buys, **Retry refused on an
        alarm that lost position**, the legend's numbers, and the Entire-spoilboard + *Use the work
        origin already set* pairing. Four cross-links restored now the topic exists again.
        🔴 **`img/heightmap.png` is NOT current** — the audit's §6 said to check before
        deleting, and the answer is no: it shows the pre-#332 tab bar, the old run bar with Rewind,
        the bottom status bar, a *Full table* radio, Probe depth/feed fields that have since moved
        into the probe definition, and a single *Surface* pane instead of Steps/Program/Surface Map.
        A fresh shot is owed; nothing references the old one.
  - [x] **M6–M21 — DONE 2026-09-17.** §2 of the audit is now fully paid.
        - `1b0c1541` — **M6** corner reliefs, **M7** Indirect + groups, **M8** Save Drawing, all in
          `#work-order`.
        - `c14983d6` — **M10** squareness by probe and the reversal test, **M11** scratch stepper
          calibration, as a **new `#calibration` topic** (machinist:3) covering all four wizards.
          **M9** (Scribe square) turned out to be already covered by the O4 rewrite in `234cb927`;
          extended with its two prerequisites.
        - `f6318339` — **M12** `(PROMPT)` programs, **M13** split screen, **M15** the status line /
          Status window / status log, **M18** console search, all in `#job`.
        - `585a521c` — **M14** configuration overlays, **M16** restore points, **M19** on-page search
          marking, **M20** Machine mirror, **M21** Restart ioSender, all in `#settings`.
        - `85fef283` — **M17** Work surface, in `#machine-setup` step 3.
        ⚠️ **The audit had M15 slightly wrong**: it lists an "errors-only setting". There is no such
        setting — errors-only is the behaviour and the setting is the pop-up's dwell time. Written
        from source, which is why it was caught.

### ✅ §4 IS PAID — all thirteen NEEDS-UPDATING topics, 2026-09-20

U1–U13 of `MANUAL-AUDIT-2026-09-17.md` §4, written from source rather than from the changelog
entries the audit cites. Landed with the `#376` outline and Peek sections in the same pass.

- **U1 / U2** `#job` — the run strip (State's colour and its right-click recovery menu, the elapsed
  job time, the Signals lamps and the P/T probe-input double-click, the overrides' fill bars,
  double-click to reset feed/spindle and a SINGLE click for rapids, which is 100/50/25 only); loading
  ("Loaded <name> — N lines in T s", the Data column, the oversized-program question, the verbatim
  pass-through). Plus a warning the manual did not carry at all: `!` `~` `?` act wherever they appear
  in a line, so an exclamation mark inside a COMMENT is a feed hold mid-cut.
- **U3** `#connect` — what the handshake reports, and the three ways a connect goes wrong.
- **U4** `#setup` — Generate → loaded job → Job tab; Esc discards a handoff; the measurement
  persists and names its date.
- **U5** `#work-order` — the Generate report and its estimate's limits, the reason on a disabled
  Generate, the recorded blank, and the two upgrade notes (V-carve step, Bore feed) as a warning.
- **U6** `#machine-setup` — a "Controller macros (step 7)" section; `tlo.macro` and `error:81`.
- **U7** `#settings` — three settings were missing from the UI→General row; four given their own
  section.
- **U8** `#offsets` — the reachability guard, with the real refusal text.
- **U9** `#gcode-viewer` — the stock block, and `G53` drawn in the right frame.
- **U10** `#accuracy-calibration` — clear the offset first, and do the reversal test.
- **U11** `#errors-alarms` — the two pre-run `ALARM:2` catches, and where a message that has gone
  can be found.
- **U12** `#sdcard` — per-file hash-compared macro upload, the poll-rate fix, and unknown ≠ empty.
- **U13** `#tools` — a note placing Height Map and Probing.

🔴 **Three OBSOLETE sites the audit did not catch**, all found by checking a claim in source
before writing near it:
1. **"Pick the point in the 3D viewer"** to start a job partway through — in BOTH `#job` and
   `#gcode-viewer`. `StartFromBlock` has exactly one caller, the program list. Corrected in both.
2. **The "status line"** — the manual described a permanent status line showing the latest message.
   The run strip lost its message line on 2026-08-10 and only a FLAGGED message is displayed at all
   (`GrblViewModel.SetErrorMessage`'s own comment says so). Six sites reworded; `#job`'s M15 section,
   written on 2026-09-17, was one of them.
3. **#292 as the audit summarised it** — "settings that have not arrived no longer display as 0".
   What the code does is leave the field AS IT WAS and reschedule a re-read. Written from the source.

**Still owed from this wave: the screenshots only** — §6 of the audit, `job-runscreen.png` first.

### ✅ The F1 anchors are FIXED (`2e26663b`, 2026-09-18)
`ManualHelp.cs`'s map had drifted from the manual and the failure is silent — a dead anchor still
opens the manual, the browser just does not scroll, so F1 looks like it worked. `StartJob` pointed at
`start-job` (renamed `#setup` 2026-07-31) and `Probing` at `probing` (deleted 2026-08-01); `Probing`
now points at `#setup`, where what that tab did actually lives. **FeedsAndSpeeds**, **WorkOrder** and
**Calibration** were absent from the map and opened the front page; all three are in it now. Every
anchor was checked mechanically against the section ids here, including the two literal
`ManualHelp.Open` call sites, and `ManualHelp.AllTopics` is exposed so that check can be automated.
**The rule this leaves:** rename or delete a topic and grep `ManualHelp.cs` in the same edit.

### Process note — why this got to 156
The rule at the top of this file ("when shipping a UI change, add the impact here") did not run once in
six weeks. The 18 unticked boxes above it are July items superseded by later payoff sections that never
ticked them, so **the checkbox state is not a live signal either** — read the dated section headers, not
the boxes.

---

## Debt from the setup + 3D-view wave (shipped 2026-09-20/21) — RECORDED AS IT SHIPPED

38 commits over two days. Machine Setup's tool-length step became an interview, probe definitions
gained a plate *kind* and lost two settings, the DRO title became an offset picker, the jog pad
gained a Go-to button, and the 3D view changed substantially — including **the deletion of a
Settings page**. Recorded as it shipped, which is the rule the process note below says stopped
running for six weeks.

### 🔴 A Settings page NO LONGER EXISTS
- [ ] **`#settings` line 2584** — the **G Code** row promises "**GCode Viewer** (how the 3D view
      draws it)". That page is **deleted** (`360a8976`). It drove the OLD renderer, which the UI has
      no way to reach, so every option on it was inert: nothing on it ever affected the 3D view in
      the Job tab. Rewrite the row to mention GCode command stripping only, and point viewer options
      at the view's own **View options** button.
- [ ] Sweep for any other mention of viewer settings living under Settings.

### `#gcode-viewer` — the 3D view changed in five ways
- [ ] **Line 2889 and line 2900** both describe a **Reset view** button. There is no such button:
      it is now **View options**, and Reset view is a button *inside* that dialog. Line 2900 also
      claims a <kbd>Ctrl</kbd>+<kbd>V</kbd> shortcut — **checked in source 2026-09-21: that is the
      OLD renderer's** (`RenderControl.xaml` lines 77-78, with <kbd>Ctrl</kbd>+<kbd>R</kbd> for a
      "Restore view" that the carve view has no equivalent of). The carve view has no keybinding at
      all. Line 2900 is describing a screen the user cannot reach — rewrite the whole entry, do not
      patch the shortcut.
      ⚠️ Related CODE debt, not manual debt: **`KeyMapEditor` still offers `RenderControl.ResetView`
      and `RenderControl.RestoreView`** as bindable actions (lines 825-826, 909-910). They bind to
      nothing reachable. They go with the old-renderer removal.
- [ ] **New: the View options dialog.** Four toggles (rapid moves, stock block, bed grid, stored
      positions), three colour pickers (cut, rapid, stock), Default colours, Reset view. Document
      that colours persist, and the reason a lit surface does not match its swatch while the cut and
      rapid *lines* match exactly.
- [ ] **New: stored-position signs.** G30 and the toolsetter (G59.3) are drawn as **100 mm signs
      painted on the bed** — a parking P for the park, TS for the toolsetter — when taught. Say that
      an all-zero position is treated as never taught and is not drawn.
- [ ] **New: a green dot at machine zero**, drawn only when homing is enabled. Worth explaining
      *why*: it is at the top of Z travel, so the gap between it and the stock is the headroom.
- [ ] **Gone: the machine-envelope wireframe box.** If any text mentions a box around the work
      envelope, remove it — the bed grid carries the footprint now.
- [ ] The tool cone now **follows the live machine position** while jogging (`43201cf6`, `a1389639`).
      If the manual says the view only moves during a program, that is now wrong.

### `#machine-setup` — step 5 is a different screen
- [ ] **Line 2302** describes step 5 as "Declare the probes fitted to this machine (touch plate /
      3D probe / toolsetter)". It is now a **three-part interview** and needs a real section:
      1. what probes you have, editing the seeded touch plate or defining new ones;
      2. which probe measures tool length — **asked after the probes exist**, not before;
      3. the positions: target surface Z (only when a plate, not a toolsetter, does tool length),
         then G59.3, then G30, with an offer to place G30 100 mm to either side of G59.3.
- [ ] **`$65` bit 3 is now an OUTPUT of step 5** (`5161b4eb`, `220a1045`). Explain the trap it
      closes: touch plates wired OR'd onto the probe input must NOT auto-select the toolsetter
      input, and the button says "Turn it on"/"Turn it off" according to the probe chosen, greyed
      when the controller already agrees.
- [ ] **The tool-length search distance is computed**, not typed — from the probe's height and the
      G59.3 Z, as a **floor** (`5c9588da`). Do not describe it as a prediction of the target top.
- [ ] **Reference TLO runs in machine coordinates** and no longer leaves G59.3 as the active WCS
      (`2916313d`). If any text says to select G59.3 first, delete it.
- [ ] Mention the advice the interview gives: put G30 and G59.3 **adjacent**, and give a plate used
      as a toolsetter a **repeatable seat** (a shallow cutout) so it lands the same way every time.

### Probe definitions — `#setup` and `#machine-setup`
- [ ] **Two touch-plate kinds**: corner (two 90° lips) and Z-only (flat). A corner plate can be
      **turned upside down** to act as a Z-only plate, and it says so when chosen for tool length
      (`96d31252`).
- [ ] **The 3D probe is no longer seeded** — most users do not have one. Any "you will find a 3D
      probe already defined" wording is wrong.
- [ ] **Travel speed is GONE** from every probe definition (`43ef1da1`) — nothing read it. Also
      **Target height** (`cdc6f3af`). Check the probe-settings tables for both.
- [ ] **Motion parameters hide Edge standoff and Drop to side for a flat plate** (`fdaae67d`) —
      they only mean something when probing sideways.

### `#job` — two new controls
- [ ] **The DRO title is an offset picker** (`3740a198`). Clicking "DRO (G54)" drops down the
      selectable work offsets with the current one ticked; hovering one shows its X/Y/Z and
      rotation. It works in **both** places the DRO appears — the Job tab and the run strip — which
      is the point: the run strip is reachable when the Job tab is not.
- [ ] **A Go-to button** in the jog pad's empty 3-o'clock square (`bb20dd47`). It lists the stored
      positions — fixtures, G28, G30, G59.3 and any non-zero offset — and moves there. **It drives
      Z only for G30 and G59.3**, which are over clear air by definition; everything else is XY
      only, at the current Z (`7e0556bb`). That distinction is a safety point, not a detail.
- [ ] **The Work Parameters offset dropdown no longer lists G28/G30/G92** (`4f1d17e4`). They are
      stored *positions*, and selecting one sent its code to the controller — which **rapids the
      machine** from a dropdown next to the tool selector. If the manual documents that list,
      correct it and say why.

### Screenshots to reshoot
- [ ] **Machine Setup step 5** — entirely new; the old shot shows a screen that no longer exists.
- [ ] **Probe definition dialog** — plate-kind radios, inversion note, lip field only for corner.
- [ ] **Probe motion parameters** — Travel speed gone; two fields hidden for a flat plate.
- [ ] **The DRO panel**, both places — the title is now a dropdown affordance.
- [ ] **The jog pad**, both places — the 3-o'clock square is no longer empty.
- [ ] **The 3D view** — signs on the bed, green dot, no wireframe box, **View options** button.
- [ ] **Settings → App → G Code** — and any shot of the settings tree showing a GCode Viewer page.

### Not manual debt, recorded so it is not mistaken for debt
- The link gate (`7d7568b3`), the cone/envelope/marker late-arrival fixes and the remote debounce
  (`f21bc167`) are behaviour fixes with **no UI surface**. Nothing to document.
