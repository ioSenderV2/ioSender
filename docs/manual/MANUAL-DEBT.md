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

## Debt from the probe-macro consolidation (shipped 2026-07-09)

Dropped `cal.macro` + `probe_tfl.macro`; the sole corner probe is now **pcorner.macro** (a superset). Impact:

- [ ] `docs/Repeatable-Tool-Change.html` — describes `probe_tfl.macro` probing the spoilboard / stock top /
      geometry (~lines 225, 368, 399) and lists it as an installed macro. Rewrite to reference **pcorner.macro**
      (and note the provisioned set is now just **tc + pcorner**).
- [ ] Any online-manual (`docs/manual/index.html`) mention of the ATC/tool-change macro set naming
      "cal"/"probe_tfl" — update to "tc, pcorner".
- [ ] Localized copies of the changed UI strings (SDCardView "Install ATC" tooltip/prompt, MachineSetupWizard
      Step-6 macro-status text) still say "cal, probe_tfl" in the 7 `Locale/**/*.csv` rows — sweep on the next
      loc pass.
