# Claude Code guide — stevenrwood/ioSender fork

A **fork of [terjeio/ioSender](https://github.com/terjeio/ioSender)** carrying a large set of
enhancements and fixes as **one all-in-one build** on the `integration` branch. Developed against the
fork's own `master`; upstream is no longer tracked or submitted to. Start here.

## Read first
- **`FeaturesAndFixes.html` / `FeaturesAndFixes.pdf`** — the changelog: every feature/fix in this repo,
  with diff stats, a description, and how the pieces relate. (Formerly `ProposedPRs.*`.)

## Build (Windows only — WPF, .NET Framework 4.6.2)
- Open **`ioSender XL/ioSender XL.sln`** in Visual Studio 2022, build **Release** — or:
  `msbuild "ioSender XL/ioSender XL.sln" -t:Build -p:Configuration=Release`
- Needs the .NET Framework 4.6.2 targeting pack. A couple of external DLLs (RP.Math, websocket-sharp)
  are referenced by relative `..\..\` paths; if a clean build can't resolve `RP.Math` or is missing
  `App.config`, see entry #1 in `FeaturesAndFixes`.
- **macOS / Linux:** not supported — WPF is Windows-only. Run in a Windows VM. (The sibling grblHAL
  **Simulator** and iMXRT1062 **firmware** forks do build cross-platform.)

## Branch model
- **`master`** = the fork baseline (an upstream release + the first handful of fixes).
- **`integration`** (default branch) = `master` + every enhancement — the everyday working build; this
  is what ships and what you develop on.

## Conventions
- Line endings are **LF** (`.gitattributes` `* text=auto eol=lf`).
- Localization is LocBaml: a per-control `x:Uid` + one row per `Locale/<loc>/csv/*.csv` (all 7 locales).
  New UI strings need a row in each locale CSV; **values containing a comma must be CSV-quoted**.
- Develop directly on `integration` — it is the single all-in-one build (no composable `pr/*` routing).
