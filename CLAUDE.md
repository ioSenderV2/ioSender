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

## Working directives (agents: read before you touch code)
Short rules that apply to every task here, so they don't have to be repeated per prompt.

**Build & verify**
- Build with **`.\build.ps1`** (`-Configuration Debug|Release|Both`), **not raw `msbuild`**. It passes
  `-restore`, so the **`WpfUiTestServer` NuGet package** resolves. A bare `msbuild`/`dotnet build` fails to
  find that package — that means you forgot `-restore`, **not** that the repo is broken. Never "fix" it by
  editing a `.csproj` or vendoring a DLL.
- **Windows-only** (WPF). Don't attempt macOS/Linux builds.
- **A clean build is not "done."** Verify behavior: launch the app — optionally with `-testserver[=PORT]`
  (or `IOSENDER_TESTSERVER=1`) to drive the UI by `x:Uid` over localhost — and exercise the changed flow.
  If you couldn't drive it, say so ("unverified"); don't claim success from a compile.
- **Don't revert your own working changes** because a build looks broken — diagnose the build *command*
  first (almost always a missing `-restore`).

**Scope & design (solo user)**
- No back-compat, no migration shims, no feature flags — **default new behavior ON**.
- **Confirm an ambiguous design in one sentence before building it.**
- **Reuse existing engines before reinventing:** `MacroProcessor.Run` (run macros), **`AppDialogs.Show`**
  (message boxes — use this, *not* `MessageBox.Show`; it's UI-test-server aware), `GCode.File.AddBlock`
  (build g-code in memory), the existing probe macros. See the settings/architecture notes before adding tabs.

**Code conventions**
- **LF** line endings (`.gitattributes eol=lf`).
- **Outbound HTTPS** (e.g. GitHub) needs TLS 1.2 enabled explicitly on net462:
  `ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;` (idiom in `SimulatorManager.cs`).
- **New UI strings:** give the control an `x:Uid`, then add a row in **all 7** `Locale/<loc>/csv/*.csv`
  (English baseline) via `tools/locadd.py` — add the XAML file to its `TARGETS` if missing. Comma-containing
  values must be CSV-quoted. English fallback works, so this is low-urgency but grows silently.

**Git**
- Develop on **`integration`** (fixes) or a **`pr/*`** branch (new features). `master` = the product.
- **Don't commit or push unless asked**; if you're on the default branch, branch first.

## Build (Windows only — WPF, .NET Framework 4.6.2)
- **Headless (no VS GUI):** `.\build.ps1` — kills a running ioSender.exe, builds (MSBuild found via
  `vswhere`), optionally launches. `-Configuration Debug|Release|Both`, `-Launch`, `-Headless`, `-NoKill`.
  In VS Code: **Terminal → Run Task** (Ctrl+Shift+B = *Build Debug + Launch*). This is the one build
  entrypoint — the `.vscode/tasks.json` tasks all delegate to it.
- Or open **`ioSender XL/ioSender XL.sln`** in Visual Studio 2022, build **Release** — or raw:
  `msbuild "ioSender XL/ioSender XL.sln" -restore -t:Build -p:Configuration=Release`
- **NuGet dependency (don't be surprised):** `CNC Core` and `ioSender XL` consume **`WpfUiTestServer`**
  as a **`<PackageReference>` (v1.0.0 from nuget.org)** — the flag-gated in-process UI-automation server
  (published standalone at github.com/stevenrwood/WpfUiTestServer). There is **no in-repo `WpfUiTestServer/`
  project** anymore. These are legacy `net462` csproj with otherwise zero NuGet deps, so a plain
  `msbuild ... -t:Build` **fails to resolve the package** — you must pass **`-restore`** (or run a
  `nuget/dotnet restore` first). `build.ps1` already does this; prefer it.
- Needs the .NET Framework 4.6.2 targeting pack. A couple of external DLLs (RP.Math, websocket-sharp)
  are referenced by relative `..\..\` paths; if a clean build can't resolve `RP.Math` or is missing
  `App.config`, see entry #1 in `Overview.html`.
- **Unhandled exceptions:** the global handlers (App.xaml.cs) dump a timestamped, stack-traced entry to
  `%AppData%\ioSender\ioSender.crash.log` and exit `0xFA11` (64017). Interactive runs also show a dialog;
  set env `IOSENDER_HEADLESS=1` (or `build.ps1 -Headless`) to suppress it for unattended runs. VS's
  JIT debugger does **not** auto-attach (no managed `DbgManagedDebugger` registered).
- **macOS / Linux:** not supported — WPF is Windows-only. Run in a Windows VM. (The sibling grblHAL
  **Simulator** and iMXRT1062 **firmware** repos do build cross-platform.)

## Repos
App + simulator live under the `ioSenderV2` org (original names kept, full history preserved):
- `ioSenderV2/ioSender` — this WPF sender app.
- `ioSenderV2/Simulator` — the option-matched grblHAL simulator (`build-matched-sim` CI + `sim-<sig>` releases).

The grblHAL firmware was **not** migrated (its submodule web makes it all-or-nothing) — it stays in the fork, frozen-referenced at tag `pr-era-cutoff`:
- `stevenrwood/iMXRT1062` — firmware superproject; build branch `srw/local-build-config`.
- `stevenrwood/core` — grblHAL core submodule; build branch `srw/combined` (carries the WCS-rotation fix).

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
