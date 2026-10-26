# The turn loop — ioSender

> **Extends: `claude-hub/playbooks/turn_workflow_loop.md`** — that is the canonical loop and the
> description of how the hooks enforce it. Only ioSender's own specifics are below.

The mechanism lives in `claude-hub/hooks/`; this project's strings live in
[.claude/turn-config.json](../../.claude/turn-config.json) — the checklist, the source extensions,
the verify command, and the deny rules. Change behaviour there, not in the hook scripts.

```powershell
c:\github\claude-hub\tools\turn.ps1 status                       # what this turn has done
powershell -File c:\github\claude-hub\hooks\test-gates.ps1       # 21-case gate regression suite
```

---

## Builds — the part that is ioSender-specific

### 🔴 The `-Launch` build IS the compile check. Build FIRST, then commit and summarise.

```powershell
.\build.ps1 -Launch -message="what we're testing"
```
The message tells the user, the instant the window appears, what they're looking at. This is the
project's `verify.command` — the Stop gate blocks the turn without it once `.cs`/`.xaml`/`.csproj`/
`.resx`/`.config` files have changed.

**Order: edits → `-Launch` → commit → summary.** Not edits → `-Scratch` → commit → `-Launch`. A
compile error surfaces identically either way, so a `-Scratch` run whose only successor is a
`-Launch` run **bought nothing and cost a whole build**: the output is discarded, the app is rebuilt
from scratch, and the user sat through both. **The user can be testing the UI while the commit and
the write-up happen** — that is the point. Hold the commit until the build is green, then commit
while they test.

**Measured 2026-10-26:** `-Scratch`, then a commit, then `-Launch`, nothing else in between. The
user's words: *"You ran build.ps1 twice and the only thing in between was the commit… Wasting my
time. I can be testing ui while you do the commit and summary."* `build.ps1` now warns when it sees
that pair (see **the wasted-pair warning** below).

**`-Scratch` is for exactly one situation**: a live instance is mid-test **and the turn is not about
to end**, so you need a compile check that cannot disturb it:
```powershell
.\build.ps1 -Scratch
```
If the next thing you intend to run is `-Launch`, you did not need it. "The user has an instance
running" is **not** on its own a reason — `-Launch` kills and replaces that instance anyway, which
is what you want when the turn is ending.

**Locale pass**, while the user tests:
```powershell
$env:PYTHONIOENCODING = "utf-8"; python tools/locadd.py
```
Derives all 7 locales from the `x:Uid`s already in the XAML. See `localization_pass.md`.

## Hard rules

**🔒 No `-testserver` unless the user asks in this turn** — and even then only against
`-simulator`, never a real controller. Check `GET /state/lbl_connectionTarget` before any
MDI/motion/homing/reset call. Reaching for it to self-verify caused the 2026-07-21 incident where
MDI commands hit the user's real machine mid-homing. All live-hardware testing is the user's.

**🔒 No `git push`, no release, inside a turn.** Pushing to `v2/master` fires the release CI; that
belongs to `end_of_session_wrapup.md`.

**Push remote is `v2`, never bare `origin`** — `origin` is the frozen archive
(`stevenrwood/ioSender`), `v2` is the product repo (`ioSenderV2/ioSender`).

**Don't self-verify visual changes** by launching + testserver + screenshot. Build clean and stop;
the user checks for free.

---

## Settled — do not re-litigate

Each cost a round of back-and-forth. They are closed.

- **`-Launch` on the final build only.** Went back and forth 2026-07-25 (never / first-of-batch);
  landed on: the truly last build of the turn gets it, interim builds never do.
- **`build.ps1` kills a running `ioSender.exe` on *every* build**, `-Launch` or not — so an interim
  build after a launch silently leaves nothing running. Caught 2026-07-30.
- **`-NoKill` does not solve that** — it fails outright (MSB3027/MSB3021, file locked) as soon as
  there's a real rebuild. Windows won't let MSBuild overwrite a loaded DLL.
- **`-Scratch` is the real fix** — redirects the solution's output to `bin\<Config>.scratch\` via
  MSBuild's `OutDir`, so an interim build never touches locked files. Implies no launch, no kill.
- **`-message=` multi-word** works: `build.ps1` quotes whitespace-containing args before joining
  them for `Start-Process` (fixed 2026-07-30). If only one word shows again, check that fix first.
- **`OutDir` trailing backslash** must be doubled (`\\`) — Win32's argv parser reads a lone trailing
  `\` before a closing quote as an escaped quote, absorbing every following MSBuild arg.
- **Commit as you go**, not saved up. Corrected 2026-07-30.
- **Never `msbuild /t:Rebuild`** — sibling `HintPath` DLL folders don't exist here, so a clean can't
  re-resolve `RP.Math`/`websocket-sharp`. Use `/t:Build`, and always pass `-restore`.
