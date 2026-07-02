# Claude Code guide — stevenrwood/ioSender fork

A **fork of [terjeio/ioSender](https://github.com/terjeio/ioSender)** carrying a stack of proposed
enhancements as clean, individually-reviewable `pr/*` branches. If you're using Claude Code to build,
run, or cherry-pick from this fork, start here.

## Read first
- **`Overview.html` / `Overview.pdf`** — the big picture across all three coordinated forks (this
  sender, the grblHAL **Simulator**, and the iMXRT1062 **firmware**): goals, each fork's PRs, the
  `apply-prs` composer, and effort/scale.
- **`FeaturesAndFixes.html` / `FeaturesAndFixes.pdf`** — every feature/fix in this repo: branch (or
  integration-resident), diff stats, description, dependencies. (Formerly `ProposedPRs.*`.)

## Build (Windows only — WPF, .NET Framework 4.6.2)
- Open **`ioSender XL/ioSender XL.sln`** in Visual Studio 2022, build **Release** — or:
  `msbuild "ioSender XL/ioSender XL.sln" -t:Build -p:Configuration=Release`
- Needs the .NET Framework 4.6.2 targeting pack. A couple of external DLLs (RP.Math, websocket-sharp)
  are referenced by relative `..\..\` paths; if a clean build can't resolve `RP.Math` or is missing
  `App.config`, see PR 1 in `FeaturesAndFixes`.
- **macOS / Linux:** not supported — WPF is Windows-only. Run in a Windows VM (the Simulator and
  firmware forks *do* build cross-platform; see `Overview.html`).

## Branch model
- **`master`** = upstream release + PRs 1–8 and 24 already integrated.
- **`integration`** (default branch) = `master` + every pending PR merged — the everyday working build.
- **`pr/<name>`** = one focused feature/fix each, branched off `master`.

## Compose a custom build — `tools/apply-prs.py`
Pick the PRs you want; it resolves the dependency closure, merges them off `master`, and builds to verify:
```
python tools/apply-prs.py my-build 15 16 21    # compose (16 auto-pulls 15), build Release, verify
python tools/apply-prs.py --list               # list every PR + baseline/composable tag + deps
python tools/apply-prs.py --check 9 10         # report same-line overlaps only (no branch)
```
PRs 1–8 + 24 are already in `master` (requesting one just skips it). `tools/forks.json` registers the
sibling **Simulator** fork (`--fork sim`); the **firmware** fork is all-or-nothing via
`srw/local-build-config` (its per-PR branches are for upstream submission, not subset builds).

## Conventions
- Line endings are **LF** (`.gitattributes` `* text=auto eol=lf`).
- Localization is LocBaml: a per-control `x:Uid` + one row per `Locale/<loc>/csv/*.csv` (all 7 locales).
  New UI strings need a row in each locale CSV; **values containing a comma must be CSV-quoted**.
- Keep each `pr/*` a clean single-feature diff: develop on `integration`, then route each change to its
  owning `pr/*` branch and re-merge.
