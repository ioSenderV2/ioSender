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
- [ ] Machine Setup's in-app **Overview** step list (`MachineSetupWizard.xaml`, `ov_s1`–`ov_s6`) stops at six
      entries and is wrong from 6 onward — it says "6 · Controller macros" when step 6 is Fixture definitions
      and 7 is Controller macros. Missing Fixture definitions, Calibration and Build simulator entirely.
      Fixing it means 3 new `x:Uid` rows through `tools/locadd.py` across all 7 locales.

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

- [ ] **`#job` — a sub-section on the outline.** Where sections come from (an M6, or the add-in's own
      markers), the **Program start** and **Program end** groups, and the naming convention worth
      telling people about: *a comment immediately above the tool change becomes the toolpath's name*.
      That is opt-in behaviour a reader can use in their own posts and hand-written files.
- [ ] **`#job` — the two run commands**, right-click on a group: **Start from this toolpath** (runs
      Program start, then from there to the end) and **Run just this toolpath** (Program start, that
      toolpath, Program end). Both now run the program's own preamble rather than a synthetic one.
- [ ] **Screenshot**: this wants a figure of the grouped list with one section expanded. Fold into the
      `job-runscreen.png` reshoot already owed as priority 1 of the audit's §6 — a job loaded with
      three toolpaths would cover the run strip, Peek, and the outline in one shot.
- [ ] **Worth a mention in `#work-order`** too: a generated work order now outlines by tool change like
      any other program.

---

## Debt from Peek (shipped 2026-09-17) — RECORDED AS IT SHIPPED

Logged the same day, which is the whole point of this file and is what did not happen for the six
weeks the audit below had to reconstruct.

**Peek** is a new run-strip button: pause a running job at the next block boundary, park at `G30`
with the spindle off, look at the work, then Resume (or Cycle Start) to go back and carry on.
Hardware-verified 2026-09-17 — see `docs/Architecture-Peek.md`.

- [ ] **`#job` — the run strip.** The control list and the run-control table both need Peek adding.
      It sits to the right of Stop and is **collapsed when it would do nothing**, like Feed Hold and
      Stop (#355), so "I don't see it" is expected rather than a fault.
- [ ] **`#job` — a short sub-section on what Peek actually does**, because two things surprise:
      it takes effect at the **end of the current block**, not instantly (Feed Hold remains the
      immediate stop), and it parks at **G30**, not at machine home.
- [ ] **`#settings` — the Keyboard table.** The **Program** group gains a third row,
      *Peek / Resume*, beside MDI and Status. Unbound by default.
- [ ] **Refusals worth documenting**: Peek is not offered for an **SD card job** (the controller
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
        🔴 **App bug found while writing it:** `WorkOrderModel.cs:680` says the run strip's
        button reads "Generate (mark only)" while this is set. **No such string exists in the repo** —
        the comment is aspirational, and it is the justification given for persisting the flag. Either
        build it or correct the comment; the manual does not claim it.
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
  - [ ] **M6–M21** — the rest, in §2 of the audit.

### Process note — why this got to 156
The rule at the top of this file ("when shipping a UI change, add the impact here") did not run once in
six weeks. The 18 unticked boxes above it are July items superseded by later payoff sections that never
ticked them, so **the checkbox state is not a live signal either** — read the dated section headers, not
the boxes.
