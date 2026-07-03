# Claude Code guide — ioSenderV2/ioSender

**ioSender V2** — an enhanced, **all-in-one** grblHAL / Grbl g-code sender. It is the ongoing offshoot of
`stevenrwood/ioSender` (itself a one-time fork of
[terjeio/ioSender](https://github.com/terjeio/ioSender)), severed from upstream at the **PR-era cutoff**.
Upstream is no longer tracked or submitted to; the product is the `master` branch, consumed whole. Start here.

## Read first
- **`Overview.html` / `Overview.pdf`** — the one document: the lineage diagram (how V2 came off the
  PR-era archive), how the V2 repos wire together at runtime, and the **full changelog** folded in as the
  `#features-and-fixes` section (every feature/fix with diff stats and a description). Superset of the old
  `Overview.html` + `FeaturesAndFixes.html`.

## Build (Windows only — WPF, .NET Framework 4.6.2)
- Open **`ioSender XL/ioSender XL.sln`** in Visual Studio 2022, build **Release** — or:
  `msbuild "ioSender XL/ioSender XL.sln" -t:Build -p:Configuration=Release`
- Needs the .NET Framework 4.6.2 targeting pack. A couple of external DLLs (RP.Math, websocket-sharp)
  are referenced by relative `..\..\` paths; if a clean build can't resolve `RP.Math` or is missing
  `App.config`, see entry #1 in `Overview.html`.
- **macOS / Linux:** not supported — WPF is Windows-only. Run in a Windows VM. (The sibling grblHAL
  **Simulator** and iMXRT1062 **firmware** repos do build cross-platform.)

## Repos (all under the `ioSenderV2` org, original names kept)
- `ioSenderV2/ioSender` — this WPF sender app.
- `ioSenderV2/iMXRT1062` — the grblHAL firmware (Teensy 4.1 driver + `core` submodule) the app connects to.
- `ioSenderV2/core` — grblHAL core, consumed as the firmware's submodule.
- `ioSenderV2/Simulator` — the option-matched grblHAL simulator (`build-matched-sim` CI + `sim-<sig>` releases).

## Branch model
- **`master`** (default) = the whole all-in-one product. There are no composable `pr/*` branches and nothing
  is staged for upstream — develop directly on `master`.
- The upstream-submittable history (`master` + `pr/*` + `apply-prs` + `ProposedPRs.html`) lives frozen in the
  **PR-era archive** repo `stevenrwood/ioSender` at tag `pr-era-cutoff`. `apply-prs`/`forks.json` are retained
  here too in case V2 ever adopts an outside-contributor PR model.

## Conventions
- Line endings are **LF** (`.gitattributes` `* text=auto eol=lf`).
- Localization is LocBaml: a per-control `x:Uid` + one row per `Locale/<loc>/csv/*.csv` (all 7 locales).
  New UI strings need a row in each locale CSV; **values containing a comma must be CSV-quoted**.
