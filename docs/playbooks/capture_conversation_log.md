# Capture the conversation log

**When:** the final step of end-of-session wrap-up (see
[end_of_session_wrapup.md](end_of_session_wrapup.md)), before the user `/clear`s.
**Memory context:** `iosender-end-of-session-convolog.md`.

Converts the Claude Code session transcript JSONL into a styled, self-contained per-session **`.html`**
in `%USERPROFILE%\Downloads\ClaudeConv\<guid>.html`. Keeps user prompts + Claude prose only; strips
tool calls, command output, diffs, thinking, and system-reminder/slash-command noise.

## Ready command

```powershell
powershell -ExecutionPolicy Bypass -File tools\effort\convo-logger.ps1 -Once
```

## Modes

- `-Once` — regenerate the CURRENT (newest) session's HTML in full, then exit. **This is the step.**
- `-All` — rebuild every past transcript to its own HTML.
- (no switch) — background follow-loop (regenerates on a poll interval).

## Notes

- Source transcript: `%USERPROFILE%\.claude\projects\c--github-ioSender\<guid>.jsonl`.
- Idempotent (each run rebuilds the whole file — no partial-append state).
