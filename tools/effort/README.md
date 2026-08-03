# Effort tracking

Tracks **your hours** (real keyboard/mouse activity) and **my (Claude) tokens** — especially tokens spent
**autonomously while you're away**. Nothing here is committed yet; review and move/commit as you like.

## Your hours — `effort-tracker.ps1`
Run it once and leave it in the background:

```
powershell -ExecutionPolicy Bypass -File tools\effort\effort-tracker.ps1
```

- A **session** = active time; it **ends after >5 min of keyboard/mouse inactivity**, and the next input
  starts a new one. (Tune with `-IdleGapMinutes 5`.)
- Polls every 20 s (`-PollSeconds`), so ~20 s granularity. **Any** input counts — any app (ioSender, Fusion,
  the editor, this CLI).
- Completed sessions append to `sessions.csv` (`start,end,minutes`). The in-progress session is mirrored to
  `sessions.current` each poll, so a crash / reboot / Ctrl+C still captures it (finalised on the next run).
- Auto-start at login: Task Scheduler → "At log on" → the command above (or a shortcut in `shell:startup`).

## Our conversation — `convo-sessions.ps1`
Logs the **Claude Code conversation** (your prompts + my prose replies) to a per-session **HTML** file, with
all the noise stripped — tool calls, command output, file diffs, and my internal "thinking". Markdown is
lightly rendered (fenced code blocks, inline code, bold, headings). Pasted screenshots are embedded as
`data:` URIs so each file stays self-contained. Source is the session transcript JSONL the CLI writes under
`%USERPROFILE%\.claude\projects\<project>\<guid>.jsonl`.

```
# The end-of-session capture step - the whole thing:
powershell -ExecutionPolicy Bypass -File tools\effort\convo-sessions.ps1
```

**The session boundary is that command.** Running it ends a session, so everything logged since the previous
run is this one — no idle-gap guessing. Output goes to `%USERPROFILE%\Downloads\ClaudeConv` (`-OutDir` to
change): `sessions\<yyyy-MM-dd_HHmm>_<slug>.html` plus a record appended to `sessions.json` and a re-rendered
`index.html`.

- **`sessions.json` is the durable archive.** Claude Code deletes transcripts after `cleanupPeriodDays`
  (default 30), so a session not captured at wrap-up can't be recovered. The manifest is append-only —
  records never fall off it.
- **Incremental.** The checkpoint stores each transcript's size, so unchanged transcripts are never opened:
  ~4 files instead of 171, **~1 s instead of ~5 min**.
- `-Amend` folds later turns into the session just written (same filename). `-WhatIfOnly` previews.
  `-IncludeThinking` adds my thinking blocks. `-Once` is accepted and ignored (old habit still works).

### `build-session-index.ps1`
Re-renders `index.html` from `sessions.json` alone — no transcripts, instant. `convo-sessions.ps1` already
does this on every capture, so you only need it after changing the table's columns/styling.

### `migrate-session-manifest.ps1`
**One-time**, already run 2026-08-02. Seeded the manifest with 216 sessions (2026-06-07 →) from three
sources: surviving transcripts (exact), each session HTML's own footer (exact, and how 59 sessions that had
already dropped off the index were recovered), and the old `index.html` (rounded, last resort). The retired
60-minute gap heuristic survives only in here, only for sessions predating the boundary rule. Don't re-run.

### `convo-logger.ps1`
The original one-transcript-per-file logger (`<guid>.html`, the CLI's own boundaries). Superseded by
`convo-sessions.ps1`; kept for reference.

## My tokens
I can't read my own token usage in-conversation — for the **exact** number, run **`/cost`** in Claude Code.
I log each **autonomous stint** (work done while you're away) in [`EFFORT-LOG.md`](EFFORT-LOG.md) so you can
attribute the session cost to "while away" vs "together".

## Assumptions / open questions (confirm when back)
- **Session boundary = >5 min inactivity** (your clarification). ✓ implemented.
- **Storage:** kept in `tools/effort/` and the live data (`sessions.csv`, `sessions.current`) is gitignored so
  it doesn't churn the repo. Move the whole folder out of the repo if you'd rather it not live here.
- **Tokens:** exact via `/cost`; want me to also **estimate per stint** (rough, from tool-call / build counts)?
- Want a **roll-up** (`summarize.ps1` → hours/day, hours this week, total)? Quick to add.
- Should hours + token stints feed the **Overview "Effort" section** docs, or stay a private log?
