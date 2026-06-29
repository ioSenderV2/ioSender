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
