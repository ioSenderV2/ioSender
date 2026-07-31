# Per-turn workflow loop

**When:** the default operating loop for active feature/fix work in this repo, every turn.
**Memory context:** `iosender-testserver-real-hardware-safety.md` (the hard rule in step 6),
`iosender-turn-workflow-loop.md`.
**Why:** adopted 2026-07-21 after a same-session incident where `-testserver` MDI/motion commands
were sent to the user's REAL controller mid-testing (see the memory note above) - this loop puts a
hard boundary between "Claude iterates/builds" and "user tests real hardware", and moves the
push-to-master/release trigger out of the loop entirely (that's `end_of_session_wrapup.md`'s job, not
a per-turn thing).

## The loop

1. **Every turn starts from a user prompt** - a new work item, feedback, or test results from either
   the user's own hardware/LAN testing or a testing pass they're doing on the current work item. Don't
   self-initiate new work or decide unilaterally that something needs testing next.
2. **When the prompt asks for a change or describes a new feature, state understanding + plan BEFORE
   touching any file.** Investigation is fine first (grep/read to figure out what's actually being
   asked, or to scope the change) - but Edit/Write don't happen until the restated understanding and
   the intended approach have been put in front of the user. This is a hard gate, not a formality:
   don't shear off into implementing right after the user describes something, even if the fix seems
   obvious - present the plan, let them confirm or redirect, then implement. (Corrected 2026-07-30 -
   user called out that jumping straight to changes without confirming understanding first is exactly
   the failure mode this step exists to prevent.)
3. **Iterate on the plan/implementation.** Ask questions when a design point is ambiguous (one
   sentence, per [[iosender-core-rules]]). Watch for additional informational prompts arriving
   mid-turn (they surface as a `<system-reminder>` alongside the next tool result, not a fresh
   conversational turn) - address them as they arrive rather than plowing through a stale plan.
4. **Once implementation + EN-US loc scaffolding is done** (every new control gets an inline `x:Uid`
   as you create it - cheap insurance, see `localization_pass.md`'s own rule): **any time there's a
   change that needs testing, build with `-Launch`.** No exceptions, no "first build of a batch"
   judgment call, doesn't matter whether anything's committed yet - if the change needs eyes on it,
   build+launch. The FIRST test build of a finished set of changes should go straight to
   `-Launch -message="..."` describing what's being tested - not a plain verify-it-compiles build
   followed by a second build+launch once that's confirmed. The message is what tells the user, the
   instant the window comes up, what they're looking at:
   ```powershell
   .\build.ps1 -Launch -message="Countersink bit: 4 sizes, plunge chamfer"
   ```
   (`-message=` is an ioSender.exe startup arg, not a build.ps1 parameter - it rides through via
   build.ps1's `$AppArgs` passthrough, forwarded verbatim to the launched exe. A multi-word message
   needs build.ps1's own quoting fix from 2026-07-30 - see Notes - to survive as one argv token.)
   Corrected 2026-07-25 (twice) and 2026-07-30 (the -message addition) - simplify to this one rule;
   stop trying to guess whether a relaunch would be disruptive, and stop burning a throwaway
   compile-only build before the one that actually puts something in front of the user.
5. **Commit as you go, right after each verified change** - NOT gated behind the user declaring the
   whole testing session done. In practice this means: implement -> build (verify it compiles, and if
   it's the first test build of this change, `-Launch` per step 4) -> commit -> move to the next
   change or wait for feedback. A multi-part turn (several distinct fixes) gets several commits, each
   right after its own change is verified, not one commit saved up for the end. Corrected 2026-07-30 -
   the older "commit only once the user says they're satisfied" framing didn't match actual practice
   and the user called it out directly.
6. **While the user tests, do the non-English locale `.csv` work** - `tools/locadd.py` derives ALL 7
   locales (en-US + the 6 translate-me placeholders) in one pass from the x:Uid's already in the XAML,
   so this is just running the tool now rather than earlier:
   ```powershell
   $env:PYTHONIOENCODING = "utf-8"
   python tools/locadd.py
   ```
   See `localization_pass.md` for the full rules (scope = only lines you added/changed, never hand-add
   CSV rows - extend the tool instead).
7. **Absolutely no `-testserver` unless the user explicitly requests it for this turn.** Reaching for
   it on your own initiative to "self-verify" is exactly what caused the 2026-07-21 incident. **Even
   when requested, only ever drive it against `-simulator`** - never the user's real controller target.
   Before any MDI/motion/homing/reset call through the test server, confirm the connection target is a
   loopback/simulator address first (`GET /state/lbl_connectionTarget`) - see
   `iosender-testserver-real-hardware-safety.md` for the full rule and the incident it came from. All
   live-hardware testing is the user's to run and report back on.
8. **Do NOT push to remote / trigger a release here.** `git push` to `v2/master` (which fires the
   rolling-release CI build) happens ONLY during `end_of_session_wrapup.md`'s own sequence, when the
   user is wrapping the whole session - not as part of finishing an individual work item.

## Ready commands

```powershell
# Step 4: any change that needs testing - build + launch with a message, every time
.\build.ps1 -Launch -message="what we're testing"

# Step 5: commit right after that build/verification succeeds - don't save it up
git add <files>
git commit -m "..."

# Step 6: non-English locale pass (run once implementation/EN-US strings have settled)
python tools/locadd.py
```

## Notes

- Push/release stays entirely out of this loop - see `end_of_session_wrapup.md`.
- 2026-07-25: went back and forth on when `-Launch` is warranted (never / first-build-of-a-batch-only)
  before landing on the simple rule in step 4 - any change that needs testing gets built with
  `-Launch`, full stop. Don't re-litigate this.
- 2026-07-30: added step 2 (state understanding + plan before touching files on a change/feature
  request), the `-message=` addition to step 4, and corrected step 5's commit timing to match actual
  practice (commit as you go, not saved up for an end-of-testing signal) - all direct user
  corrections, not refinements to re-litigate.
- 2026-07-30: fixed a real bug in `build.ps1` itself - `Start-Process -ArgumentList` joins array
  elements with a bare space and does NOT re-quote ones containing whitespace, so a multi-word
  `-message="..."` value was silently re-split into separate argv entries by the time it reached
  ioSender.exe, and only the first word ever landed on `-message=`. Fixed by quoting any argument
  containing whitespace before the join. If `-message` ever appears to show only one word again,
  check this fix hasn't regressed before assuming it's a usage error.
