# Capture the conversation log

**When:** the final step of end-of-session wrap-up (see
[end_of_session_wrapup.md](end_of_session_wrapup.md)), before the user `/clear`s.
**Memory context:** `iosender-end-of-session-convolog.md`.

Writes **this session** to a styled, self-contained, descriptively named HTML in
`%USERPROFILE%\Downloads\ClaudeConv\sessions\`, appends one record to `sessions.json`, and re-renders
`index.html`. Keeps user prompts + Claude prose only; strips tool calls, command output, diffs, thinking,
IDE/opened-file and system-reminder/slash-command noise. Screenshots you paste into a prompt are embedded
inline as `data:` URIs, so a session HTML keeps its visual context and stays self-contained (no image
folder). Images Claude viewed via the Read tool are not included (they are tool-result turns).

Filename: `<yyyy-MM-dd_HHmm>_<slug>.html` (sortable start-time prefix + slug from the session's first real
prompt), e.g. `2026-07-08_0753_so-both-cameras-working-if-do-start-recording.html`. Start/stop times,
duration, and turn count appear in both the header and the footer.

## The session boundary is THIS COMMAND (changed 2026-08-02)

Running the capture is what ends a session, so there is nothing to infer: **every turn after the previous
capture belongs to this one.** The old 60-minute idle-gap heuristic is retired.

Two things follow, and both are the point of the change:

- **Only new transcripts are read.** The checkpoint in `sessions.json` records each transcript's size at
  capture time, so an unchanged transcript is never opened. A capture now reads the ~4 files the sitting
  touched instead of all 171 (**~1 s, down from ~5 min**).
- **`sessions.json` is the durable record, and it is append-only.** Nothing re-derives history. This also
  fixes real data loss: the index used to be rebuilt from scratch from surviving transcripts, and Claude
  Code deletes those after `cleanupPeriodDays` (default 30) — so every rebuild silently dropped the
  sessions that had aged out. It was down to 157 rows against 197 HTMLs on disk before the migration
  recovered them (216 now).

## Step 0 — verify committed + pushed (gate)

Run this FIRST. Capturing is the last thing before `/clear`, so don't do it on top of uncommitted or
unpushed work. Read-only; it never commits or pushes.

```powershell
powershell -ExecutionPolicy Bypass -File tools\verify-pushed.ps1
```

Mirrors `push-all.ps1`'s remote model (origin/integration + v2/master), fetches both (best-effort),
and exits **1** listing anything dirty/unpushed, **0** when clean and in sync. If it fails: commit the
outstanding changes, run `tools\push-all.ps1`, then re-run the gate. Only proceed to the Ready command
once it prints `OK  clean + pushed`.

## Ready command

```powershell
powershell -ExecutionPolicy Bypass -File tools\effort\convo-sessions.ps1
```

That's the whole step — it re-renders `index.html` itself. You no longer run `build-session-index.ps1`
after it. (`-Once` is still accepted and ignored, so the old muscle-memory command still works.)

## Modes

- *(no switch)* — capture everything since the last checkpoint as one session. **This is the step.**
- `-Amend` — ran it too early and kept working? Folds the extra turns into the session just written,
  keeping its filename so the index link doesn't move.
- `-WhatIfOnly` — report the window, turn count and filename that would be written; write nothing.
- `-IncludeThinking` — also include Claude's internal thinking blocks (off by default).

## Ordering: write the summary FIRST, then call the capture last in the same message

Claude Code flushes an assistant message's text to the transcript **before** running a tool call in that
same message, so text written earlier in the message than the capture call is already on disk and gets
captured. So write the end-of-session summary as prose, then make the capture the final action of the
same message — the summary lands in *this* session's log (verified 2026-07-08). Don't run the capture and
then write the summary as trailing text; that pushes the summary to the next run.

## Supporting scripts

- **`build-session-index.ps1`** — re-renders `index.html` from `sessions.json` alone (no transcripts, <1 s).
  Only needed after changing the table's styling/columns or hand-editing the manifest.
- **`migrate-session-manifest.ps1`** — the **one-time** seed, already run on 2026-08-02. It is the only
  thing that still uses the 60-minute heuristic, and only for sessions that predate the boundary rule.
  Don't run it again; `-Force` would rebuild the manifest from scratch.
- **`convo-logger.ps1`** — the original one-transcript-per-file logger. Superseded, kept for reference.

## Notes

- Source transcripts: `%USERPROFILE%\.claude\projects\c--github-ioSender\*.jsonl`.
- Transcript retention: Claude Code auto-deletes `.jsonl` older than `cleanupPeriodDays` (**default 30**),
  set in `~/.claude/settings.json`. `sessions.json` + the `sessions\` HTMLs are the durable archive past
  that window — **a session not captured at wrap-up is not recoverable later.**
- `sessions.json` is rewritten with a rolling `.bak` on every capture.
