# Capture the conversation log

**When:** the final step of end-of-session wrap-up (see
[end_of_session_wrapup.md](end_of_session_wrapup.md)), before the user `/clear`s.
**Memory context:** `iosender-end-of-session-convolog.md`.

Pools **every** Claude Code transcript for this project into one chronological stream, re-cuts it into
sessions on idle-time gaps, and writes the **current** session to a styled, self-contained, descriptively
**named** HTML in `%USERPROFILE%\Downloads\ClaudeConv\sessions\`. Keeps user prompts + Claude prose only;
strips tool calls, command output, diffs, thinking, IDE/opened-file and system-reminder/slash-command noise.

Filename: `<yyyy-MM-dd_HHmm>_<slug>.html` (sortable start-time prefix + slug from the session's first real
prompt), e.g. `2026-07-08_0753_so-both-cameras-working-if-do-start-recording.html`. Start/stop times,
duration, and turn count appear in both the header and the footer.

## Ready command

```powershell
powershell -ExecutionPolicy Bypass -File tools\effort\convo-sessions.ps1 -Once
```

## Why this script (not convo-logger.ps1)

`convo-logger.ps1` maps one *transcript file* → one `<guid>.html` (the CLI's own session boundaries).
`convo-sessions.ps1` ignores those: it **merges all transcripts** and re-splits on a time gap, so a `/clear`
that started a fresh transcript minutes later stays ONE session, and a transcript left open across an
overnight break splits into two. This is the official capture step now.

## Modes

- `-Once` — merge/split everything, write **only the most-recent (current) session**, then exit. **This is the step.** Always regenerates the current sitting fresh (idempotent, deterministic name).
- (no switch) — rebuild **all** detected sessions to their own named HTML files.
- `-Analyze` — print the inter-entry gap distribution + session counts at several thresholds; write nothing.
- `-SessionGapMinutes N` — idle gap (minutes) that starts a new session (**default 60**).

## Notes

- Source transcripts: `%USERPROFILE%\.claude\projects\c--github-ioSender\*.jsonl`.
- Boundary default 60 min was picked from the observed gap distribution (99% of inter-entry gaps < ~9 min;
  the real breaks cluster at 1 hr+). Re-tune with `-Analyze` / `-SessionGapMinutes`.
- Transcript retention: Claude Code auto-deletes `.jsonl` older than `cleanupPeriodDays` (**default 30**),
  set in `~/.claude/settings.json`. These `sessions\` HTMLs are the durable archive past that window.
