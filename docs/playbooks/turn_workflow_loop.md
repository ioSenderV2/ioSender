# The turn loop

**This is the canonical, single source of truth for how a turn runs.** Nothing else describes this
loop. If another document appears to, that document is stale — fix it, don't follow it.

A **turn** = the user gives a prompt → Claude plans → implements → builds → commits → hands back
something the user can test. One prompt in, one testable state out.

Part of it is **enforced by hooks**; the rest is injected into context at the head of every turn.
See [Enforcement](#enforcement) at the bottom for which is which — and why the split falls where it
does. Steps marked 🔒 are hard-blocked by a hook; you cannot skip them.

---

## The loop

**1. Start.** Every turn starts from a user prompt — new work, feedback, or test results. Never
self-initiate work, and never decide unilaterally what gets tested next.

**2. Plan before touching anything.** When the prompt asks for a change, state your understanding
and intended approach *in your visible response* before the first `Edit`/`Write`. Investigation
(grep/read to scope it) is fine first. Then record it:
```powershell
.\tools\turn.ps1 plan "restate the ask + the approach in one or two sentences"
```
A plain question from the user ("can we…?", "what if…?") is a **discussion** turn — answer it, don't
implement. This step is **audited at the end of the turn, not gated** — if files changed and no plan
was recorded, the user is told. See [Enforcement](#enforcement) for why it isn't a hard block.

**3. Implement.** Iterate. Ask ambiguous-design questions **one AskUserQuestion call at a time**.
Watch for `<system-reminder>` prompts arriving mid-turn and address them as they land. Every new UI
control gets an inline `x:Uid` as you create it. **On a rename, grep the old identifier repo-wide as
the last editing step, before the first build** — not as a follow-up check afterward.

**4. Interim builds — only when genuinely needed.**
```powershell
.\build.ps1 -Scratch     # compile-check without disturbing a running instance
```
Use this *only* when you must confirm something compiles while a live instance is still mid-test.
**Not reflexively before every `-Launch`.** Most changes compile clean; when a turn has one build to
do, that build is the `-Launch` one in step 6.

**5. 🔒 Commit as you go.** Commit each piece the moment it's verified — never save commits up for
the end of a testing session. A multi-part turn gets multiple commits. The turn cannot end with a
file you edited still uncommitted.

**6. 🔒 Final build + launch.** The last build of the turn, *after* the last commit, is the only one
that gets `-Launch`:
```powershell
.\build.ps1 -Launch -message="what we're testing"
```
The message is what tells the user, the instant the window appears, what they're looking at.

**7. Locale pass.** While the user tests:
```powershell
$env:PYTHONIOENCODING = "utf-8"; python tools/locadd.py
```
Derives all 7 locales from the `x:Uid`s already in the XAML. See `localization_pass.md`.

**8. Hand off.** Say plainly what to test. The turn ends here.

---

## Hard rules

**🔒 No `-testserver` unless the user asks for it in this turn** — and even then, only ever against
`-simulator`, never a real controller. Check `GET /state/lbl_connectionTarget` before any
MDI/motion/homing/reset call. Reaching for it to self-verify is what caused the 2026-07-21 incident
where MDI commands hit the user's real machine mid-homing. All live-hardware testing is the user's.

**🔒 No `git push`, no release, inside a turn.** Pushing to `v2/master` fires the release CI. That
belongs to `end_of_session_wrapup.md`, at actual session end, not to finishing a work item.

**Push remote is `v2`, never bare `origin`** — `origin` is the frozen archive
(`stevenrwood/ioSender`), `v2` is the product repo (`ioSenderV2/ioSender`).

**Don't self-verify visual changes** by launching + testserver + screenshot. Build clean and stop;
the user checks for free.

---

## Enforcement

Hooks in [.claude/settings.json](../../.claude/settings.json) run the scripts in
[tools/claude-hooks/](../../tools/claude-hooks/). They are executed by the harness, not by Claude, so
they cannot be forgotten or reasoned around.

**Gates check facts; instructions elicit behavior.** A hook can verify exactly whether the tree is
dirty, whether a `-Launch` build ran, or whether a command string contains `-testserver`. It cannot
verify that Claude sincerely restated the user's ask — the best it could do is confirm some text got
written to a file, which is satisfiable without doing the thing. So the communicative steps are
handled by injecting the checklist at the top of every turn, and only the checkable ones are gated.

| Hook | What it does |
|---|---|
| `UserPromptSubmit` | Resets turn state, records the prompt, injects this checklist into context |
| `PreToolUse` Bash/PowerShell | **Denies** `-testserver` unless this turn's prompt asked for it, and denies it without `-simulator` even then; **denies** `git push`/release unless this turn's prompt is about pushing |
| `PostToolUse` | Records edited files, commits, and `-Launch` builds into turn state |
| `Stop` | **Blocks the end of the turn** on uncommitted edits, or source edits with no `-Launch` build. Reports a skipped plan step (step 2) to the user without blocking. |

Inspect the current turn any time with `.\tools\turn.ps1 status`.

The `Stop` gate gives up after 3 consecutive blocks and lets the turn end with a loud warning — so a
wrong check can never wedge the session. If a gate is wrong, fix the script; don't work around it.

This design is deliberately measurable: if injecting the checklist is enough, the plan audit stays
quiet. If it doesn't, the audit says so, and we'll know which step actually fails before building
anything heavier.

---

## Settled — do not re-litigate

Each of these cost a round of back-and-forth to establish. They are closed.

- **`-Launch` on the final build only.** Went back and forth 2026-07-25 (never / first-of-batch);
  landed on: the truly last build of the turn gets it, interim builds never do. Don't pass it
  speculatively.
- **`build.ps1` kills a running `ioSender.exe` on *every* build**, `-Launch` or not. So an interim
  build after a launch silently leaves nothing running. Caught 2026-07-30.
- **`-NoKill` does not solve that** — it fails outright (MSB3027/MSB3021, file locked) as soon as
  there's a real rebuild. Windows won't let MSBuild overwrite a loaded DLL; skipping the kill can't
  release the OS lock.
- **`-Scratch` is the real fix** — redirects the whole solution's output to `bin\<Config>.scratch\`
  via MSBuild's `OutDir`, so an interim build never touches locked files. Implies no launch, no kill.
- **`-message=` multi-word** works: `build.ps1` quotes whitespace-containing args before joining them
  for `Start-Process` (fixed 2026-07-30). If only one word ever shows again, check that fix first.
- **`OutDir` trailing backslash** must be doubled (`\\`) — Win32's argv parser reads a lone trailing
  `\` before a closing quote as an escaped quote, silently absorbing every following MSBuild arg.
- **Commit as you go**, not saved up. Corrected 2026-07-30; the older "commit once the user is
  satisfied" framing never matched practice.
- **Never `msbuild /t:Rebuild`** — sibling `HintPath` DLL folders don't exist here, so a clean can't
  re-resolve `RP.Math`/`websocket-sharp`. Use `/t:Build`, and always pass `-restore`.
