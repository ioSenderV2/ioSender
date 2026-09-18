# Manual audit — 2026-09-17

Read-only audit of `docs/manual/index.html` against everything shipped since the **v2.40 payoff**
(2026-08-04), which zeroed `MANUAL-DEBT.md` at changelog entry **#215**. This audit covers **#216
through #371** — 156 entries, 47 NEW / 30 CHG / 79 FIX — and **supersedes** both
`MANUAL-AUDIT-2026-07-24.md` and `SCREENSHOT-AUDIT.md` / `SCREENSHOT-REFRESH-2026-07.md`, whose
waves were paid off in v2.38 and v2.40 respectively.

**Method.** Every entry #216–#371 was read out of `Overview.html`, not skimmed by title. Each was
then sorted into "could an operator notice this" or not; the ones that could were grepped against
`index.html` for the terms they touch, and **every behavioural claim written below was checked
against source** — the XAML or the C#, not the changelog entry that described it. That last part is
not ceremony: the drift being paid off here includes at least one statement that was true of the
changelog and false of the app.

**Headline: this is a wider wave than either previous one, and of a different shape.** July 2026 was
a *restyling* wave — the same features, reshot. This one is mostly **new capability**: an SVG carving
path, a whole diode-laser path, text and V-carving, four new Work Order operations, a rebuilt Height
Map, a Calibration view with four pages, configuration overlays, parametric programs. Roughly
**21 subjects have no usable coverage at all**, against 6 in the July audit. The obsolete text is a
smaller problem than last time (16 sites) but includes **three statements that are actively wrong
rather than merely stale**, and two of those were left behind by the 2026-09-17 correction pass that
fixed their neighbours.

---

## 1. Executive summary

| Bucket | Count |
|---|---|
| **MISSING** — shipped functionality with no usable manual coverage | **21** (M1–M21) — ✅ **M1, M2, M3 done 2026-09-17** (new `#carving` and `#laser` topics); 18 left |
| **OBSOLETE** — manual asserts something that is no longer true | **16** (O1–O16) — ✅ **all applied 2026-09-17**, plus 2 uncatalogued siblings |
| **NEEDS UPDATING** — still-correct topics now incomplete | **13** (U1–U13) |
| **Screenshots** — all 19 referenced figures predate #216 | 19 (+ 4 orphans) |
| Changelog entries with no operator-visible surface | 61 of 156 (§7) |

**Running order.** §3 (the OBSOLETE pass) was taken first and is **done** — it was the cheap half
and it carried the wrong facts. The two demo-able features are **done** too: M1/M2 (`#carving`) and
M3 (`#laser`). What remains, in order: **M4 (Height Map)**, which needs the app
driven, and the **M5 remainder** (Mark only, which belongs in `#work-order`); then
§4's thirteen, which can ride along with whichever topic they touch; then the rest of §2 and the
reshoots.

---

## 2. MISSING — no usable coverage at all

> ✅ **M1 and M2 are DONE 2026-09-17** (`7ed98713`) — written as one new topic, **`#carving`
> "Engraving & carving"**, placed between Work Order and Machine Setup (intermediate:6,
> machinist:7), because the two share one engine and splitting them would have split the depth
> discussion. It also absorbs the parts of **M5** that belong with a carve — **Clear floor** (its
> depth read from the sibling Engrave, the *Below carve floor* rubbing trap, the ball-nose refusal)
> and **Mark** including Dashed. What is left of M5 is **Mark only**, which is a property of the
> *run* rather than of a carve and belongs in `#work-order`.
>
> Written from the app's own tooltips rather than the changelog — they are unusually good in this
> area — with every behavioural claim checked in source. Three things that check turned up, now in
> the topic and not previously in this table: Text and SVG are offered **no** Contour/Pocket/Chamfer
> at all; **Clear floor is absent on a single-stroke engrave** (no floor to clear), which is the
> likeliest "why can't I see it"; and the ball-nose message **blocks** Generate rather than merely
> advising.
>
> Still owed for these two: **screenshots**. A carve in the Work Order tree with the stock preview,
> and a negative badge, both listed in §6.

> ✅ **M3 is DONE 2026-09-17** (`cca9e5f4`) — topic **`#laser` "Burning an SVG on a diode laser"**,
> placed after `#carving` (intermediate:7). The `-enableSVGLaserJob` gate is the first thing on the
> page, in a warning box, and says the quiet half too: no flag means no keyboard row either, so there
> is nothing to bind and nothing to find. Written from the dialog's tooltips and
> `SvgLaserSettings`/`SvgLaserProgram`, with four behaviours this table had not catalogued —
> a placement outside the envelope is **refused, not trimmed** (and stated as non-advisory because
> these machines run `$20=0`/`$21=0`); the ramp's **CLAMPED** warning, without which a test strip
> compares a value against itself; shading depth follows the **interval** as much as the power, hence
> the areal readout; and an **aborted job leaves the `G92` applied**, because `G92.1` is the last
> line. Still owed: the three-tab dialog figure in §6.

"Usable" is doing work in that heading. Several of these are *named* somewhere — in a menu table or
a one-line parenthetical — without anything that would let an operator use them. Those are marked
**named only**, because deleting them from this list would be the same mistake as calling an
unanswered query an empty answer.

| # | Subject | Entries | Evidence of the gap | What it needs |
|---|---|---|---|---|
| **M1** | **SVG artwork as a Work Order toolpath** — choose a `.svg` as the geometry, set the artwork width, and it V-carves like lettering. Plus **Negative** (carve the background, art stands proud), **Panel: Rectangle / Artwork outline**, the **Border** field, and the **Max carve depth** cap that makes a narrow bit usable. | #262, #267, #268, #274, #300, #322, #323, #324 | `grep -c SVG index.html` → 1, and that one hit is the File-menu row (line 551), not this feature. `V-carve` → 1 hit, in the conceptual intro at line 399. | A new sub-section under `#work-order`, or its own topic. Must cover: width measures the **ink**, not the page; an import it cannot fully read is **refused** by name; the depth cap (0 = automatic); Negative + Border; Artwork outline needing one enclosing contour. |
| **M2** | **Text toolpaths** — single-stroke engraving, **V-carving** TrueType outlines, and **Shape text** (text fitted inside a Line/Circle/Oval/Square/Rect toolpath, cap height 0 = auto-fit). | #222, #223, #224 | `Text toolpath` → 0. `Shape text` → 0. The geometry list at line 1069 does not include Text. | Same section as M1 — they are one engine. Cover the V-bit included angle, the auto-fit rule, and that a line is a **baseline**. |
| **M3** | **The diode-laser path** — `File → Open` an `.svg` and burn it: outlines or shaded, placement (Origin X/Y, Copies and pitch), the **back-left anchor**, power-per-copy stepping, the exposure readout (power ÷ feed × interval), and **skew compensation** from a burned test square. | #273, #301, #302, #309–#315, #320 | `laser` → 0 hits (case-sensitive); `Laser` → 1, the File-menu row at line 551 — which is itself wrong, see **O12**. | Its own topic. **Note the gate:** the feature is held back by default and needs `-enableSVGLaserJob` (#315), so the topic must say so or it documents something the reader cannot find. |
| **M4** | **Height Map** as a topic — the Steps / Program / Surface Map panes, **Full work surface** (sets its own origin), the drop allowance, the colour legend in millimetres, **Retry/resume** after an alarm, and the per-point hold and **Continue**. | #283–#287, #290 | Two passing mentions only: a Tools-menu row (line 552) and "Probe height map" in Setup's Actions (line 991). No topic, no anchor. | A new topic. **`img/heightmap.png` already exists as an orphan** — check whether it is current before deleting it (see §6). |
| **M5** | **Clear floor / Mark / Mark dashed / Mark only** — an end mill flattening a V-carve's corrugated floor; a V-bit tracing an outline as a shallow groove, optionally dashed; and a run mode that dimples every hole centre and cuts nothing else. | #325–#329, #347, #348, #350 | `Clear floor` → 0. `Mark only` → 0. Neither appears in the operations list at line 1071. | Rows in the `#work-order` operations list plus short sub-sections. Mark only in particular needs its two guards stated — it is a program that *looks* like the job and is not. |
| **M6** | **Corner reliefs (dogbones)** — a checkbox on Square and Rect toolpaths so a square part actually seats in the pocket. | #259 | `Corner relief` → 0, `dogbone` → 0. | A paragraph in `#work-order`. Worth stating why it is off by default (each wall gets a nick ~⅓ of the cutter radius deep) and why Contour has no such option. |
| **M7** | **Indirect toolpaths and groups** — borrow another toolpath's geometry (Absolute or Relative offsets), label toolpaths into a group, and point an Indirect at a **group** so it copies the whole set as the set changes. | #270, #271, #272 | `Indirect` → 0. `Group` → 0. Indirect is missing from the geometry list at line 1069. | A `#work-order` sub-section. The payoff to state: adding a toolpath to the original adds it to every copy. |
| **M8** | **Save Drawing** — right-click the stock diagram → a dimensioned PDF shop drawing, with lettered colour balloons and a feature table listing every instance's X and Y. | #303, #304, #349 | `Save Drawing` → 0. | A `#work-order` sub-section. This is the feature that makes a work order portable to a second machine. |
| **M9** | **Scribe square** — cuts a rectangle inset 10 mm from the measured frame so a rotation error reads as a **taper** measured with calipers, instead of a point judged by eye. | #367 | `Scribe` → 0. The button exists: `StartJobView.xaml:484`. | A `#setup` sub-section. State that it **cuts** and leaves a permanent mark on the part — that is why it is its own button and not a Verify skew mode. |
| **M10** | **Squareness (probe) and the reversal test** — measure the gantry against a reference square; flip the square to separate *its* error from the machine's; the invert checkbox; the offset as a **delta**, not an absolute. | #368, #369 | Named as a page that exists (lines 1207–1208, 1297) with no method behind it. `reversal` → 1 hit, and it is about backlash (line 801). | Content under `#accuracy-calibration` or a Calibration topic. Two things must be said: **clear the offset first** (this tab writes a delta where the pins tab writes an absolute), and **the sign is not derived** — measure, Apply, re-measure. |
| **M11** | **Stepper calibration (scratch)** — N candidate steps/mm cut as N pairs of scratch lines, measured with calipers. A per-mark depth, the touch-plate probe option, and "Reference the loaded bit at the puck". | #330, #336, #337, #339, #342 | **Named only** — two table rows (lines 815, 1296) say it exists and that it needs no probe. No procedure. | A short how-to. It is the only calibration method with no hardware dependency, so it is the one a reader without a 3D probe needs. |
| **M12** | **Parametric programs** — `(PROMPT)` fields answered when the file is **loaded**, and bracket arithmetic folded for controllers with no expression support. One `square.nc` for every size. | #321 | `PROMPT` → 0. | A `#job` sub-section under "Loading a program". |
| **M13** | **The Job tab's split screen** — program list and 3D view at once, with a draggable splitter. Off by default, turned on in Settings. | #238 | `split screen` → 0. Not in the Settings → UI list at line 1393. | Two sentences in `#job` and a row in `#settings`. |
| **M14** | **Configuration overlays** — what an overlay *is*, the two merge modes, **Help → Support → Apply / Export / Undo**, and the `-overlay <file>` one-run form. | #247 | **Named only** — line 533 describes Export's output, line 553 lists the menu items. Nothing says what an overlay is, that Apply backs up and restarts, or that Undo exists. | A `#settings` sub-section. This is also the mechanism the tab-bar advice at line 531 depends on. |
| **M15** | **The status line, the Status window and the status log** — click the status line for every message since launch; the pop-up's dwell time and errors-only setting; the durable `status_<stamp>.log`; and what now gets recorded in it (connects with their settings count, run start/end, Generate, setting changes, dialogs and their answers). | #221, #246, #263, #280, #299 | `Status button` → 0, `message history` → 0. The one `status line` hit (line 1665) predates all of it. | A `#job` sub-section, cross-linked from `#errors-alarms`. This is the first thing to reach for when asking "what actually happened". |
| **M16** | **Restore points** — Restore now lists **moments**, each saying when, how long ago and what it holds; choose machine settings, app configuration, or both. | #296 | One stale mention: "It also keeps **backups / restore points**" (line 1422), which describes the settings-only scan that was removed. | Replace that clause with a short description of the dialog. |
| **M17** | **Work surface (spoilboard extent)** — a Machine Setup field for a board smaller than the machine's travel, read by Height Map's Full work surface and Work Order's Entire Spoilboard. | #289 | `Work surface` → 0. It is on **step 3 · Axis information** (`MachineSetupWizard.xaml:218`, inside `tabStepAxis`), which the step table at line 1194 describes without it. | A clause in the step-3 row plus a sentence in `#work-order`. The reason matters: travel describes the **gantry**, this describes the **board**, and conflating them ran a cutter across bare table. |
| **M18** | **Console search and the two text-size steppers** — every match highlighted (not just the current one), F3/F4 stepping, and separate Log / MDI font sizes. | #255, #256 | The pop-out console is covered (lines 937–943); its search is not. | Two sentences in the existing `#job` console callout. |
| **M19** | **Settings search points at the match on the page** — matched controls are marked and scrolled to, with a **dashed** mark for a tooltip-only hit. | #257 | `#settings` covers search-the-words and the "Matched tooltip:" hover (lines 1379, 1404–1410) but not the on-page marking, which is the half that landed later. | Extend the existing callout. |
| **M20** | **Machine mirror** — the window that reads machine state purely through the wire contract. | #218 | **Named only** — one Tools-menu row (line 552). | One line. Low priority; it is a developer-facing view. |
| **M21** | **Help → Support → Restart ioSender** — and *when* to reach for it. | #307 | **Named only**, inside a parenthetical at line 553. | One sentence, alongside M14 in `#settings`. |

---

## 3. OBSOLETE — the manual asserts something that is not true

> ✅ **ALL 16 APPLIED 2026-09-17** (`234cb927`). Kept below as the record of what was wrong and what
> settled it. The pass also turned up **two sites this table had missed**, both found by sweeping for
> the *claim* rather than the wording — the trap §5.1 names:
> - **O8 had a second wording**, `index.html:581`, "run Machine Setup once (**File → Machine**)" in
>   the first-five-minutes list. Fixed with it.
> - **O3 had a consequence this table did not follow through.** Two sites advised keeping an
>   "`R0`-before-`G53` habit so a leftover rotation never contaminates a machine-coordinate move"
>   (`:832`, `:1432`). Both halves of that are now wrong: grblHAL **exempts** machine moves from
>   rotation, so there is nothing to contaminate — and an `R0` **is** a rotation write, which is the
>   one thing that corrupts the parser's held position and turns the next Z-only `G53` lift into a
>   full-table rapid (#358, #359; the mechanism is documented at `StartJobView.xaml.cs:2789`). The
>   manual was advising the operator to do the dangerous thing as a safety habit. Replaced with a
>   warning under `#offsets` that says what actually needs care.
>
> Terminology was unified in the same pass: **"run bar" → "run strip"** at six sites, the app's own
> name for it since #220 and already what `#getting-started` said.

Each row names the source that settles it. **O3, O5, O6, O7 and O12 are wrong facts, not stale
emphasis** — an operator acting on them is misled.

| # | Site | What it says | Why it is wrong | Fix |
|---|---|---|---|---|
| **O1** | `index.html:864` | The run bar holds "Feed Hold/Stop/**Rewind**" | The Rewind **button** was removed in #233. `JobControl.xaml:168` carries the removal comment; the *mechanism* stays, the button does not. And #355 now **collapses** Feed Hold (unless the machine can move) and Stop (unless a job is streaming), so neither is unconditionally present either. | Drop Rewind. Add a line that Feed Hold and Stop appear only when they would do something. |
| **O2** | `index.html:676` | Ctrl-jog uses "the distance shown as *Jog step* in **the status bar**" | #220 **removed the bottom status bar entirely**. Everything it showed is on the run strip or the menu bar. | Repoint at the run strip's jog group. |
| **O3** | `index.html:1042` | "The work rotation uses grblHAL's `G10 L2 R` — measured skew is applied **as its negative**." | **Inverted.** #357 established the sign is `+θ` and fixed it; `StartJobView.xaml.cs:2528` and `:2557` emit `R{rotDeg}` from `Math.Atan2(...)` un-negated. The old negation pointed the work frame 2θ the wrong way. The comment that had justified the negation was itself confounded by a stock 1.7 mm out of square. | State `+θ`, and that the **origin is stored counter-rotated** because grblHAL pivots the rotation about **machine** zero, not the WCS origin. |
| **O4** | `index.html:1020`, and the bullet at `:999` | "Verify skew **re-touches the two back corners**" | #362 rebuilt it: the descent **is** the probe (a single probing move from machine top, so a stale tool-length offset can only miss, never drive in), each corner targets its own measured top, and there is a **Touch corners** checkbox — unticked makes it a **fly-over** that probes nothing. #365 added a V-bit picker, a 2 mm sight height, a tool-radius inset and crossing each back-corner pair at height. `StartJobView.xaml:454–484`. | Rewrite the callout. Six frame points, two modes, and what the fly-over is for. |
| **O5** | `index.html:1188` | `<h3>The nine steps</h3>` | Machine Setup has **eight** steps plus Overview — `MachineSetupWizard.xaml.cs:346` `GetPages()` returns Overview + Machine, Home, Axis, Homing, Probes, Fixtures, Macros, Simulator. The table directly beneath this heading already lists eight. Left behind by the 2026-09-17 pass that fixed the caption and the "(1–8)" list item. | s/nine/eight/. |
| **O6** | `index.html:1244` | Figure caption: "Note the **nine steps** and their status dots down the left." | Same as O5. This one also describes a screenshot that genuinely shows nine, so it needs the caption's *predates* treatment rather than a plain word swap. | Reshoot (§6) or add the predates line. |
| **O7** | `index.html:1247–1251` | Callout "**Calibration lives here now**": calibration wizards "are steps in this sequence instead". | **Directly contradicts the callout 45 lines above it** (`:1202`, "Calibration has moved out"), which is correct: #332 moved Calibration to its own view at `Tools → Calibration`. Two callouts in one topic saying opposite things is worse than either being wrong alone. | Delete the callout, or rewrite it as the history note it now is. |
| **O8** | `index.html:1178` | "**Machine Setup** — opened from **File → Machine**, in its own window" | #248 restored the full tab bar as the shipped default: `Default-App.config:179` `TabsKeys` = `GRBLConfig,FeedsAndSpeeds,StartJob,GRBL,Offsets,SDCard,WorkOrder,MachineSetup,LatheWizards`. Machine Setup is a **tab**. And even reached from a menu it opens as a tab, not a window (#332 — it declares `RequiresRunStrip`). | Rewrite. The `#getting-started` note at `:555` already states the rule correctly — match it. |
| **O9** | `index.html:1384` | "**Settings** — opened from **File → Settings**, in its own window" | Same. `GRBLConfig` is the **first** entry in `TabsKeys`. | Rewrite. |
| **O10** | `index.html:1437` | "The main bar **ships cut back** to what you need while a job runs, with the rest in the menus" | #248 reversed exactly this, and said why: a crowded default that can be slimmed with one overlay beats a slim default nobody can opt out of. | Rewrite to match `#getting-started`, which was corrected on 2026-09-17 while this was missed. |
| **O11** | `index.html:1453` | Keyboard "has **one group**, **Top Level Tabs**, holding every top-level destination: the views … and the main-menu commands that aren't views" | There are at least **three**. #282 split the menu commands out into **Top Level Menu Items** (`KeyMapEditor.xaml.cs:708` and `:717` define both constants); #254 added a **Program** group for the run strip's MDI and Status buttons (`ActionKeyBinder.cs:67–68`). | Rewrite the paragraph; add the Program group, which is genuinely useful (both stay live **during a run**, unlike the menu commands). |
| **O12** | `index.html:551` | File menu holds "**Load SVG Laser Job**" | `Features.SvgLaserJob` is a plain property defaulting **false** (`Features.cs:44`); `MainWindow.xaml.cs:3789` collapses `menuLoadSvgLaser` unless it is set, and only `App.xaml.cs:180` sets it, from `-enableSVGLaserJob` (#315). A reader on a shipped build will not find this entry. | Remove it from the File-menu row, and let M3's topic state the flag. |
| **O13** | `index.html:1069–1073` | Toolpath geometry is "line, circle, oval, square, rectangle or **surface**"; operations are "pocket, contour, drill, bore, side finish, bottom finish, chamfer" | `WorkOrderModel.cs:39` — `Line, Circle, Oval, Square, Rect, Surface, Indirect, Text, Svg`. `:66` — `Pocket, Contour, Drill, Bore, SideFinish, BottomFinish, Chamfer, Countersink, Surface, Engrave, ClearFloor, Mark`. **Three geometries and five operations missing.** | Rewrite both lists. This is the single edit that most understates what the app now does. |
| **O14** | `index.html:1119–1123` | "**Entire Spoilboard ignores your origin on purpose**" — it "touches off its own fresh Z0 and works in machine coordinates" | Now conditional. #290 added an opt-in to **cut on the origin already set**, precisely because after a Full work surface run the self-touch-off is harmful — it replaces a Z0 measured from sixteen probed points with one eyeballed touch. #289 changed where its extent comes from (the Work surface config, not `$130/$131`). #354 clears the rotation when it claims the scratch slot. | Rewrite the callout with the opt-in and the reason. |
| **O15** | `index.html:1446–1449` | Placement-editor figure caption: "Setup, Job, Work Order and Offsets are on the tab bar; Machine Setup and Settings are in the **File menu**" | Describes the pre-#248 four-tab default. True of the *screenshot*, false of the app. | Reshoot, or add the *predates* italic line the two other stale figures now carry. |
| **O16** | `index.html:677–680` | "Keyboard jogging works whenever the **Job screen is active** — even if focus has drifted to a side panel" | #260 made it universal: **every window, including dialogs**, with one rule — a jog key jogs and a shortcut fires unless you are typing in an input field. Jogging while a setup dialog is open is the case it was built for. | Rewrite the callout. Worth keeping: a key **release** always stops a jog whatever has focus by then. |

---

## 4. NEEDS UPDATING — correct, but now incomplete

These are not errors. They describe less than the app does, and a reader following them would work
harder than necessary.

| # | Topic | What to add | Entries |
|---|---|---|---|
| **U1** | `#job` | The run bar is now **the run strip** (#220): a five-column right half with Jogging, Signals and Overrides as real groups and live feed/spindle values; run controls, DRO and MDI on the left; **State** and **job elapsed time** raised into view; overrides drawn as a fill bar behind the value, **double-click to reset**. | #220, #355 |
| **U2** | `#job` | Loading: a 220k-line file now loads in ~3 s and the status line says so (#216, #229); the Data column no longer starts collapsed (#217); a program **wider than the machine can travel** is questioned before it runs (#291); and — worth stating plainly — a block ioSender's own parser cannot model is now **streamed verbatim** rather than silently dropped (#234). | #216, #217, #229, #234, #291 |
| **U3** | `#connect` | A first connect opens on **Serial**, not Network (#249); the connect now reports how many settings it read, the firmware options, and whether the board is already in alarm (#280); a controller left in **check mode** is recovered rather than looped on, and a slow clone board is offered more time (#293); a connect that could not read capabilities is **refused** rather than carried on blind (#235). | #235, #249, #280, #281, #293, #308 |
| **U4** | `#setup` | **Generate** now makes the program the loaded job and takes you to the Job tab to look at it; **Run** streams it from there (#236). **Esc** discards a handed-off program and returns you to the tab that built it (#343). The measure result **persists across restarts**, and the readout names the date it was taken (#363). | #236, #343, #363 |
| **U5** | `#work-order` | Generate reports that it is compiling and what the job will take, per toolpath and overall (#230); a disabled Generate now names the reason (#275, #331); a work order **records the blank it was authored for** (#306). Two **upgrade notes** belong here: a Bore's emitted feed changes where the cutter is narrower than the bore's path radius (#261), and an existing V-carve drops from six levels to two because Depth of cut now drives the step (#274) — fewer, heavier passes, worth watching the first one. | #230, #261, #274, #275, #306, #331 |
| **U6** | `#machine-setup` | Step 3 gains **Work surface (spoilboard)** — see M17. Step 7 now sends **only the macros that changed** (~3 s instead of 23 — #352), and **`tlo.macro` is a new required macro** (#339): an existing user will be offered an upload, and until they accept it any program calling it fails with `error:81`. Also: settings that have not arrived yet no longer display as **0**, and Apply will not write a zero back (#292). | #289, #292, #339, #352 |
| **U7** | `#settings` | New settings to list: status pop-up **dwell time** and errors-only (#246); **Job tab split screen** (#238, and M13); **ESC closes current tab if closable** (#333); **continuous jog** as an explicit setting (#228); **prefer network** now defaults off (#249); the AI Review Key the Feeds & Speeds topic already references. Plus the keyboard groups (O11). | #228, #238, #246, #249, #333 |
| **U8** | `#offsets` | A stored WCS origin a program **names as a prerequisite** is now checked against the soft-limit envelope before anything moves, with the overshoot stated in millimetres (#278) — this is the check that turns a mid-run `ALARM:2` with a probe fitted into a refusal at the start. | #278, #317 |
| **U9** | `#gcode-viewer` | The stock block takes its depth from **cutting moves only** — rapids, the machine-coordinate preamble and probe moves no longer inflate it — and where no size is known it says **"No stock size information"** and draws nothing rather than inventing a 150×150 stand-in (#237). `G53` park moves are drawn in the right frame now (#279). | #237, #279 |
| **U10** | `#accuracy-calibration` | The calibration list at lines 810–831 should name the **reversal test** (M10) and say that the (probe) squareness tab writes a **delta** — so clear the existing offset first. | #368, #369 |
| **U11** | `#errors-alarms` | Click the status line for the full message history (M15). Two new `ALARM:2` causes worth listing: a stored WCS sitting outside travel (#278) and a program bigger than the machine (#291). | #278, #291 |
| **U12** | `#sdcard` | Macro upload is per-file and hash-compared now (#352); a filesystem listing that goes unanswered reports **unknown**, not "no files found" (#243); the upload no longer runs at the status-poll rate (#346). | #243, #346, #352 |
| **U13** | `#tools` | The topic is correct as rewritten. Note only that **Probing** and **Height Map** are Tools-menu entries with no topic behind them — Height Map is M4; Probing has had no topic since the July deletion and is now the older gap of the two. | — |

---

## 5. Three process notes worth keeping

1. **The 2026-09-17 correction pass left siblings.** O5, O6, O7 and O10 are all the *same* four
   facts that pass corrected elsewhere in the file — eight steps, where Calibration lives, what the
   default tab bar is. The pass fixed the sites it grepped for and not the ones phrased differently
   ("nine steps" as a heading, a callout titled from the old arrangement). **Grep for the claim, not
   the wording.**
2. **Three of the "missing" items are named-only.** M11, M14, M20 and M21 all appear in a table row
   or a parenthetical. A word count would score them as covered. They are not.
3. **The debt file's own missing list was close but not right.** It listed "Help > Restart
   ioSender" and "configuration overlays" as having **zero** coverage — both are named (lines 533,
   553). It did not list **Height Map**, **the status log**, **restore points**, **Work surface**,
   **console search** or **settings-search highlighting** at all. Six subjects, one of them (M4) a
   whole topic.

---

## 6. Screenshots

**All 19 referenced figures predate #216.** **Five** now carry an italic admission in their captions
— two from the 2026-09-17 text pass, and three added by the correction pass (`job-runscreen`,
`machine-setup-calibration`, `settings-top-level-tabs`), whose content is *falsified* rather than
merely dated. An admission is a stopgap; all five still owe a reshoot. Ranked by how badly the shot
misleads:

| Priority | File | Why | Note |
|---|---|---|---|
| 1 | `job-runscreen.png` | The **run strip** replaced the run bar and the bottom status bar went (#220). This is the largest single visual change in the wave, on the manual's most-visited topic. | Also proves O1 (no Rewind). |
| 2 | `main-window-tools-menu.png` | Old four-tab bar, no Calibration in the menu. | Caption already admits it. |
| 3 | `machine-setup-overview.png` | Shows nine steps with Calibration expanded. | Caption already admits it; also O6. |
| 4 | `settings-top-level-tabs.png` | Its caption (O15) describes the pre-#248 four-tab default. | Reshoot fixes caption and figure together. |
| 5 | `start-job-panel.png` | Setup grew a Verify skew / Touch corners / V-bit picker / **Scribe square** row (#362, #365, #367) — the row that had to be made to wrap. | Needed by M9 and O4 anyway. |
| 6 | `work-order-composition.png` | The tree now carries Text, SVG and Indirect geometries and group headers; the operations list grew five kinds (O13). | |
| 7 | `machine-setup-calibration.png` | Depicts Calibration as Machine Setup step 8, which it is not (#332). | Should become a **Calibration view** shot instead. |
| — | the other 12 | Each depicts an area touched since #216 but not falsified by it. | Ride along with the topic session that needs them. |

**New shots wanted** by the missing topics. **The first two are now owed rather than speculative** —
the `#carving` topic shipped 2026-09-17 with no figure at all, which is the only topic in the manual
in that state: an SVG carve in the Work Order tree with the stock preview, and a **negative** badge
showing the artwork standing proud (M1/M2). Then the SVG laser dialog's three tabs (M3), Height Map's
Surface Map with its legend (M4), a Save Drawing PDF page (M8), the Calibration view's four sub-tabs
(M10/M11).

**Four orphans** — referenced by nothing in `index.html`:

- `heightmap.png` — **do not delete.** M4 needs a Height Map figure; check whether this one is still
  representative before shooting a new one.
- `odd-jobs-work-order.png`, `probing-tabs.png`, `tools-tab.png` — all depict retired arrangements.
  Delete.

Shooting needs the app driven: `build.ps1 -default-config -Shot <name>` for anything that should
match a fresh install, or hand captures. `-testserver` only if the user asks in the same turn, and
only against `-simulator`.

---

## 7. The other 61 entries — accounted for, no manual impact

Listed so the next reader can see that 156 were sorted, not sampled. Each of these is internal: a
crash fix, a performance fix, a race, a refactor, or a correction to something the manual never
described.

**Portability and engine work:** #218 (CNC.Core off WPF), #219 (one streaming engine), #240 (MDI
dispatch), #318 (CNC.Svg), #344 (one prologue/epilogue), #343's internals.

**Crashes and startup:** #232, #253, #265, #294, #298, #308, #330's BAML crash, #334, #335.

**Comms, connect and the wire:** #227, #242, #243, #244, #251, #252, #258, #276, #277, #281, #287,
#293, #346, #352, #356, #370.

**Performance:** #216, #229, #239, #245, #319, #360, #364.

**Safety fixes with no describable UI surface** — the operator sees only that it no longer goes
wrong: #225, #226, #228, #234, #241, #250, #264, #266, #278, #279, #284, #286, #287, #291, #295,
#316, #317, #340, #341, #351, #353, #354, #357, #358, #359.

**Correctness inside a feature already described:** #233, #237, #261, #267, #269, #271's tree
walker, #275, #292, #297, #300, #305, #312, #313, #322's cache key, #323, #326, #329, #331, #337,
#338, #345, #347, #348's two fixes, #350, #361, #363, #366, #371.

Several appear in both this list and §4 — a fix can be internal in mechanism and still change a
sentence (#237, #261, #274, #292 are the clearest). Where that is true the §4 row is the authority.

---

## 8. What this audit does not cover

- **Videos.** Four topics still carry `data-video="pending"` — connect, setup, work-order,
  machine-setup. Tracked separately.
- **The in-app bug that makes the app contradict the manual.** `MachineSetupWizard.xaml`'s Overview
  list is `ov_s1`–`ov_s6` — **six** entries ending "6 · Controller macros", which is really step 7,
  omitting Fixture definitions and Build simulator. The manual (once O5/O6 are fixed) correctly says
  eight. That is an **app** fix — two new `x:Uid` rows through `tools/locadd.py` × 7 locales — and it
  is in the way of the Machine Setup topic being trustworthy. Open since 2026-08-03.
- **`#intro-to-cnc`.** Conceptual, not tied to a release; unchanged by this wave.
