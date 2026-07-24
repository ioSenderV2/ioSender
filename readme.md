# ioSender V2 — an enhanced all-in-one grblHAL / Grbl g-code sender

This is [`ioSenderV2/ioSender`](https://github.com/ioSenderV2/ioSender) — **ioSender V2**, the ongoing offshoot of the [`stevenrwood/ioSender`](https://github.com/stevenrwood/ioSender) fork (itself a one-time fork of `terjeio/ioSender`). It carries a large set of enhancements and fixes delivered as **one all-in-one build** on `master` — not a menu of separately-selectable branches. Severed from upstream at the PR-era cutoff; the original upstream is no longer tracked or submitted to.

### 📖 [Online user manual →](https://iosenderv2.github.io/ioSender/)
A task-oriented guide with **novice / intermediate / machinist** reading paths, an A–Z subject index, search, diagrams and screenshots. Inside the app, press **`F1`** to open it at the page for whatever you're looking at.

### 📋 Changelog &amp; overview
[`Overview.pdf`](Overview.pdf) &nbsp;·&nbsp; [`Overview.html`](Overview.html) &nbsp;·&nbsp; [read it online →](https://iosenderv2.github.io/ioSender/overview.html)
— how V2 came off the PR-era archive, how the V2 repos wire together, and the **full changelog** (every feature and fix with diff stats and a description) folded in as the [`#features-and-fixes`](Overview.html#features-and-fixes) section.

`master` (default) is the whole product — clone and build it (build notes below); it is consumed whole. The upstream-submittable PR-era history lives frozen in the archive repo [`stevenrwood/ioSender`](https://github.com/stevenrwood/ioSender) at tag `pr-era-cutoff`.

### ⚡ Quick install (Windows, no build tools needed)
Open PowerShell and run:
```powershell
irm https://raw.githubusercontent.com/ioSenderV2/ioSender/master/install.ps1 | iex
```
Downloads the latest build, installs it to `%LocalAppData%\Programs\ioSender`, adds a desktop shortcut, and launches it. No admin rights needed. Re-run the same command any time to update.

---

## Highlights

- **All-in-one Job tab** — DRO, jogging (on-screen + keyboard, up to 9 axes), program list, live 3D toolpath/carve view, and console in one resizable layout, with a fixed run-control bar (Home/Unlock/Reset/Run/Feed Hold/Stop) always visible at the bottom.
- **Run, Dry Run, Check Run, or Simulate** — a single Run button whose dropdown picks the mode: dry-run watches the toolpath with spindle/coolant forced off and Z clear of stock, Check Run validates via grblHAL's `$C`, and Simulate switches the connection to a bundled grblHAL simulator for the run and switches back automatically when it ends.
- **Guided Start Job / Stepper Calibration / Auto Square / Surface Spoilboard wizards** — each builds and runs its own program from on-screen inputs, no hand-written g-code required.
- **Machine Setup wizard** — walks a fresh or newly-flashed controller through homing, ATC macro provisioning, and first-run checks.
- **Advanced settings editor** — grbl/grblHAL `$`-settings presented with on-screen documentation, dynamically generated from data files and/or the controller's own reported settings.
- **Offsets, Tools, Probing, Height Map, SD Card** tabs, each covered in the online manual.
- **Lathe mode**, including conversational threading on a grblHAL controller with spindle-sync support.
- **Bundled/matched simulator support** — a grblHAL simulator built to match your connected controller's options, launched from Settings, `-simulator`, or the Run dropdown's Simulate mode, with no manual download/build step.
- **ioSender XL** — an alternate main window with fully configurable, drag-to-place panels (a "main page editor") and pinnable flyouts, for a layout tuned to your machine and workflow.
- Localized into 7 languages (see [`Locale/`](Locale)) via `x:Uid`-driven LocBaml resources.

See [`Overview.html`](Overview.html) (or the [online version](https://iosenderv2.github.io/ioSender/overview.html)) for the complete, versioned changelog — every feature and fix, with a description.

#### Testing without hardware

No board yet? Settings → Simulator builds a grblHAL simulator matched to whichever controller options you want to test against — no separate download or manual build required. You can also launch straight into it with `ioSender.exe -simulator`, or pick **Simulate** from the Job tab's Run dropdown to run just one job against the simulator and automatically reconnect to your real controller afterward. Jogging, running programs, and settings all work; g-codes that need real input (e.g. probing) don't.

#### Releases

Stable builds are published as versioned GitHub Releases — see the [Releases page](https://github.com/ioSenderV2/ioSender/releases) or use the [Quick install](#-quick-install-windows-no-build-tools-needed) script above, which always grabs the latest. Dev builds (unreleased, built straight off `master`) are for testing only.

---

## Some UI examples

![Job tab — main run screen](Media/job-main.png)

Job tab: DRO, jogging, program list, and run controls together, with the fixed run-control bar always visible at the bottom.
<br><br>

![3D toolpath / carve view](Media/3d-carve-view.png)

3D view: live toolpath and material-removal (carve) preview, tool marker updates as the job runs.
<br><br>

![ioSender XL — configurable panel layout](Media/xl-panel-layout.png)

ioSender XL: the same controller functionality in a fully configurable, drag-to-place panel layout — pick which panels appear where, and pin flyouts (Offsets, Machine Position, ...) open.
<br><br>

![Machine Setup wizard](Media/machine-setup-wizard.png)

Machine Setup wizard: guides a fresh or newly-flashed controller through homing and ATC macro provisioning.
<br><br>

![Advanced grbl/grblHAL settings editor](Media/settings-editor.png)

Advanced settings editor, with on-screen documentation. The UI is generated dynamically from data files and/or the controller's own reported settings.
<br><br>

![Probing options](Media/probing.png)

Probing tab.

---
