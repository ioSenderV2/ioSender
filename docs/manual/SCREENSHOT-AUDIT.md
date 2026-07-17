# Manual screenshot audit — 2026-07-16

Comprehensive, read-only visual audit of every screenshot in `docs/manual/index.html`
against the current shipped UI. Supersedes/confirms the screenshot items in
`MANUAL-DEBT.md` (main-menu overhaul, #84, 2026-07-09) and adds two **new** content-drift
findings (Machine Setup wizard, Settings tabs) discovered since that debt was written.

Method: every `<img src="img/*.png">` in `index.html` was catalogued, every referenced PNG
under `docs/manual/img/` was opened and visually inspected, and topic content was
cross-checked against the current `MainWindow.xaml`, `MachineSetupWizard.xaml`,
`GrblConfigView.xaml`, `ProgramPanel.xaml`, `ProbingView.xaml`, `OffsetView.xaml`,
`SDCardView.xaml`, `LatheWizardsView.xaml` and `ErrorsAndAlarms.xaml`.

---

## 1. Executive summary

| Metric | Count |
|---|---|
| Screenshots referenced in manual (`<img src="img/...">`) | 13 |
| Existing screenshot files (`docs/manual/img/*.png`) | 13 |
| Missing screenshots (`shot-todo` placeholders) | 0 |
| **Outdated — needs reshoot** | **11** |
| — of which Critical (stale chrome only) | 9 |
| — of which Critical **+ content drift** (panel content itself stale) | 2 |
| Current / acceptable, no action | 2 |
| Topics with no screenshot and no placeholder (conceptual/text-only pages) | 5 |

Headline finding: **every full-window screenshot in the manual (11 of 13) is stale** —
each one shows the retired `File  Camera  Help` menu bar and the fully-removed
toolbar icon row (Open/Reload/Edit/Close icons + "Start Job (F1)" button) beneath it.
Only the two screenshots that are *not* full-window captures — the isolated Connect
dialog and the isolated Errors/Alarms dialog — are current, because dialogs have no
menu bar or toolbar of their own.

Two screenshots also have **stale panel content**, independent of the chrome, from
features shipped *after* the menu overhaul:
- `machine-setup-overview.png` — the wizard now has **8 steps**, not 6 (new "6 · Fixture
  definitions" and "8 · Build simulator" steps; "Controller macros" moved from step 6 to 7).
- `settings-grbl.png` — the Settings tab strip now has **8 tabs**, not 7 (new **Simulator**
  tab, tied to the `-simulator` CLI flag feature, changelog #130).

The corresponding **manual text** (the step/tab tables) is also stale for both of these
— see §4.

---

## 2. Detailed reshoot list

All 13 screenshots are full main-window or dialog captures at either **1628×1128** (main
window) or a tight dialog crop. Ordered by priority.

### Priority: Critical / High (Novice track)

#### `img/job-runscreen.png`
- **Topic:** The Job screen (running g-code) — `#job`
- **Reading track:** Novice #5, Intermediate #3
- **Reason:** Old `File  Camera  Help` menu bar; full old toolbar icon row (Open/Reload/Edit/Close
  + "Start Job (F1)") still shown beneath it; program-view title bar shows a plain static
  `NewServingPlatter` text with no controls — the current header shows a filename + **✕** close
  button (or **Load File...** when empty).
- **Capture notes:** Job screen with a program loaded, current menu bar (`Connect…  Camera  Help`),
  no toolbar row, program-view header showing the filename + close-✕ button.

#### `img/start-job-panel.png`
- **Topic:** Start Job — measure stock & set origin — `#start-job`
- **Reading track:** Novice #6, Intermediate #4, Machinist #8
- **Reason:** Same stale menu bar + removed toolbar row as above; the right-hand block-list
  panel shows a static `Start Job` title with no Load/close control (same header-bar drift).
- **Capture notes:** Start Job tab, current chrome, current program-view header style.

#### `img/machine-setup-overview.png`
- **Topic:** Machine Setup (commissioning) — `#machine-setup`
- **Reading track:** Novice #7, Intermediate #2, Machinist #4
- **Reason:** Stale menu bar + toolbar row, **and** the wizard step list itself is stale: shows
  only 6 steps (Overview, 1·Machine, 2·Home position, 3·Axis information, 4·Homing & limits,
  5·Probe definitions, 6·Controller macros). Current `MachineSetupWizard.xaml` defines **8
  steps** — a new **"6 · Fixture definitions"** step was inserted before macros, and a new
  **"8 · Build simulator"** step was appended; "Controller macros" is now step **7**, not 6.
- **Capture notes:** Overview page showing all 8 current steps, current chrome. Highest-value
  reshoot in the set — content, not just chrome, is wrong.

#### `img/errors-dialog.png`
- **Topic:** Errors & alarms — `#errors-alarms`
- **Reading track:** Novice #8, Intermediate #10, Machinist #10
- **Verdict:** **CURRENT — no action.** Self-contained dialog (no main-window chrome), title
  and tab headers (`Error codes` / `Alarm codes`) verified against `ErrorsAndAlarms.xaml`.

### Priority: Medium (Intermediate track)

#### `img/probing-tabs.png`
- **Topic:** Probing — `#probing`
- **Reading track:** Intermediate #5, Machinist #5
- **Reason:** Stale menu bar + removed toolbar row. Sub-tab content itself (`Tool length
  offset | Edge finder, external | Edge finder, internal | Center finder`) matches current
  `ProbingView.xaml` — chrome-only issue.
- **Capture notes:** Probing tab, current chrome, same sub-tab content is fine to reuse framing.

#### `img/tools-tab.png`
- **Topic:** Tools (surfacing, squaring, calibration) — `#tools`
- **Reading track:** Intermediate #7, Machinist #3
- **Reason:** Stale menu bar + removed toolbar row. Panel content (Surface Spoilboard sub-tab)
  looks structurally current — chrome-only issue.

#### `img/offsets-table.png`
- **Topic:** Work offsets (WCS) — `#offsets`
- **Reading track:** Intermediate #6, Machinist #6
- **Reason:** Stale menu bar + removed toolbar row. Offset grid/columns and Set/Get/Clear
  buttons match current `OffsetView.xaml` — chrome-only issue.

#### `img/sdcard.png`
- **Topic:** SD card jobs — `#sdcard`
- **Reading track:** Intermediate #9
- **Reason:** Stale menu bar + removed toolbar row. Panel content matches current
  `SDCardView.xaml` (grid columns, "List CNC files only", button labels) — chrome-only issue.

#### `img/gcode-viewer.png`
- **Topic:** The 3D g-code viewer — `#gcode-viewer`
- **Reading track:** Intermediate #8
- **Reason:** Stale menu bar + removed toolbar row. Shown with the "3D View" sub-tab active,
  so the new program-view header (Load File / filename+✕) isn't visible in this crop either
  way — chrome-only issue, but still needs a reshoot for the top bar.

### Priority: Lower (Machinist track only)

#### `img/settings-grbl.png`
- **Topic:** Settings (grbl & app) — `#settings`
- **Reading track:** Machinist #2
- **Reason:** Stale menu bar + removed toolbar row, **and** the tab strip is stale: shows 7
  tabs (`Grbl | App | Jogging | G Code | Keyboard & Controller | Macros | Main Page`).
  Current `GrblConfigView.xaml` has **8 tabs** — a new **Simulator** tab was appended (tied to
  the `-simulator` CLI flag, changelog #130). Content drift, not just chrome.
- **Capture notes:** Grbl sub-tab with search box, current chrome, tab strip showing all 8 tabs
  including Simulator.

#### `img/heightmap.png`
- **Topic:** Height map (surface compensation) — `#heightmap`
- **Reading track:** Machinist #7
- **Reason:** Stale menu bar + removed toolbar row. Panel content (Area/Grid/Probing controls)
  looks plausible/current — chrome-only issue.

#### `img/lathe-wizard.png`
- **Topic:** Lathe mode & wizards — `#lathe`
- **Reading track:** Machinist #9
- **Reason:** Stale menu bar + removed toolbar row. Sub-tab names (`Turning | Parting | Facing
  | Threading`) match current `LatheWizardsView.xaml` exactly — chrome-only issue.

### Current — no reshoot needed

#### `img/connect-dialog.png`
- **Topic:** Connecting to your machine — `#connect`
- **Reading track:** Novice #3
- **Verdict:** **CURRENT.** Isolated Serial/Network/Simulator connect-dialog crop, no
  main-window chrome, unaffected by the menu/toolbar changes.

#### `img/errors-dialog.png`
See above — CURRENT.

---

## 3. Missing screenshot list

**No `shot-todo` placeholders exist anywhere in the manual** — every topic that has a
`<figure><img …>` has a real (if stale) file behind it. `README.md` describes the
`shot-todo` placeholder convention, but the current manual doesn't use it: topics that
don't have a photographic screenshot simply have no `<figure>` at all. Five topics fall
into this category:

| Topic | Anchor | Reading track | Has SVG diagram(s) instead? | Notes |
|---|---|---|---|---|
| Intro to CNC — the big picture | `#intro-to-cnc` | Novice #1 | **Yes** — 3 custom hand-drawn SVG diagrams (coordinates, machine anatomy, tool-change flow) | Intentional — conceptual page, no screenshot needed. |
| Getting started | `#getting-started` | Novice #2 | No | Text-only ("main window at a glance" table + first-five-minutes list). A screenshot of the current main window (tab row, no menu/toolbar clutter) would strengthen this as the reader's very first visual of the app — **candidate for a new screenshot**, not just a reshoot. |
| Jogging & the DRO | `#jogging` | Novice #4 | No | Text-only. A screenshot of the jog pad + DRO (current chrome) would help — **candidate for a new screenshot**. |
| Getting clean, repeatable results | `#clean-results` | Intermediate #1 (opener) | No | Checklist/reference page, no single screen to show — likely intentional, low value as a screenshot target. |
| Accuracy, calibration & repeatability | `#accuracy-calibration` | Machinist #1 (opener) | No | Same as above — conceptual/checklist opener, likely intentional. |

Priority for the two real candidates (`getting-started`, `jogging`) is **Medium** — they're
early Novice-track stops without any visual anchor, but adding a screenshot is a scope
increase beyond "fix what's wrong," not required to pay down the audited debt.

---

## 4. Text updates needed

Confirmed by direct text search of `index.html` (cross-referencing `MANUAL-DEBT.md`'s
"Topic text to fix" list — all items below are still present and unfixed).

| Section anchor | Line(s) | Issue | Suggested fix |
|---|---|---|---|
| `#connect` | 556 | *"Open the connection dialog from **File → Connect…**"* — the File menu no longer exists. | *"Open the connection dialog from the **Connect…** menu"* (note it reads **Reconnect…** once connected). |
| `#job` | 795, 797 | *"**File → Load**"* / *"**File → Load Folder**"* — File menu removed; loading is now via the program-view header buttons or drag-drop. | Replace with: *"Use the **Load File...** / **Load Folder...** buttons on the program-view header"* (or drag-and-drop a file/folder onto the list). |
| `#job` | 834 | *"**File → Transform** can rotate the program…"* — Transform moved to the program list's right-click menu (per `MANUAL-DEBT.md`). | *"**Right-click → Transform** in the program list can rotate the program…"* |
| `#machine-setup` | 899–909 | The "six steps" table lists only 6 steps; the wizard now has **8** (new step 6 "Fixture definitions", renumbered step 7 "Controller macros", new step 8 "Build simulator"). Intro sentence *"An Overview page lists them"* and *"once all six steps are done"* (line 897) also say "six." | Update the table to 8 rows and change "six steps"/"six" → "eight steps"/"eight" throughout the section (lines 897, 899, 900). |
| `#settings` | 1039–1048 | The Settings tab table lists 7 tabs; current app has **8** (new **Simulator** tab after Main Page, tied to the `-simulator` CLI flag). | Add a `Simulator` row to the table. |
| *(none found)* | — | `MANUAL-DEBT.md` flags **"Open Console"** and **camera guidance** as things to fix/add, but no such text currently exists anywhere in the manual — it's not *wrong*, it's simply **absent**. Same for **Help → Support** submenu. | These are content **gaps**, not incorrect text: worth adding a line to `#job` (or a new callout) explaining "double-click the Console tab to pop it out," and a line to `#settings` or `#getting-started` about the opt-in Camera (Settings → App → Camera). Not urgent — no user is being actively misled, unlike the `File →` items above. |
| — | — | F1/context-help mappings — cannot be verified by static text search; requires exercising the app's F1 hook against each `HelpTopic` anchor. | Out of scope for this read-only audit; flag as a follow-up manual QA pass once the in-app F1 hook (`docs/manual/README.md` §"In-app deep-link hook") is implemented — per that same README, the hook is still design-stage ("Status" section), so this may be moot for now. |

All of the above were also called out in `MANUAL-DEBT.md`'s "Topic text to fix" section —
this audit **confirms every item is still outstanding** and adds no new text-drift findings
beyond the two content-table updates in §4 (machine-setup step count, settings tab count),
which are new debt from post-overhaul feature work, not part of the original menu-overhaul
list.

---

## 5. Cross-reference against `MANUAL-DEBT.md`

| MANUAL-DEBT.md item | Status after this audit |
|---|---|
| Any screenshot showing the old menu bar | **Confirmed** — all 11 full-window screenshots show it. |
| Any screenshot showing the toolbar-icon row | **Confirmed** — all 11 full-window screenshots show it. |
| Program view screenshots (`job`, `start-job`, `getting-started`) title bar stale | **Confirmed** for `job` and `start-job` (both show the old static-title header). `getting-started` has no screenshot at all (see §3). |
| `connect` text: "File → Connect…" stale | **Confirmed**, still present at line 556. |
| `job`/`getting-started`: "File → Load/Load Folder" stale | **Confirmed**, still present at lines 795/797. `getting-started` itself doesn't mention File menu loading, so only `job` needs the fix. |
| `job`: "File → Transform" stale (right-click menu now) | **Confirmed**, still present at line 834. |
| Console: "Open Console" menu item stale | **Not found in manual text** — nothing to fix, but also nothing documenting the current double-click behavior (gap, see §4). |
| Camera: now opt-in, needs guidance | **Not found in manual text** — pure content gap (see §4), no stale text to fix. |
| Help → Support submenu (new) | **Not found in manual text** — content gap, not urgent (submenu currently just holds "Open Application data folder"). |
| Stale-word sweep ("File menu", "Open Console", "toolbar", "Reload"/"Edit" icons) | Re-swept: only genuine hits are the three `File → …` items above (§4) and the `#gcode-viewer` figcaption's "toolbar" (line 1088), which refers to the **3D viewer's own view toolbar** — a false positive, not stale. |
| F1 / context-help mappings | Unverified — the in-app F1 hook is still design-stage per `README.md`, so likely moot; flagged as follow-up. |

**New findings not in `MANUAL-DEBT.md`:**
- `machine-setup-overview.png` **and** the `#machine-setup` step table are stale beyond the
  chrome — missing "Fixture definitions" (step 6) and "Build simulator" (step 8); "Controller
  macros" mislabeled as step 6 instead of 7.
- `settings-grbl.png` **and** the `#settings` tab table are stale beyond the chrome — missing
  the new **Simulator** tab (8th tab, tied to the `-simulator` CLI flag, changelog #130).

---

## 6. Suggested order of attack

1. **`machine-setup-overview.png` + its text table** — only screenshot/text pair with a
   *functionally wrong* step count; highest-value single fix.
2. **`job-runscreen.png`, `start-job-panel.png`** — Novice-track backbone, both show the
   stale program-view header pattern in addition to the menu/toolbar.
3. **Text fixes** for `#connect`, `#job` (×2) — quick, no screenshot dependency, remove
   actively-misleading instructions (File → Connect / File → Load / File → Transform).
4. **`settings-grbl.png` + its text table** — Machinist-track, but has the same "missing a
   real tab" content-drift problem as #1.
5. Remaining Intermediate-track chrome-only reshoots: `probing-tabs.png`, `tools-tab.png`,
   `offsets-table.png`, `sdcard.png`, `gcode-viewer.png`.
6. Remaining Machinist-track chrome-only reshoots: `heightmap.png`, `lathe-wizard.png`.
7. Optional scope add: new screenshots for `#getting-started` and `#jogging` (§3).
8. Optional content-gap fill: Console double-click note, Camera opt-in guidance, Help →
   Support mention (§4) — nothing is *wrong* here today, just undocumented.

Once reshoots land, follow `docs/playbooks/reimport_manual_screenshot.md` to swap the files
in and republish, then delete the paid-off items from `MANUAL-DEBT.md` (this audit doesn't
touch that file — leave the checkbox bookkeeping to whoever executes the reshoot).
