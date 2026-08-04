# Capture the conversation log

> **Extends: `claude-hub/playbooks/capture_conversation_log.md`** — read that for the whole
> procedure (what gets captured, how the session boundary works, the modes, the ordering rule).
> Only the ioSender-specific bits are below.

**When:** the final step of end-of-session wrap-up ([end_of_session_wrapup.md](end_of_session_wrapup.md)),
before the user `/clear`s.
**Memory context:** `iosender-end-of-session-convolog.md`.

## Ready command

```powershell
powershell -ExecutionPolicy Bypass -File c:\github\claude-hub\tools\convo-sessions.ps1
```

`-Project` defaults to `ioSender`, so there is nothing else to pass. It writes into
`claude-hub\conversations\ioSender\`, re-renders that project's index plus the cross-project
roll-up, and commits itself into `claude-hub` (no push).

## Step 0 — verify committed + pushed (gate)

Run this FIRST, in **this** repo. Capturing is the last thing before `/clear`, so don't do it on
top of uncommitted or unpushed work. Read-only.

```powershell
powershell -ExecutionPolicy Bypass -File tools\verify-pushed.ps1
```

Mirrors `push-all.ps1`'s remote model (origin/integration + v2/master) and exits **1** listing
anything dirty/unpushed. If it fails: commit, run `tools\push-all.ps1`, re-run the gate. Only
proceed once it prints `OK  clean + pushed`.

## What moved (2026-08-03)

The tooling used to live at `tools/effort/` in this repo and write to
`%USERPROFILE%\Downloads\ClaudeConv`. Both are gone:

- Tools → `claude-hub/tools/`. There is no copy here; there is deliberately only one.
- Conversations → `claude-hub/conversations/ioSender/`, now versioned — the session HTMLs too,
  not just the manifest, since an aged-out transcript makes the HTML equally irreplaceable.
- The in-repo manifest **mirror** (`tools/effort/sessions.json`) is gone along with the mechanism
  that needed it. Nothing to commit *here* on capture any more.
- Effort-tracker data (`sessions.csv`) → `claude-hub/effort/`.

Per-project settings now live in `claude-hub/conversations/ioSender/sessions.json` under `config`
— that one file carries the config, the checkpoint and every session record.
