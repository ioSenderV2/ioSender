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

## Our conversation — `convo-logger.ps1`
Logs the **Claude Code conversation** (your prompts + my prose replies) to a per-session **HTML** file, with
all the noise stripped — tool calls, command output, file diffs, and my internal "thinking". Markdown is
lightly rendered (fenced code blocks, inline code, bold, headings). Source is the session transcript JSONL
the CLI writes under `%USERPROFILE%\.claude\projects\<project>\<guid>.jsonl`.

```
# Regenerate the CURRENT session (the end-of-session capture step):
powershell -ExecutionPolicy Bypass -File tools\effort\convo-logger.ps1 -Once

# Regenerate every PAST transcript once (one .html per session):
powershell -ExecutionPolicy Bypass -File tools\effort\convo-logger.ps1 -All

# FOLLOW the live session in the background (like effort-tracker):
powershell -ExecutionPolicy Bypass -File tools\effort\convo-logger.ps1
```

- Output folder defaults to `%USERPROFILE%\Downloads\ClaudeConv` (`-OutDir` to change); one styled, self-
  contained `<session-guid>.html` per session (light/dark aware). Each run regenerates the target session's
  HTML in full from the transcript, so it's always idempotent — no partial-append state.
- Keeps only `type=user` prompts (minus `isMeta` / tool-result turns and slash-command wrappers) and the
  `text` blocks of `type=assistant`. `-IncludeThinking` adds my thinking blocks if you want them.
- `-Once` is the intended end-of-session step (run it after everything is committed/pushed/published, just
  before you `/clear`). `-PollSeconds` (default 5) only matters in follow mode.

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
