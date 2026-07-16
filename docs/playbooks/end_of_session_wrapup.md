# End-of-session wrap-up

**When:** work for the session is done and the user is about to `/clear`.
**Memory context:** `iosender-end-of-session-convolog.md`.

Run these **in order**. The conversation-log step is LAST because the user's next prompt is almost
always `/clear`.

## The sequence

1. **Everything committed** on `integration`.
2. **Changelog updated** — new `#N` entry in `Overview.html` + `Overview.pdf` regen.
   → [add_changelog_entry.md](add_changelog_entry.md), [regenerate_overview_pdf.md](regenerate_overview_pdf.md).
3. **Pushed all the way to remote** — `origin/integration` **and** `v2/master`. → `tools\push-all.ps1`
   (checks ahead/behind, pushes both, verifies both refs land; `-DryRun` to preview).
3.5. **Wait for the rolling-release CI build to finish** — the push to `v2/master` triggers
   `.github/workflows/release.yml` (`Rolling release`) on `ioSenderV2/ioSender`. It can fail for
   reasons nothing local catches (clean-runner build vs. a locally cached `-restore`). Run
   `tools\wait-for-release.ps1` and **wait for it to exit** before moving on - it polls until the run
   for this push's commit completes and exits 0/1 on success/failure. **If it fails, stop and
   surface the failure** (link + a look at the log) instead of writing the summary as if everything
   shipped clean - don't silently proceed to steps 5/6 on a red build.
4. **New docs published to gh-pages** (only if the manual changed).
   → [publish_manual_site.md](publish_manual_site.md).
5. **Write the end-of-session summary to chat** — the recap of what shipped (the message the user reads).
   Include the CI result from step 3.5.
6. **THEN capture the conversation log** — the `-Once` call, → [capture_conversation_log.md](capture_conversation_log.md).

## Ordering that matters (steps 5 → 6): put the summary BEFORE the capture, in the SAME message

The capture reads the session transcript from disk. Claude Code flushes the assistant message's **text**
to the transcript **before** it runs a tool call in that same message — so any text written *earlier in the
message than the `-Once` call* is already on disk and gets captured. Therefore:

- **Write the full end-of-session summary as prose first, then make the `-Once` call the LAST action of the
  same message.** The summary lands in *this* session's log, not the next run's. (Verified 2026-07-08 with a
  marker-phrase test.)
- The old flow ran the capture and *then* wrote the summary as trailing text — which pushed the summary to
  the following run. Don't do that.

## Ready command (step 3.5)

```powershell
powershell -ExecutionPolicy Bypass -File tools\wait-for-release.ps1
```

## Ready command (step 6)

```powershell
powershell -ExecutionPolicy Bypass -File tools\effort\convo-sessions.ps1 -Once
```

## Notes

- This is a one-shot at end-of-session, **not** a per-commit routine and **not** a git hook.
