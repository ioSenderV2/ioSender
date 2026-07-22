# Per-turn workflow loop

**When:** the default operating loop for active feature/fix work in this repo, every turn.
**Memory context:** `iosender-testserver-real-hardware-safety.md` (the hard rule in step 5),
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
2. **Iterate on the plan/implementation.** Ask questions when a design point is ambiguous (one
   sentence, per [[iosender-core-rules]]). Watch for additional informational prompts arriving
   mid-turn (they surface as a `<system-reminder>` alongside the next tool result, not a fresh
   conversational turn) - address them as they arrive rather than plowing through a stale plan.
3. **Once implementation + EN-US loc scaffolding is done** (every new control gets an inline `x:Uid`
   as you create it - cheap insurance, see `localization_pass.md`'s own rule), do the **first Debug
   build with `-Launch` and a `-message=` describing what to test**, so the user's window opens
   straight into the right context:
   ```powershell
   .\build.ps1 -Launch '-message=<what to test this run>'
   ```
   Add `-simulator` (optionally `-simulator <port>`) if the change should be smoke-tested against the
   bundled simulator before the user touches real hardware - see step 5.
4. **While the user tests, do the non-English locale `.csv` work** - `tools/locadd.py` derives ALL 7
   locales (en-US + the 6 translate-me placeholders) in one pass from the x:Uid's already in the XAML,
   so this is just running the tool now rather than earlier:
   ```powershell
   $env:PYTHONIOENCODING = "utf-8"
   python tools/locadd.py
   ```
   See `localization_pass.md` for the full rules (scope = only lines you added/changed, never hand-add
   CSV rows - extend the tool instead).
5. **Absolutely no `-testserver` unless the user explicitly requests it for this turn.** Reaching for
   it on your own initiative to "self-verify" is exactly what caused the 2026-07-21 incident. **Even
   when requested, only ever drive it against `-simulator`** - never the user's real controller target.
   Before any MDI/motion/homing/reset call through the test server, confirm the connection target is a
   loopback/simulator address first (`GET /state/lbl_connectionTarget`) - see
   `iosender-testserver-real-hardware-safety.md` for the full rule and the incident it came from. All
   live-hardware testing is the user's to run and report back on.
6. **When the user says they're satisfied testing is done, commit.** Not before - a build that
   compiles clean is not the same as "done" (see [[iosender-core-rules]]'s verify-before-claiming-done
   rule); the user's own test pass is what actually closes a turn out.
7. **Do NOT push to remote / trigger a release here.** `git push` to `v2/master` (which fires the
   rolling-release CI build) happens ONLY during `end_of_session_wrapup.md`'s own sequence, when the
   user is wrapping the whole session - not as part of finishing an individual work item.

## Ready commands

```powershell
# Step 3: build + launch with a testing message
.\build.ps1 -Launch '-message=<goal>'
# ...optionally against the simulator first:
.\build.ps1 -Launch -simulator '-message=<goal>'

# Step 4: non-English locale pass (run once implementation/EN-US strings have settled)
python tools/locadd.py

# Step 6: commit (see build_commit_test_loop.md for the fuller pre-commit build-verify shape)
git add <files>
git commit -m "..."
```

## Notes

- This supersedes `build_commit_test_loop.md`'s "commit while the user tests" timing for the commit
  step specifically - commit now waits for the user's explicit go-ahead, not just a clean Release
  build. The kill/debug-build/launch mechanics in that playbook are unchanged and still apply.
- Push/release stays entirely out of this loop - see `end_of_session_wrapup.md`.
