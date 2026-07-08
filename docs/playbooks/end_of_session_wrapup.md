# End-of-session wrap-up

**When:** work for the session is done and the user is about to `/clear`.
**Memory context:** `iosender-end-of-session-convolog.md`.

Run these **in order**. The conversation-log step is LAST because the user's next prompt is almost
always `/clear`.

## The sequence

1. **Everything committed** on `integration`.
2. **Changelog updated** — new `#N` entry in `Overview.html` + `Overview.pdf` regen.
   → [add_changelog_entry.md](add_changelog_entry.md), [regenerate_overview_pdf.md](regenerate_overview_pdf.md).
3. **Pushed all the way to remote** — `origin/integration` **and** `v2/master`.
4. **New docs published to gh-pages** (only if the manual changed).
   → [publish_manual_site.md](publish_manual_site.md).
5. **THEN capture the conversation log** (the final action):
   → [capture_conversation_log.md](capture_conversation_log.md).

## Ready command (step 5)

```powershell
powershell -ExecutionPolicy Bypass -File tools\effort\convo-logger.ps1 -Once
```

## Notes

- This is a one-shot at end-of-session, **not** a per-commit routine and **not** a git hook.
- `-Once` captures through the last *completed* turn, so the very final wrap-up message may land on
  the next run — acceptable.
